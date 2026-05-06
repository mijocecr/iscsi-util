using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    //  🔥 INFRAESTRUCTURA DE TRAZAS
    // ============================================================

    private static long _traceCounter = 0;
    private static long NextTraceId() => ++_traceCounter;

    private static Stopwatch StartTrace(long id, string method, string details = "")
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[ISCSI] #{id} → {method} {details}");
        return sw;
    }

    private static void EndTrace(long id, string method, Stopwatch sw, string result = "OK")
    {
        sw.Stop();
        Console.WriteLine($"[ISCSI] #{id} ← {method} [{result}] en {sw.ElapsedMilliseconds} ms");
    }

    private static void Log(long id, string message)
    {
        Console.WriteLine($"[ISCSI] #{id} {message}");
    }

    // ============================================================
    //  🔥 SANITIZAR NOMBRE PARA ARCHIVOS Y SYSTEMD
    // ============================================================

    public static string SanitizarNombre(string iqn)
    {
        char[] invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', ' ' })
            .ToArray();

        return new string(iqn.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string SystemdSafe(string s)
    {
        return s.Replace(":", "_")
                .Replace(".", "_")
                .Replace("-", "_")
                .Replace("/", "_");
    }

    // ============================================================
    //  🔥 BASE DE MONTADO EN ESPACIO DE USUARIO
    // ============================================================

    private static string GetUserIscsiBase()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string basePath = Path.Combine(home, ".local", "share", "iscsi");

        if (!Directory.Exists(basePath))
            Directory.CreateDirectory(basePath);

        return basePath;
    }

    // ============================================================
    //  🔥 DETECTAR FILESYSTEM
    // ============================================================

    private static string DetectarFsType(string blkidOut)
    {
        if (blkidOut.Contains("TYPE=\"ext2\"")) return "ext2";
        if (blkidOut.Contains("TYPE=\"ext3\"")) return "ext3";
        if (blkidOut.Contains("TYPE=\"ext4\"")) return "ext4";
        if (blkidOut.Contains("TYPE=\"xfs\"")) return "xfs";
        if (blkidOut.Contains("TYPE=\"btrfs\"")) return "btrfs";
        if (blkidOut.Contains("TYPE=\"f2fs\"")) return "f2fs";
        if (blkidOut.Contains("TYPE=\"ntfs\"")) return "ntfs";
        if (blkidOut.Contains("TYPE=\"vfat\"")) return "vfat";
        if (blkidOut.Contains("TYPE=\"exfat\"")) return "exfat";
        if (blkidOut.Contains("TYPE=\"iso9660\"")) return "iso9660";
        return "ext4";
    }

    // ======================================================================
    //  DISCOVER — Descubrir destinos iSCSI en un portal
    // ======================================================================
    public static async Task<List<IscsiDestino>> Descubrir(string ip)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, "Descubrir", $"IP='{ip}'");

        var destinos = new List<IscsiDestino>();

        try
        {
            Log(id, "Ejecutando discovery...");
            var discovery = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discovery -t sendtargets -p {ip}"
            );

            if (string.IsNullOrWhiteSpace(discovery.Stdout))
            {
                Log(id, "No se encontraron destinos.");
                EndTrace(id, "Descubrir", sw, "EMPTY");
                return destinos;
            }

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Log(id, $"Procesando línea: '{line}'");

                if (!line.Contains("iqn.")) continue;

                string iqn = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(s => s.StartsWith("iqn."));

                if (string.IsNullOrWhiteSpace(iqn))
                    continue;

                bool conectado = sesiones.Contains(iqn);

                if (destinos.Any(d => d.Iqn == iqn && d.Ip == ip))
                    continue;

                var d = new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                };

                destinos.Add(d);
            }

            foreach (var d in destinos.Where(x => x.Conectado))
            {
                Log(id, $"Completando información para {d.Iqn}...");
                await CompletarInformacionDestino(d, id);
            }

            EndTrace(id, "Descubrir", sw, $"OK ({destinos.Count} destinos)");
            return destinos;
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] Descubrir: {ex.Message}");
            EndTrace(id, "Descubrir", sw, "ERROR");
            return destinos;
        }
    }

    // ======================================================================
    //  COMPLETAR INFORMACIÓN — DevicePath, PartitionPath, MountPoint, FS
    // ======================================================================
    private static async Task CompletarInformacionDestino(IscsiDestino d, long parentId)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, "CompletarInformacion", $"IQN='{d.Iqn}'");

        try
        {
            var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var match = byPath.FirstOrDefault(l =>
                l.Contains(d.Ip) && l.Contains("lun")
            );

            if (match != null)
            {
                d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                Log(id, $"DevicePath='{d.DevicePath}'");
            }
            else
            {
                Log(id, "No se encontró DevicePath.");
                EndTrace(id, "CompletarInformacion", sw, "NO_DEVICE");
                return;
            }

            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            Log(id, $"PartitionPath='{d.PartitionPath}'");

            var mounts = ShellHelper.EjecutarComoRoot("mount").Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var mline = mounts.FirstOrDefault(l => l.Contains(d.PartitionPath));

            if (mline != null)
            {
                var parts = mline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    d.MountPoint = parts[2];
                    Log(id, $"MountPoint detectado='{d.MountPoint}'");
                }
            }

            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");

            d.TieneFilesystem =
                !string.IsNullOrWhiteSpace(blkid.Stdout) &&
                blkid.Stdout.Contains("TYPE=");

            if (d.TieneFilesystem)
            {
                d.FsType = DetectarFsType(blkid.Stdout);
                Log(id, $"FsType detectado='{d.FsType}'");
            }

            Log(id, $"TieneFilesystem={d.TieneFilesystem}");

            EndTrace(id, "CompletarInformacion", sw);
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] CompletarInformacion: {ex.Message}");
            EndTrace(id, "CompletarInformacion", sw, "ERROR");
        }
    }

    // ======================================================================
    //  CONECTAR — Montaje avanzado, robusto y con instrumentación
    // ======================================================================
    public static async Task Conectar(IscsiDestino d)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, "Conectar", $"IQN='{d.Iqn}', IP='{d.Ip}'");

        try
        {
            string userBase = GetUserIscsiBase();
            d.MountPoint = Path.Combine(userBase, SanitizarNombre(d.Iqn));

            Log(id, $"MountPoint='{d.MountPoint}'");

            if (Directory.Exists(d.MountPoint))
            {
                try { Directory.GetFileSystemEntries(d.MountPoint); }
                catch
                {
                    Log(id, "MountPoint corrupto → limpiando...");
                    ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
                }
            }

            if (!Directory.Exists(d.MountPoint))
                Directory.CreateDirectory(d.MountPoint);

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn);

            Log(id, $"yaConectado={yaConectado}");

            if (!yaConectado)
            {
                Log(id, "Buscando portal válido para este IQN...");

                var discovery = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m discovery -t sendtargets -p {d.Ip}"
                );

                var portals = new List<string>();

                foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains(d.Iqn))
                    {
                        string portal = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        portal = portal.Split(',')[0];
                        portals.Add(portal);
                    }
                }

                if (portals.Count == 0)
                    throw new Exception("No se encontraron portales para este IQN.");

                string? portalValido = null;

                foreach (var portal in portals)
                {
                    Log(id, $"Probando portal {portal}...");

                    var result = ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {portal}"
                    );

                    if (!result.Stderr.Contains("No records found"))
                    {
                        portalValido = portal;
                        break;
                    }
                }

                if (portalValido == null)
                    throw new Exception("No se encontró ningún portal válido para este destino.");

                d.Ip = portalValido;
                Log(id, $"Portal válido seleccionado: {d.Ip}");

                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=new"
                );

                if (d.UsaChap || d.UsaMutualChap)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.authmethod --value=CHAP");

                    if (d.UsaChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username --value={d.UsuarioChap}");

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password --value={d.PasswordChap}");
                    }

                    if (d.UsaMutualChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username_in --value={d.UsuarioMutualChap}");

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password_in --value={d.PasswordMutualChap}");
                    }
                }

                Log(id, $"Realizando login iSCSI en portal {d.Ip}...");
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --login"
                );
            }

            d.DevicePath = null;

            for (int i = 0; i < 10; i++)
            {
                var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                var match = byPath.FirstOrDefault(l =>
                    l.Contains(d.Ip) && l.Contains("lun")
                );

                if (match != null)
                {
                    d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    Log(id, $"DevicePath='{d.DevicePath}'");
                    break;
                }

                await Task.Delay(200);
            }

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception($"No se encontró symlink para {d.Iqn}");

            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            Log(id, $"PartitionPath='{d.PartitionPath}'");

            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                Log(id, "No se detectó filesystem → destino sin formatear.");
                d.TieneFilesystem = false;
                d.Conectado = true;
                EndTrace(id, "Conectar", sw, "NO_FS");
                return;
            }

            d.TieneFilesystem = true;
            string fsType = DetectarFsType(blkid.Stdout);
            d.FsType = fsType;

            Log(id, $"Filesystem detectado='{fsType}'");

            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                Log(id, "Montando volumen...");
                ShellHelper.EjecutarComoRoot(
                    $"mount -t {fsType} {d.PartitionPath} \"{d.MountPoint}\"");
            }
            else
            {
                Log(id, "Ya estaba montado.");
            }

            string testFile = null;

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var files = Directory.GetFileSystemEntries(d.MountPoint);
                    if (files.Length > 0)
                    {
                        testFile = files[0];
                        break;
                    }
                }
                catch { }

                await Task.Delay(200);
            }

            if (testFile == null)
                testFile = d.MountPoint + "/.";

            var owner = ShellHelper.EjecutarComoRoot($"stat -c %u:%g \"{testFile}\"").Stdout.Trim();

            if (owner == "0:0")
            {
                int uid = int.Parse(ShellHelper.EjecutarComoRoot("id -u").Stdout.Trim());
                int gid = int.Parse(ShellHelper.EjecutarComoRoot("id -g").Stdout.Trim());

                Log(id, "Reparando permisos root:root...");
                ShellHelper.EjecutarComoRoot($"chown -R {uid}:{gid} \"{d.MountPoint}\"");
            }

            d.Conectado = true;
            NotificadorLinux.Enviar($"Destino {d.Iqn} montado en {d.MountPoint}");

            EndTrace(id, "Conectar", sw, "OK");
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Fallo al conectar destino {d.Iqn}");
            EndTrace(id, "Conectar", sw, "ERROR");
        }
    }

    // ======================================================================
    //  DESCONECTAR — desmontaje avanzado, limpieza real e instrumentación
    // ======================================================================
    public static async Task Desconectar(IscsiDestino d)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, "Desconectar", $"IQN='{d.Iqn}', MP='{d.MountPoint}'");

        try
        {
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

                if (mpCheck.ExitCode == 0)
                {
                    Log(id, "Desmontando volumen...");
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }

                mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    Log(id, "[WARN] El volumen sigue montado tras umount -l");
                }
            }

            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                try
                {
                    Log(id, "Eliminando directorio de montaje...");
                    ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
    
                    }
                catch (Exception ex)
                {
                    Log(id, $"[WARN] No se pudo eliminar el directorio: {ex.Message}");
                }
            }

            // --------------------------------------------------------------
            // 3) Cerrar sesión iSCSI
            // --------------------------------------------------------------
            Log(id, "Cerrando sesión iSCSI...");
            ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout");

            await Task.Delay(300);

            // --------------------------------------------------------------
            // 4) Eliminar nodo iSCSI
            // --------------------------------------------------------------
            Log(id, "Eliminando nodo iSCSI...");
            ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete");

            // --------------------------------------------------------------
            // 5) Reset de propiedades del destino
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;

            NotificadorLinux.Enviar($"Destino {d.Iqn} desconectado");

            EndTrace(id, "Desconectar", sw, "OK");
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Fallo al desconectar destino {d.Iqn}");
            EndTrace(id, "Desconectar", sw, "ERROR");
        }
    }

    // ======================================================================
    //  PERSISTENCIA — fstab + systemd
    // ======================================================================
    public static void AplicarPersistencia(IscsiDestino d)
    {
        long id = NextTraceId();
        var sw = StartTrace(id, "AplicarPersistencia", $"IQN='{d.Iqn}', Persistir={d.Persistir}");

        try
        {
            if (d.Persistir)
            {
                GuardarEnFstab(d, id);
                CrearServicioLogin(d, id);
                CrearMountUnit(d, d.FsType, id);
                HabilitarServicios(id);
            }
            else
            {
                EliminarDeFstab(d, id);
                EliminarServicioSystemd(d, id);
                EliminarMountUnit(d, id);
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            }

            EndTrace(id, "AplicarPersistencia", sw, "OK");
        }
        catch (Exception ex)
        {
            Log(id, $"[ERROR] Persistencia: {ex.Message}");
            EndTrace(id, "AplicarPersistencia", sw, "ERROR");
        }
    }

    // ======================================================================
    //  FSTAB — Guardar entrada persistente
    // ======================================================================
    private static void GuardarEnFstab(IscsiDestino d, long id)
    {
        if (!d.TieneFilesystem || string.IsNullOrWhiteSpace(d.MountPoint))
        {
            Log(id, "No se puede persistir: no hay filesystem o mountpoint.");
            return;
        }

        string entry = $"{d.PartitionPath} {d.MountPoint} auto _netdev 0 0";

        Log(id, $"Añadiendo entrada a fstab: {entry}");

        ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.PartitionPath}#d\" /etc/fstab");
        ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.MountPoint}#d\" /etc/fstab");

        ShellHelper.EjecutarComoRoot(
            $"sh -c \"echo '{entry}' >> /etc/fstab\""
        );
    }

    // ======================================================================
    //  FSTAB — Eliminar entrada persistente
    // ======================================================================
    private static void EliminarDeFstab(IscsiDestino d, long id)
    {
        if (string.IsNullOrWhiteSpace(d.MountPoint))
            return;

        Log(id, $"Eliminando entrada de fstab para {d.MountPoint}");

        ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.MountPoint}#d\" /etc/fstab");
        ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.PartitionPath}#d\" /etc/fstab");
    }

    // ======================================================================
    //  SYSTEMD — Crear servicio de login iSCSI
    // ======================================================================
    private static void CrearServicioLogin(IscsiDestino d, long id)
    {
        string safe = SystemdSafe(d.Iqn);
        string path = $"/etc/systemd/system/iscsi-login-{safe}.service";

        string contenido =
$@"[Unit]
Description=Login iSCSI for {d.Iqn}
After=network-online.target iscsid.service
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/bin/iscsiadm -m node -T {d.Iqn} -p {d.Ip} --login
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

        ShellHelper.EjecutarComoRoot($"sh -c \"echo '{contenido.Replace("'", "'\\''")}' > {path}\"");
        ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
        ShellHelper.EjecutarComoRoot($"systemctl enable iscsi-login-{safe}.service");

        Log(id, $"Servicio creado: {path}");
    }

    // ======================================================================
    //  SYSTEMD — Crear unidad .mount
    // ======================================================================
    private static void CrearMountUnit(IscsiDestino d, string fsType, long id)
    {
        string safeMount = d.MountPoint
            .Trim('/')
            .Replace("/", "-")
            .Replace(".", "_");

        string unitName = $"{safeMount}.mount";
        string path = $"/etc/systemd/system/{unitName}";

        string contenido =
$@"[Unit]
Description=Mount iSCSI volume {d.Iqn}
After=iscsi-login-{SystemdSafe(d.Iqn)}.service

[Mount]
What={d.PartitionPath}
Where={d.MountPoint}
Type={fsType}
Options=_netdev

[Install]
WantedBy=multi-user.target
";

        ShellHelper.EjecutarComoRoot($"sh -c \"echo '{contenido.Replace("'", "'\\''")}' > {path}\"");
        ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
        ShellHelper.EjecutarComoRoot($"systemctl enable {unitName}");

        Log(id, $"Mount unit creada: {path}");
    }

    // ======================================================================
    //  SYSTEMD — Eliminar servicio
    // ======================================================================
    private static void EliminarServicioSystemd(IscsiDestino d, long id)
    {
        string safe = SystemdSafe(d.Iqn);
        string service = $"/etc/systemd/system/iscsi-login-{safe}.service";

        if (File.Exists(service))
        {
            ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-login-{safe}.service");
            ShellHelper.EjecutarComoRoot($"rm -f {service}");
            Log(id, $"Servicio eliminado: {service}");
        }
    }

    // ======================================================================
    //  SYSTEMD — Eliminar mount unit
    // ======================================================================
    private static void EliminarMountUnit(IscsiDestino d, long id)
    {
        if (string.IsNullOrWhiteSpace(d.MountPoint))
            return;

        string safeMount = d.MountPoint
            .Trim('/')
            .Replace("/", "-")
            .Replace(".", "_");

        string mountUnit = $"/etc/systemd/system/{safeMount}.mount";

        if (File.Exists(mountUnit))
        {
            ShellHelper.EjecutarComoRoot($"systemctl disable {safeMount}.mount");
            ShellHelper.EjecutarComoRoot($"rm -f {mountUnit}");
            Log(id, $"Mount unit eliminada: {mountUnit}");
        }
    }

    // ======================================================================
    //  SYSTEMD — Habilitar servicios necesarios
    // ======================================================================
    private static void HabilitarServicios(long id)
    {
        Log(id, "Habilitando servicios systemd...");

        ShellHelper.EjecutarComoRoot("systemctl enable iscsid");

        var check = ShellHelper.EjecutarComoRoot("systemctl list-unit-files | grep -q open-iscsi");

        if (check.ExitCode == 0)
        {
            ShellHelper.EjecutarComoRoot("systemctl enable open-iscsi");
        }
        else
        {
            Log(id, "open-iscsi no existe en este sistema (OK)");
        }
    }

    // ======================================================================
    //  DETECTAR PERSISTENCIA REAL
    // ======================================================================
    public static bool EsPersistente(IscsiDestino d)
    {
        if (string.IsNullOrWhiteSpace(d.MountPoint))
            return false;

        if (File.Exists("/etc/fstab"))
        {
            string fstab = File.ReadAllText("/etc/fstab");
            if (fstab.Contains(d.PartitionPath) || fstab.Contains(d.MountPoint))
                return true;
        }

        string safe = SystemdSafe(d.Iqn);
        string service = $"/etc/systemd/system/iscsi-login-{safe}.service";
        if (File.Exists(service))
            return true;

        string safeMount = d.MountPoint
            .Trim('/')
            .Replace("/", "-")
            .Replace(".", "_");

        string mountUnit = $"/etc/systemd/system/{safeMount}.mount";
        if (File.Exists(mountUnit))
            return true;

        return false;
    }
    
    
   public static async Task InicializarDestino(IscsiDestino d, string label, string fsType)
{
    long id = NextTraceId();
    var sw = StartTrace(id, "InicializarDestino", $"IQN='{d.Iqn}', FS='{fsType}', Label='{label}'");

    try
    {
        // --------------------------------------------------------------
        // 1) Asegurar que el destino está conectado
        // --------------------------------------------------------------
        if (!d.Conectado)
        {
            Log(id, "Destino no conectado → conectando...");
            await Conectar(d);
        }

        // --------------------------------------------------------------
        // 2) Desmontar si está montado
        // --------------------------------------------------------------
        var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
        if (mpCheck.ExitCode == 0)
        {
            Log(id, "Desmontando volumen...");
            ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
            await Task.Delay(300);
        }

        // --------------------------------------------------------------
        // 3) Borrar tabla de particiones
        // --------------------------------------------------------------
        Log(id, "Borrando tabla de particiones...");
        ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {d.PartitionPath}");

        // --------------------------------------------------------------
        // 4) Crear tabla GPT
        // --------------------------------------------------------------
        Log(id, "Creando tabla GPT...");
        ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mklabel gpt");

        // --------------------------------------------------------------
        // 5) Crear partición primaria
        // --------------------------------------------------------------
        Log(id, "Creando partición primaria...");
        ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mkpart primary 0% 100%");

        // Esperar a que el kernel detecte la nueva partición
        await Task.Delay(1200);

        // --------------------------------------------------------------
        // 6) Detectar nueva partición real
        // --------------------------------------------------------------
        Log(id, "Detectando nueva partición...");
        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 2)
            throw new Exception("No se detectó la nueva partición tras crearla.");

        d.PartitionPath = "/dev/" + lines[1].Trim();

        Log(id, $"Nueva partición detectada: {d.PartitionPath}");

        // --------------------------------------------------------------
        // 7) Formatear según filesystem elegido
        // --------------------------------------------------------------
        Log(id, $"Formateando en {fsType}...");

        switch (fsType)
        {
            case "ext4":
                ShellHelper.EjecutarComoRoot($"mkfs.ext4 -F -L \"{label}\" {d.PartitionPath}");
                break;

            case "xfs":
                ShellHelper.EjecutarComoRoot($"mkfs.xfs -f -L \"{label}\" {d.PartitionPath}");
                break;

            case "btrfs":
                ShellHelper.EjecutarComoRoot($"mkfs.btrfs -f -L \"{label}\" {d.PartitionPath}");
                break;

            case "f2fs":
                ShellHelper.EjecutarComoRoot($"mkfs.f2fs -f -l \"{label}\" {d.PartitionPath}");
                break;

            case "ntfs":
                ShellHelper.EjecutarComoRoot($"mkfs.ntfs -F -L \"{label}\" {d.PartitionPath}");
                break;

            case "exfat":
                ShellHelper.EjecutarComoRoot($"mkfs.exfat -n \"{label}\" {d.PartitionPath}");
                break;

            default:
                throw new Exception($"Filesystem no soportado: {fsType}");
        }

        d.FsType = fsType;
        d.TieneFilesystem = true;

        // --------------------------------------------------------------
        // 8) Montaje automático compatible
        // --------------------------------------------------------------
        string mountFs = fsType;

        // NTFS moderno → ntfs3
        if (fsType == "ntfs")
            mountFs = "ntfs3";

        Log(id, $"Montando volumen como {mountFs}...");
        ShellHelper.EjecutarComoRoot($"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\"");

        // --------------------------------------------------------------
        // 9) Notificación
        // --------------------------------------------------------------
        NotificadorLinux.Enviar($"Destino {d.Iqn} inicializado como {fsType} con etiqueta '{label}'");

        EndTrace(id, "InicializarDestino", sw, "OK");
    }
    catch (Exception ex)
    {
        Log(id, $"[ERROR] InicializarDestino: {ex.Message}");
        NotificadorLinux.Enviar($"[ERROR] Fallo al inicializar destino {d.Iqn}");
        EndTrace(id, "InicializarDestino", sw, "ERROR");
    }
}

    
public static bool SoportaFs(string fs)
{
    string cmd = fs switch
    {
        "ext4" => "which mkfs.ext4",
        "xfs" => "which mkfs.xfs",
        "btrfs" => "which mkfs.btrfs",
        "f2fs" => "which mkfs.f2fs",
        "ntfs" => "which mkfs.ntfs",
        "exfat" => "which mkfs.exfat",
        _ => null
    };

    if (cmd == null)
        return false;

    var r = ShellHelper.EjecutarComoRoot(cmd);
    return r.ExitCode == 0;
}

   
   
    
}

