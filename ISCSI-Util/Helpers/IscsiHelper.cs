using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    //  INFRAESTRUCTURA DE TRAZAS
    // ============================================================

    private static long _traceCounter;

    private static long NextTraceId()
    {
        return ++_traceCounter;
    }

    private static void TraceIn(long id, string method, string details = "")
    {
        LogService.Debug($"[ISCSI] #{id} → {method} {details}");
    }

    private static void TraceOut(long id, string method, string result = "OK")
    {
        LogService.Debug($"[ISCSI] #{id} ← {method} [{result}]");
    }

    // ============================================================
    //  SANITIZAR NOMBRE PARA ARCHIVOS Y SYSTEMD
    // ============================================================

    public static string SanitizarNombre(string iqn)
    {
        if (string.IsNullOrWhiteSpace(iqn))
            return "iscsi_target";

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', ' ' })
            .ToArray();

        return new string(iqn.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string SystemdSafe(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        return s.Replace(":", "_")
            .Replace(".", "_")
            .Replace("-", "_")
            .Replace("/", "_");
    }

    // ============================================================
    //  NORMALIZAR PORTAL (IP + PUERTO)
    // ============================================================

    private static (string Ip, int Port) NormalizarPortal(string portal)
    {
        if (string.IsNullOrWhiteSpace(portal))
            return ("127.0.0.1", 3260);

        if (portal.Contains(':'))
        {
            var partes = portal.Split(':', 2);
            if (int.TryParse(partes[1], out var p))
                return (partes[0], p);
        }

        return (portal, 3260);
    }

    // ============================================================
    //  DETECTAR FILESYSTEM
    // ============================================================

    private static string DetectarFsType(string blkidOut)
    {
        if (string.IsNullOrWhiteSpace(blkidOut))
            return "raw";

        var s = blkidOut.ToLowerInvariant();

        if (s.Contains("type=\"ext2\"")) return "ext2";
        if (s.Contains("type=\"ext3\"")) return "ext3";
        if (s.Contains("type=\"ext4\"")) return "ext4";
        if (s.Contains("type=\"xfs\"")) return "xfs";
        if (s.Contains("type=\"btrfs\"")) return "btrfs";
        if (s.Contains("type=\"f2fs\"")) return "f2fs";
        if (s.Contains("type=\"ntfs\"")) return "ntfs";
        if (s.Contains("type=\"vfat\"")) return "vfat";
        if (s.Contains("type=\"exfat\"")) return "exfat";
        if (s.Contains("type=\"iso9660\"")) return "iso9660";
        if (s.Contains("type=\"swap\"")) return "swap";
        if (s.Contains("type=\"crypto_luks\"")) return "luks";
        if (s.Contains("type=\"lvm2_member\"")) return "lvm";
        if (s.Contains("type=\"linux_raid_member\"")) return "raid";
        if (s.Contains("type=\"zfs_member\"")) return "zfs";

        if (s.Contains("pttype="))
            return "raw";

        return "raw";
    }

    // ============================================================
    //  DETECTAR CHAP / MUTUAL CHAP
    // ============================================================

    public static void DetectarChap(IscsiDestino d)
    {
        if (d == null) return;

        var id = NextTraceId();
        LogService.Debug($"[ISCSI] #{id} DetectarChap → {d.Iqn} ({d.Ip})");

        try
        {
            var (ipSolo, port) = NormalizarPortal(d.Ip);

            var check = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {ipSolo}"
            );

            var nodoExiste = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

            if (!nodoExiste)
            {
                LogService.Debug($"[ISCSI] #{id} Nodo no existe. Creándolo temporalmente...");
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=new"
                );
            }

            LogService.Debug($"[ISCSI] #{id} Leyendo configuración CHAP real...");

            var show = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} -o show"
            );

            var config = show.Stdout ?? "";

            var authMethod = ExtraerValor(config, "node.session.auth.authmethod");
            var user = ExtraerValor(config, "node.session.auth.username");
            var pass = ExtraerValor(config, "node.session.auth.password");
            var userIn = ExtraerValor(config, "node.session.auth.username_in");
            var passIn = ExtraerValor(config, "node.session.auth.password_in");

            var chapEnabled = authMethod.Equals("CHAP", StringComparison.OrdinalIgnoreCase);

            var userEmpty = string.IsNullOrWhiteSpace(user) || user == "<empty>";
            var passEmpty = string.IsNullOrWhiteSpace(pass) || pass == "<empty>";
            var userInEmpty = string.IsNullOrWhiteSpace(userIn) || userIn == "<empty>";
            var passInEmpty = string.IsNullOrWhiteSpace(passIn) || passIn == "<empty>";

            d.UsaChap = chapEnabled && !userEmpty && !passEmpty;
            d.UsaMutualChap = chapEnabled && !userInEmpty && !passInEmpty;

            d.UsuarioChap = userEmpty ? "" : user;
            d.PasswordChap = passEmpty ? "" : pass;
            d.UsuarioMutualChap = userInEmpty ? "" : userIn;
            d.PasswordMutualChap = passInEmpty ? "" : passIn;

            LogService.Debug(
                $"[ISCSI] #{id} CHAP={d.UsaChap}, Mutual={d.UsaMutualChap}, " +
                $"User='{d.UsuarioChap}', UserIn='{d.UsuarioMutualChap}'"
            );

            if (!nodoExiste)
            {
                LogService.Debug($"[ISCSI] #{id} Eliminando nodo temporal...");
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=delete"
                );
            }

            LogService.Debug($"[ISCSI] #{id} DetectarChap completado.");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR DetectarChap: {ex.Message}");
            d.UsaChap = false;
            d.UsaMutualChap = false;
        }
    }

    private static string ExtraerValor(string config, string key)
    {
        if (string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(key))
            return "";

        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    return parts[1].Trim();
            }
        }

        return "";
    }

    // ============================================================
    //  DISCOVER — Descubrir destinos iSCSI
    // ============================================================

    public static async Task<List<IscsiDestino>> Descubrir(string ip)
    {
        var id = NextTraceId();
        TraceIn(id, "Descubrir", $"IP='{ip}'");

        LogService.Write($"[ISCSI] Discovering targets at {ip}...");

        var destinos = new List<IscsiDestino>();

        try
        {
            LogService.Debug($"[ISCSI] #{id} Ejecutando discovery sendtargets...");
            var discovery = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}")
            );

            if (string.IsNullOrWhiteSpace(discovery.Stdout))
            {
                LogService.Debug($"[ISCSI] #{id} Discovery vacío.");
                TraceOut(id, "Descubrir", "EMPTY");
                return destinos;
            }

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout ?? "";

            var countParseados = 0;

            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var portalRaw = partes[0];
                var portal = portalRaw.Split(',')[0];

                if (!portal.Contains(':'))
                    portal = $"{portal}:3260";

                if (!portal.StartsWith(ip))
                    continue;

                var iqn = partes.LastOrDefault(s => s.StartsWith("iqn."));
                if (string.IsNullOrWhiteSpace(iqn))
                    continue;

                var conectado = sesiones.Contains(iqn, StringComparison.OrdinalIgnoreCase);

                destinos.Add(new IscsiDestino
                {
                    Ip = portal,
                    PortalReal = portal,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                });

                countParseados++;
            }

            LogService.Debug($"[ISCSI] #{id} Targets parseados: {countParseados}");
            LogService.Debug($"[ISCSI] #{id} Detectando CHAP para {destinos.Count} targets...");

            await Task.Run(() =>
            {
                foreach (var d in destinos)
                {
                    var chap = IscsiChapDetector.Detect(d);

                    d.RequiresChap = chap.RequiresChap;
                    d.RequiresMutualChap = chap.RequiresMutualChap;
                    d.HasLocalChapConfigured = chap.HasLocalChapConfigured;
                    d.HasLocalMutualConfigured = chap.HasLocalMutualConfigured;

                    d.LocalUser = chap.LocalUser;
                    d.LocalPass = chap.LocalPass;
                    d.LocalUserIn = chap.LocalUserIn;
                    d.LocalPassIn = chap.LocalPassIn;

                    d.UsaChap = d.RequiresChap || d.HasLocalChapConfigured;
                    d.UsaMutualChap = d.RequiresMutualChap || d.HasLocalMutualConfigured;
                }
            });

            LogService.Debug($"[ISCSI] #{id} CHAP detection completed.");

            TraceOut(id, "Descubrir", $"OK ({destinos.Count} targets)");
            return destinos;
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Descubrir: {ex.Message}");
            TraceOut(id, "Descubrir", "ERROR");
            return destinos;
        }
    }

    public static async Task CompletarInformacionDestino(IscsiDestino d, long parentId)
    {
        if (d == null) return;

        var id = NextTraceId();
        TraceIn(id, "CompletarInformacion", d.Iqn);

        try
        {
            // 1) Verificar la sesión activa en el Kernel
            var sessionOutput = ShellHelper.EjecutarComoRoot($"iscsiadm -m session 2>/dev/null | grep -i \"{d.Iqn}\"")
                .Stdout ?? "";
            d.Conectado = !string.IsNullOrWhiteSpace(sessionOutput);

            if (!d.Conectado)
            {
                d.DevicePath = null;
                d.PartitionPath = null;
                d.MountPoint = null;
                d.TieneFilesystem = false;
                d.FsType = "";
                return;
            }

            // 2) Detectar symlink / dispositivo activo
            var byPathOut = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/ 2>/dev/null").Stdout ?? "";
            var byPath = byPathOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var portal = string.IsNullOrWhiteSpace(d.PortalReal) ? d.Ip : d.PortalReal;
            var (ipSolo, _) = NormalizarPortal(portal);

            var match = byPath.FirstOrDefault(l =>
                l.Contains(ipSolo, StringComparison.OrdinalIgnoreCase) &&
                l.Contains("lun", StringComparison.OrdinalIgnoreCase)
            );

            if (match == null)
            {
                d.DevicePath = null;
                d.PartitionPath = null;
                d.MountPoint = null;
                d.TieneFilesystem = false;
                d.FsType = "";
                return;
            }

            d.DevicePath = "/dev/disk/by-path/" + match.Trim();

            // 3) Detectar si existe una partición o si es un dispositivo formateado en raw (Superfloppy)
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {d.DevicePath}");
            var lines = (lsblk.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

            string? targetPath = null;

            foreach (var line in lines)
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && p[1] == "part")
                {
                    targetPath = "/dev/" + p[0];
                    d.PartitionPath = targetPath;
                    break;
                }
            }

            if (targetPath == null)
            {
                d.PartitionPath = null;
                targetPath = d.DevicePath;
            }

            // 4) Detectar Filesystem
            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {targetPath}");
            d.FsType = DetectarFsType(blkid.Stdout ?? "");

            d.TieneFilesystem = !string.IsNullOrWhiteSpace(d.FsType) &&
                                !d.FsType.Equals("raw", StringComparison.OrdinalIgnoreCase);

            // 5) Detectar punto de montaje en runtime
            d.MountPoint = null;

            var mounts = (ShellHelper.EjecutarComoRoot("mount").Stdout ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var m in mounts)
            {
                if (d.PartitionPath != null && m.StartsWith(d.PartitionPath + " "))
                {
                    d.MountPoint = m.Split(' ')[2];
                    break;
                }

                if (d.DevicePath != null && m.StartsWith(d.DevicePath + " "))
                {
                    d.MountPoint = m.Split(' ')[2];
                    break;
                }
            }

            // 6) Detectar persistencia
            d.Persistir = DetectarPersistencia(d);

            if (d.Persistir && string.IsNullOrEmpty(d.MountPoint))
            {
                var safe = SanitizarNombre(d.Iqn)
                    .Replace('.', '_')
                    .Replace('-', '_');

                d.MountPoint = Path.Combine(ConfigManager.MountBasePath, safe);
            }

            TraceOut(id, "CompletarInformacion");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR CompletarInformacion: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    public static async Task ConectarSesion(SessionInfo s)
    {
        if (s == null)
            return;

        var cmd = $"iscsiadm -m node -T {s.Iqn} -p {s.Portal} --login";
        var res = ShellHelper.EjecutarComoRoot(cmd);

        if (res.ExitCode == 0)
        {
            s.Connected = true;
            s.ConnectedSince = DateTime.Now;
        }
        else
        {
            LogService.Error($"[ISCSI] Error al conectar sesión {s.Iqn}: {res.Stderr}");
        }

        await Task.CompletedTask;
    }

    public static async Task DesconectarSesion(SessionInfo s)
    {
        if (s == null)
            return;

        var cmd = $"iscsiadm -m node -T {s.Iqn} -p {s.Portal} --logout";
        var res = ShellHelper.EjecutarComoRoot(cmd);

        if (res.ExitCode == 0)
        {
            s.Connected = false;
            s.MountPoint = "";
        }
        else
        {
            LogService.Error($"[ISCSI] Error al desconectar sesión {s.Iqn}: {res.Stderr}");
        }

        await Task.CompletedTask;
    }

    // ======================================================================
    //  CONECTAR — Login iSCSI + detección + montaje
    // ======================================================================

    public static async Task Conectar(IscsiDestino d)
    {
        if (d == null) return;

        var id = NextTraceId();
        TraceIn(id, "Conectar", d.Iqn);

        LogService.Debug($"[ISCSI] #{id} >>> INICIO Conectar() para IQN={d.Iqn}, IP={d.Ip}");

        try
        {
            // 1) Crear mountpoint único por IQN
            var basePath = ConfigManager.MountBasePath;

            var safeIqn = d.Iqn
                .Replace(":", "_")
                .Replace("/", "_")
                .Replace(".", "_")
                .Replace("-", "_");

            var hash = Convert.ToHexString(
                SHA1.HashData(
                    Encoding.UTF8.GetBytes(d.Iqn)
                )
            ).Substring(0, 8);

            d.MountPoint = Path.Combine(basePath, $"{safeIqn}_{hash}");

            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            // 2) Comprobar si ya está conectado
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout ?? "";
            var yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            var (ipSolo, port) = NormalizarPortal(d.Ip);

            // 3) LOGIN iSCSI
            if (!yaConectado)
            {
                var checkNode = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo}"
                );

                var nodoExiste = !checkNode.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

                if (!nodoExiste)
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=new"
                    );

                // CHAP
                if (d.UsaChap || d.UsaMutualChap)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.authmethod --value=CHAP"
                    );

                    if (d.UsaChap)
                    {
                        var user = string.IsNullOrWhiteSpace(d.UsuarioChap) ? d.LocalUser : d.UsuarioChap;
                        var pass = string.IsNullOrWhiteSpace(d.PasswordChap) ? d.LocalPass : d.PasswordChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.username --value=\"{user}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.password --value=\"{pass}\""
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        var userIn = string.IsNullOrWhiteSpace(d.UsuarioMutualChap)
                            ? d.LocalUserIn
                            : d.UsuarioMutualChap;
                        var passIn = string.IsNullOrWhiteSpace(d.PasswordMutualChap)
                            ? d.LocalPassIn
                            : d.PasswordMutualChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
                        );
                    }
                }

                var loginResult = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --login"
                );

                if (loginResult.ExitCode != 0 &&
                    !loginResult.Stderr.Contains("already present", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Login iSCSI falló: {loginResult.Stderr}");

                await Task.Delay(300);
            }

            // 4) Detectar symlink correcto
            d.DevicePath = null;

            for (var i = 0; i < 10; i++)
            {
                var byPathOut = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout ?? "";
                var byPath = byPathOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                var match = byPath.FirstOrDefault(l =>
                    l.Contains(ipSolo, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("lun", StringComparison.OrdinalIgnoreCase)
                );

                if (match != null)
                {
                    d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    break;
                }

                await Task.Delay(200);
            }

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception("No se encontró symlink del dispositivo iSCSI.");

            // 5) Detectar partición correcta
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {d.DevicePath}");
            var lines = (lsblk.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

            string? partition = null;

            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == "part")
                {
                    partition = "/dev/" + parts[0];
                    break;
                }
            }

            d.PartitionPath = partition ?? d.DevicePath;

            // 6) Detectar filesystem
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                d.TieneFilesystem = false;
                d.FsType = "";
                d.Conectado = true;

                TraceOut(id, "Conectar", "RAW_NO_FS");
                return;
            }

            d.TieneFilesystem = true;
            d.FsType = DetectarFsType(blkid.Stdout);

            // 7) Montar
            if (!d.TieneFilesystem)
            {
                d.Conectado = true;
                TraceOut(id, "Conectar", "RAW_NO_MOUNT");
                return;
            }

            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                var mountFs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;

                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );
            }

            d.Conectado = true;
            NotificadorLinux.Enviar($"Target {d.Iqn} mounted in {d.MountPoint}");

            TraceOut(id, "Conectar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Conectar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to connect target {d.Iqn}", 6000, "critical");
            throw;
        }
    }

    // ======================================================================
    //  OBTENER PORTAL REAL
    // ======================================================================

    public static string? ObtenerPortalReal(IscsiDestino d)
    {
        if (d == null) return null;

        try
        {
            var result = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn}"
            );

            if (string.IsNullOrWhiteSpace(result.Stdout))
                return null;

            var line = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.Contains(d.Iqn));

            if (line == null)
                return null;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            var portal = parts[0].Trim();

            if (portal.StartsWith("node.", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!portal.Contains('.') || !portal.Contains(':'))
                return null;

            return portal;
        }
        catch
        {
            return null;
        }
    }

    // ======================================================================
    //  ELIMINAR PERSISTENCIA
    // ======================================================================

    private static async Task EliminarPersistencia_Original(IscsiDestino d, long id)
    {
        if (d == null) return;

        LogService.Debug($"[ISCSI] #{id} EliminarPersistencia_Original → Iniciando para {d.Iqn}");

        try
        {
            var safe = SystemdSafe(d.Iqn);

            var scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
            var servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");
            ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpEsc = d.MountPoint.Replace("/", "\\/");
                ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
            }

            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op update --name node.startup --value manual"
            );

            TraceOut(id, "EliminarPersistencia_Original");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR EliminarPersistencia_Original: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // ======================================================================
    //  DETECTAR PERSISTENCIA
    // ======================================================================

    public static bool DetectarPersistencia(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.MountPoint))
            return false;

        try
        {
            if (File.Exists("/etc/fstab"))
            {
                var fstab = File.ReadAllText("/etc/fstab");
                var pattern = $" {d.MountPoint} ";

                if (fstab.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        try
        {
            var safe = SystemdSafe(d.Iqn);
            var service = $"/etc/systemd/system/iscsi-{safe}.service";

            if (File.Exists(service))
                return true;
        }
        catch
        {
        }

        return false;
    }

    // ======================================================================
    //  DESCONECTAR — Desmontaje + logout + limpieza segura
    // ======================================================================


    // ======================================================================
    //  DESCONECTAR + BORRAR NODO — versión completa
    // ======================================================================

    public static async Task Desconectar_Borrar(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return;

        var id = NextTraceId();
        TraceIn(id, "Desconectar_Borrar", d.Iqn);

        try
        {
            var mountPoint = ObtenerPuntoMontaje(d);
            var safe = ObtenerNombreSeguro(d.Iqn);

            // 1) DESMONTAR SI ESTÁ MONTADO
            if (!string.IsNullOrWhiteSpace(mountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{mountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{mountPoint}\"");
                    await Task.Delay(300);

                    mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{mountPoint}\"");
                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot($"umount -f \"{mountPoint}\"");
                        await Task.Delay(200);
                    }
                }

                // 2) BORRAR DIRECTORIO DE MONTAJE
                if (Directory.Exists(mountPoint)) ShellHelper.EjecutarComoRoot($"rmdir \"{mountPoint}\"");

                // 3) ELIMINAR DE FSTAB (usando el mismo mountPoint exacto)
                var mpEsc = mountPoint.Replace("/", "\\/");
                ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
            }

            // 4) LOGOUT DEL TARGET
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout ?? "";
            if (!string.IsNullOrWhiteSpace(sesiones) && sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var (ipSolo, _) = NormalizarPortal(d.Ip);
                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --logout")
                );

                await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // 5) ELIMINAR SERVICIO SYSTEMD (usando el identificador seguro unificado)
            var servicePath = $"/etc/systemd/system/iscsi-{safe}.service";
            var scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";

            ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service 2>/dev/null");
            ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");
            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // 6) BORRAR NODO Y DISCOVERYDB
            var (ipSolo2, _) = NormalizarPortal(d.Ip);
            ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {ipSolo2} --op=delete");
            ShellHelper.EjecutarComoRoot($"iscsiadm -m discoverydb -t sendtargets -p {ipSolo2} --op=delete");

            // 7) RESET COMPLETO DEL OBJETO
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            d.RequiresChap = false;
            d.RequiresMutualChap = false;
            d.HasLocalChapConfigured = false;
            d.HasLocalMutualConfigured = false;

            d.LocalUser = "";
            d.LocalPass = "";
            d.LocalUserIn = "";
            d.LocalPassIn = "";

            d.UsaChap = false;
            d.UsaMutualChap = false;

            d.UsuarioChap = "";
            d.PasswordChap = "";
            d.UsuarioMutualChap = "";
            d.PasswordMutualChap = "";

            d.Persistir = false;
            d.PersistenteReal = false;

            NotificadorLinux.Enviar($"Target {d.Iqn} fully removed");
            TraceOut(id, "Desconectar_Borrar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar_Borrar: {ex.Message}");
            throw;
        }
    }

    // ======================================================================
    //  INITIALIZE TARGET — Root Directory Creation & UI Messages
    // ======================================================================

    public static async Task InicializarDestino(IscsiDestino d, string label, string fsType)
    {
        if (d == null)
            throw new ArgumentNullException(nameof(d));

        var id = NextTraceId();
        TraceIn(id, "InicializarDestino", d.Iqn ?? "UNKNOWN_IQN");

        try
        {
            if (!d.Conectado)
                await Conectar(d);

            await Task.Run(async () =>
            {
                var device = ObtenerDispositivoDesdeSesion(d.Iqn);

                if (string.IsNullOrEmpty(device))
                {
                    var (ipSolo, _) = NormalizarPortal(d.Ip ?? "");
                    ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T \"{d.Iqn}\" -p \"{ipSolo}\" --rescan");
                    ShellHelper.EjecutarComoRoot("udevadm settle");
                    await Task.Delay(1000);

                    device = ObtenerDispositivoDesdeSesion(d.Iqn);
                }

                if (string.IsNullOrEmpty(device))
                    throw new Exception($"Could not find an active block device (/dev/sdX) assigned to IQN {d.Iqn}.");

                d.DevicePath = device;

                // Garantizar punto de montaje uniforme
                d.MountPoint = ObtenerPuntoMontaje(d);

                // Desmontar rutas previas de forma segura
                ShellHelper.EjecutarComoRoot($"umount -f \"{device}\"* 2>/dev/null");
                ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\" 2>/dev/null");
                await Task.Delay(300);

                // Limpieza de firmas e inicialización de disco
                ShellHelper.EjecutarComoRoot($"wipefs -a -f {device}");
                ShellHelper.EjecutarComoRoot($"dd if=/dev/zero of={device} bs=1M count=10 status=none 2>/dev/null");
                ShellHelper.EjecutarComoRoot("udevadm settle");
                await Task.Delay(500);

                // Formateo de sistema de archivos
                var safeLabel = (label ?? "iSCSI_Disk").Replace("\"", "\\\"").Replace("$", "\\$");
                var mkfs = fsType.ToLowerInvariant() switch
                {
                    "ext4" => $"mkfs.ext4 -F -b 4096 -L \"{safeLabel}\" {device}",
                    "xfs" => $"mkfs.xfs -f -L \"{safeLabel}\" {device}",
                    "btrfs" => $"mkfs.btrfs -f -L \"{safeLabel}\" {device}",
                    "ntfs" => $"mkfs.ntfs -F -L \"{safeLabel}\" {device}",
                    "exfat" => $"mkfs.exfat -n \"{safeLabel}\" {device}",
                    _ => $"mkfs.ext4 -F -b 4096 -L \"{safeLabel}\" {device}"
                };

                var resMkfs = ShellHelper.EjecutarComoRoot(mkfs);
                if (resMkfs.ExitCode != 0)
                    throw new Exception($"Failed to format partition ({fsType}): {resMkfs.Stderr.Trim()}");

                d.PartitionPath = device;
                d.TieneFilesystem = true;
                d.FsType = fsType;

                ShellHelper.EjecutarComoRoot("udevadm settle");

                // Crear directorio y montar
                var mkdirRes = ShellHelper.EjecutarComoRoot($"mkdir -p \"{d.MountPoint}\"");
                if (mkdirRes.ExitCode != 0)
                    throw new Exception($"Failed to create mount directory {d.MountPoint}: {mkdirRes.Stderr.Trim()}");

                var mountFs = fsType == "ntfs" ? "ntfs-3g" : fsType;
                var resMount = ShellHelper.EjecutarComoRoot($"mount -t {mountFs} {device} \"{d.MountPoint}\"");

                if (resMount.ExitCode != 0)
                    throw new Exception($"Failed to mount device {device} on {d.MountPoint}: {resMount.Stderr.Trim()}");
            });

            NotificadorLinux.Enviar($"Target {d.Iqn} initialized and mounted successfully.");
            TraceOut(id, "InicializarDestino");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Initializing target: {ex.Message}");
            throw;
        }
    }

    // ======================================================================
    //  HELPER: Multi-layer SCSI node resolution
    // ======================================================================

    private static string? ObtenerDispositivoDesdeSesion(string? iqn)
    {
        if (string.IsNullOrWhiteSpace(iqn))
            return null;

        try
        {
            // Strategy A: Sysfs scanning
            if (Directory.Exists("/sys/class/iscsi_session"))
            {
                var sessions = Directory.GetDirectories("/sys/class/iscsi_session");
                foreach (var sess in sessions)
                {
                    var targetNameFile = Path.Combine(sess, "targetname");
                    if (File.Exists(targetNameFile))
                    {
                        var targetName = File.ReadAllText(targetNameFile).Trim();
                        if (targetName.Equals(iqn, StringComparison.OrdinalIgnoreCase))
                        {
                            var sid = Path.GetFileName(sess).Replace("session", "");
                            var blockDirs = Directory.GetDirectories("/sys/block");
                            foreach (var bdir in blockDirs)
                            {
                                var devName = Path.GetFileName(bdir);
                                if (devName.StartsWith("sd"))
                                {
                                    var deviceSymlink = Path.Combine(bdir, "device");
                                    if (Directory.Exists(deviceSymlink))
                                    {
                                        var realPath = ShellHelper.EjecutarComoRoot($"realpath \"{deviceSymlink}\"")
                                            .Stdout ?? "";
                                        if (realPath.Contains($"session{sid}") || realPath.Contains($"target{sid}"))
                                            return $"/dev/{devName}";
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Strategy B: Fallback to lsblk + udevadm
            var res = ShellHelper.EjecutarComoRoot("lsblk -dno NAME,TRAN");
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.Stdout))
            {
                var lines = res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1].Equals("iscsi", StringComparison.OrdinalIgnoreCase))
                    {
                        var devPath = "/dev/" + parts[0].Trim();
                        var info = ShellHelper.EjecutarComoRoot($"udevadm info --query=property --name={devPath}");
                        if (info.ExitCode == 0 && (info.Stdout ?? "").Contains(iqn, StringComparison.OrdinalIgnoreCase))
                            return devPath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] Error resolving SCSI node: {ex.Message}");
        }

        return null;
    }

    private static string? ResolverDispositivoVivo(string iqn)
    {
        if (string.IsNullOrWhiteSpace(iqn))
            return null;

        try
        {
            if (Directory.Exists("/dev/disk/by-path"))
            {
                var files = Directory.GetFiles("/dev/disk/by-path");
                foreach (var file in files)
                    if (file.Contains(iqn, StringComparison.OrdinalIgnoreCase) && !file.EndsWith("-part1"))
                    {
                        var res = ShellHelper.EjecutarComoRoot($"realpath \"{file}\"");
                        if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.Stdout))
                        {
                            var realDev = res.Stdout.Trim();
                            if (ShellHelper.EjecutarComoRoot($"blockdev --getsize64 {realDev}").ExitCode == 0)
                                return realDev;
                        }
                    }
            }
        }
        catch
        {
        }

        return null;
    }

    // ======================================================================
    //  SOPORTA FILESYSTEM
    // ======================================================================

    public static bool SoportaFs(string fs)
    {
        if (string.IsNullOrWhiteSpace(fs))
            return false;

        fs = fs.ToLowerInvariant();

        var cmd = fs switch
        {
            "ext2" => "mkfs.ext2",
            "ext3" => "mkfs.ext3",
            "ext4" => "mkfs.ext4",
            "xfs" => "mkfs.xfs",
            "btrfs" => "mkfs.btrfs",
            "f2fs" => "mkfs.f2fs",
            "ntfs" => "mkfs.ntfs",
            "exfat" => "mkfs.exfat",
            _ => ""
        };

        if (string.IsNullOrEmpty(cmd))
            return false;

        var check = ShellHelper.EjecutarComoRoot($"which {cmd}");
        return check.ExitCode == 0;
    }


    // Helper centralizado para nombres de archivos y servicios
    public static string ObtenerNombreSeguro(string iqn)
    {
        if (string.IsNullOrWhiteSpace(iqn)) return "iscsi_target";
        return SystemdSafe(iqn); // O la sanitización estándar elegida
    }

// Helper centralizado para obtener la ruta de montaje uniforme
    public static string ObtenerPuntoMontaje(IscsiDestino d)
    {
        if (d == null) return string.Empty;

        if (!string.IsNullOrWhiteSpace(d.MountPoint))
            return d.MountPoint;

        var lunName = d.Iqn.Contains(':')
            ? d.Iqn.Substring(d.Iqn.LastIndexOf(':') + 1)
            : "iscsi_lun";

        return Path.Combine(ConfigManager.MountBasePath, SanitizarNombre(lunName));
    }
}