using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    //  INFRAESTRUCTURA DE TRAZAS
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
    //  SANITIZAR NOMBRE PARA ARCHIVOS Y SYSTEMD
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
    //   BASE DE MONTADO EN ESPACIO DE USUARIO
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
    //   DETECTAR FILESYSTEM
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
    //   DETECTAR CHAP / MUTUAL CHAP
    // ======================================================================
   
    
    public static void DetectarChap(IscsiDestino d)
    {
        // Normalizamos IQN a minúsculas para comparar
        var iqn = d.Iqn?.ToLowerInvariant() ?? string.Empty;

        // MyCloud EX2 Ultra → sin CHAP
        if (iqn.Contains("mycloudex2ultra") || iqn.Contains("mycloud"))
        {
            d.UsaChap = false;
            d.UsaMutualChap = false;
            return;
        }

        // FreeNAS/TrueNAS mutual CHAP
        if (iqn.Contains("mutualchap"))
        {
            d.UsaChap = true;
            d.UsaMutualChap = true;
            return;
        }

        // FreeNAS/TrueNAS CHAP normal
        if (iqn.Contains("bak") || iqn.Contains("chap"))
        {
            d.UsaChap = true;
            d.UsaMutualChap = false;
            return;
        }

        // Por defecto: sin CHAP
        d.UsaChap = false;
        d.UsaMutualChap = false;
    }

    

    
    private static string ExtraerValor(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(key))
            {
                var parts = line.Split('=');
                if (parts.Length == 2)
                    return parts[1].Trim();
            }
        }
        return "";
    }

    // ======================================================================
    //  DISCOVER — Descubrir destinos iSCSI en un portal
    // ======================================================================
    
   public static async Task<List<IscsiDestino>> Descubrir(string ip)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → Descubrir IP='{ip}'");

    var destinos = new List<IscsiDestino>();

    using (LoadingService.Show($"Discovering targets at {ip}..."))
    {
        try
        {
            LogService.Debug($"[ISCSI] #{id} Ejecutando discovery...");

            var discoveryTask = Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}")
            );

            var completed = await Task.WhenAny(discoveryTask, Task.Delay(5000));

            if (completed != discoveryTask)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en discovery");
                NotificadorLinux.Enviar($"[TIMEOUT] Discovery to {ip} took too long.", 6000, "critical");
                return destinos;
            }

            var discovery = discoveryTask.Result;

            if (string.IsNullOrWhiteSpace(discovery.Stdout))
            {
                LogService.Write($"[ISCSI] #{id} No se encontraron destinos.");
                NotificadorLinux.Enviar($"No targets found at {ip}.", 4000, "normal");
                return destinos;
            }

            var sesiones = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout
            );

            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                string iqn = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(s => s.StartsWith("iqn."));

                if (string.IsNullOrWhiteSpace(iqn))
                    continue;

                bool conectado = sesiones.Contains(iqn);

                if (destinos.Any(d => d.Iqn == iqn && d.Ip == ip))
                    continue;

                destinos.Add(new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                });
            }

            NotificadorLinux.Enviar($"Found {destinos.Count} targets at {ip}", 5000, "normal");

            foreach (var d in destinos)
            {
                DetectarChap(d);
                d.Persistir = DetectarPersistencia(d);
            }

            foreach (var d in destinos.Where(x => x.Conectado))
            {
                try
                {
                    await CompletarInformacionDestino(d, id);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[ISCSI] #{id} WARN al completar info de {d.Iqn}: {ex.Message}");
                }
            }

            LogService.Debug($"[ISCSI] #{id} ← Descubrir OK ({destinos.Count} destinos)");
            return destinos;
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Descubrir: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Discovery failed: {ex.Message}", 6000, "critical");
            return destinos;
        }
    }
}


    

    // ======================================================================
    //  COMPLETAR INFORMACIÓN — DevicePath, PartitionPath, MountPoint, FS
    // ======================================================================
    
   
    
   public static async Task CompletarInformacionDestino(IscsiDestino d, long parentId)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → CompletarInformacion IQN='{d.Iqn}'");

    try
    {
        if (!d.Conectado)
        {
            d.TieneFilesystem = false;
            d.FsType = "";
            d.MountPoint = "";
            LogService.Debug($"[ISCSI] #{id} Destino no conectado");
            return;
        }

        var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(l =>
            l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
            l.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase) &&
            l.Contains("lun", StringComparison.OrdinalIgnoreCase)
        );

        if (match != null)
        {
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();
            LogService.Debug($"[ISCSI] #{id} DevicePath='{d.DevicePath}'");
        }
        else
        {
            LogService.Error($"[ISCSI] #{id} No se encontró DevicePath");
            return;
        }

        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        d.PartitionPath = lines.Length > 1
            ? "/dev/" + lines[1].Trim()
            : d.DevicePath;

        LogService.Debug($"[ISCSI] #{id} PartitionPath='{d.PartitionPath}'");

        var mounts = ShellHelper.EjecutarComoRoot("mount").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var mline = mounts.FirstOrDefault(l => l.Contains(d.PartitionPath));

        if (mline != null)
        {
            var parts = mline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                d.MountPoint = parts[2];
                LogService.Debug($"[ISCSI] #{id} MountPoint='{d.MountPoint}'");
            }
        }

        var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");

        d.TieneFilesystem =
            !string.IsNullOrWhiteSpace(blkid.Stdout) &&
            blkid.Stdout.Contains("TYPE=");

        if (d.TieneFilesystem)
        {
            d.FsType = DetectarFsType(blkid.Stdout);
            LogService.Debug($"[ISCSI] #{id} FsType='{d.FsType}'");
        }

        LogService.Debug($"[ISCSI] #{id} TieneFilesystem={d.TieneFilesystem}");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR CompletarInformacion: {ex.Message}");
    }
}


    

    // ======================================================================
    //  CONECTAR — Montaje avanzado, robusto y con instrumentación
    // ======================================================================
  
 public static async Task Conectar(IscsiDestino d)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → Conectar IQN='{d.Iqn}', IP='{d.Ip}'");

    using (LoadingService.Show($"Connecting to {d.Iqn}..."))
    {
        try
        {
            // ============================================================
            // 1) Crear mountpoint usando ConfigManager
            // ============================================================
            string userBase = ConfigManager.MountBasePath; // ← CAMBIO REAL
            d.MountPoint = Path.Combine(userBase, SanitizarNombre(d.Iqn));

            LogService.Debug($"[ISCSI] #{id} MountPoint='{d.MountPoint}'");

            if (Directory.Exists(d.MountPoint))
            {
                try { Directory.GetFileSystemEntries(d.MountPoint); }
                catch
                {
                    LogService.Error($"[ISCSI] #{id} MountPoint corrupto → limpiando...");
                    ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
                }
            }

            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);

                // Aplicar permisos configurados
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            // ============================================================
            // 2) Comprobar si ya está conectado
            // ============================================================
            var sesiones = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout
            );

            bool yaConectado = sesiones.Contains(d.Iqn);
            LogService.Debug($"[ISCSI] #{id} yaConectado={yaConectado}");

            // ============================================================
            // 3) LOGIN iSCSI (si no está conectado)
            // ============================================================
            if (!yaConectado)
            {
                LogService.Debug($"[ISCSI] #{id} Buscando portal válido...");

                var discoveryTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m discovery -t sendtargets -p {d.Ip}"
                    )
                );

                var completed = await Task.WhenAny(discoveryTask, Task.Delay(5000));

                if (completed != discoveryTask)
                {
                    LogService.Error($"[ISCSI] #{id} TIMEOUT en discovery");
                    NotificadorLinux.Enviar($"[TIMEOUT] Connecting to {d.Iqn} took too long.", 6000, "critical");
                    return;
                }

                var discovery = discoveryTask.Result;

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
                    LogService.Debug($"[ISCSI] #{id} Probando portal {portal}...");

                    var result = await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {portal}"
                        )
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
                LogService.Debug($"[ISCSI] #{id} Portal válido: {d.Ip}");

                await Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=new"
                    )
                );

                // ============================================================
                // 4) CHAP / MUTUAL CHAP
                // ============================================================
                if (d.UsaChap || d.UsaMutualChap)
                {
                    LogService.Debug($"[ISCSI] #{id} Configurando CHAP...");

                    await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
                        )
                    );

                    if (d.UsaChap)
                    {
                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot(
                                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username --value={d.UsuarioChap}"
                            )
                        );

                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot(
                                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password --value={d.PasswordChap}"
                            )
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot(
                                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username_in --value={d.UsuarioMutualChap}"
                            )
                        );

                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot(
                                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password_in --value={d.PasswordMutualChap}"
                            )
                        );
                    }
                }

                // ============================================================
                // 5) LOGIN
                // ============================================================
                LogService.Debug($"[ISCSI] #{id} Realizando login...");

                var loginTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --login"
                    )
                );

                var loginCompleted = await Task.WhenAny(loginTask, Task.Delay(5000));

                if (loginCompleted != loginTask)
                {
                    LogService.Error($"[ISCSI] #{id} TIMEOUT en login");
                    NotificadorLinux.Enviar($"[TIMEOUT] Login to {d.Iqn} took too long.", 6000, "critical");
                    return;
                }
            }

            // ============================================================
            // 6) DETECTAR SYMLINK
            // ============================================================
            d.DevicePath = null;

            for (int i = 0; i < 10; i++)
            {
                var byPath = await Task.Run(() =>
                    ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                );

                var match = byPath.FirstOrDefault(l =>
                    l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("lun", StringComparison.OrdinalIgnoreCase)
                );

                if (match != null)
                {
                    d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    LogService.Debug($"[ISCSI] #{id} DevicePath='{d.DevicePath}'");
                    break;
                }

                await Task.Delay(200);
            }

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception($"No se encontró symlink para {d.Iqn}");

            // ============================================================
            // 7) DETECTAR PARTICIÓN
            // ============================================================
            var lsblk2 = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}")
            );

            var lines2 = lsblk2.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines2.Length > 1
                ? "/dev/" + lines2[1].Trim()
                : d.DevicePath;

            LogService.Debug($"[ISCSI] #{id} PartitionPath='{d.PartitionPath}'");

            // ============================================================
            // 8) DETECTAR FILESYSTEM
            // ============================================================
            var blkid = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}")
            );

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                LogService.Write($"[ISCSI] #{id} No filesystem detected.");
                d.TieneFilesystem = false;
                d.Conectado = true;
                return;
            }

            d.TieneFilesystem = true;
            string fsType = DetectarFsType(blkid.Stdout);
            d.FsType = fsType;

            LogService.Debug($"[ISCSI] #{id} Filesystem='{fsType}'");

            // ============================================================
            // 9) MONTAR
            // ============================================================
            var mpCheck = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"")
            );

            if (mpCheck.ExitCode != 0)
            {
                LogService.Debug($"[ISCSI] #{id} Montando volumen...");

                string mountFs = fsType == "ntfs" ? "ntfs-3g" : fsType;

                await Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                    )
                );
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Ya estaba montado.");
            }

            d.Conectado = true;
            NotificadorLinux.Enviar($"Target {d.Iqn} mounted in {d.MountPoint}");

            LogService.Debug($"[ISCSI] #{id} ← Conectar OK");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Conectar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to connect target {d.Iqn}", 6000, "critical");
        }
    }
}

    

    // ======================================================================
    //  DESCONECTAR — desmontaje avanzado, limpieza real e instrumentación
    // ======================================================================
    
   public static async Task Desconectar_Borrar(IscsiDestino d)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → Desconectar_Borrar IQN='{d.Iqn}', MP='{d.MountPoint}'");

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Desmontar si está montado
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = await Task.Run(() =>
                    ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"")
                );

                if (mpCheck.ExitCode == 0)
                {
                    LogService.Debug($"[ISCSI] #{id} Desmontando volumen...");
                    await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"")
                    );
                    await Task.Delay(300);

                    mpCheck = await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"")
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        LogService.Debug($"[ISCSI] #{id} WARN: volumen sigue montado, forzando umount...");
                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"")
                        );
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                try
                {
                    LogService.Debug($"[ISCSI] #{id} Eliminando directorio de montaje...");
                    await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"")
                    );
                }
                catch (Exception ex)
                {
                    LogService.Error($"[ISCSI] #{id} WARN: No se pudo eliminar el directorio: {ex.Message}");
                }
            }

            // --------------------------------------------------------------
            // 3) Cerrar sesión iSCSI
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Comprobando sesiones iSCSI...");

            var sesiones = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout
            );

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Debug($"[ISCSI] #{id} Cerrando sesión iSCSI...");

                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                var completed = await Task.WhenAny(logoutTask, Task.Delay(5000));

                if (completed != logoutTask)
                {
                    LogService.Error($"[ISCSI] #{id} TIMEOUT en logout");
                    NotificadorLinux.Enviar($"[TIMEOUT] Logout from {d.Iqn} took too long.", 6000, "critical");
                }
                else
                {
                    await Task.Delay(300);
                }
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} No hay sesión activa, se omite logout.");
            }

            // --------------------------------------------------------------
            // 4) Eliminar nodo iSCSI
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Eliminando nodo iSCSI...");

            await Task.Run(() =>
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete"
                )
            );

            // --------------------------------------------------------------
            // 5) Reset de propiedades del destino
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            NotificadorLinux.Enviar($"Target {d.Iqn} disconnected");

            LogService.Debug($"[ISCSI] #{id} ← Desconectar_Borrar OK en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar_Borrar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to disconnect target {d.Iqn}", 6000, "critical");
        }
    }
}

//----------

 
public static async Task Desconectar(IscsiDestino d)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → Desconectar IQN='{d.Iqn}', MP='{d.MountPoint}'");

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Desmontar si está montado
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = await Task.Run(() =>
                    ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"")
                );

                if (mpCheck.ExitCode == 0)
                {
                    LogService.Debug($"[ISCSI] #{id} Desmontando volumen...");
                    await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"")
                    );
                    await Task.Delay(300);

                    mpCheck = await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"")
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        LogService.Debug($"[ISCSI] #{id} WARN: volumen sigue montado, forzando umount...");
                        await Task.Run(() =>
                            ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"")
                        );
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                try
                {
                    LogService.Debug($"[ISCSI] #{id} Eliminando directorio de montaje...");
                    await Task.Run(() =>
                        ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"")
                    );
                }
                catch (Exception ex)
                {
                    LogService.Error($"[ISCSI] #{id} WARN: No se pudo eliminar el directorio: {ex.Message}");
                }
            }

            // --------------------------------------------------------------
            // 3) Cerrar sesión iSCSI
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Comprobando sesiones iSCSI...");

            var sesiones = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout
            );

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Debug($"[ISCSI] #{id} Cerrando sesión iSCSI...");

                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                var completed = await Task.WhenAny(logoutTask, Task.Delay(5000));

                if (completed != logoutTask)
                {
                    LogService.Error($"[ISCSI] #{id} TIMEOUT en logout");
                    NotificadorLinux.Enviar($"[TIMEOUT] Logout from {d.Iqn} took too long.", 6000, "critical");
                }
                else
                {
                    await Task.Delay(300);
                }
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} No hay sesión activa, se omite logout.");
            }

            // --------------------------------------------------------------
            // 4) NO BORRAR EL NODO (diferencia clave)
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Nodo iSCSI conservado (no se elimina).");

            // --------------------------------------------------------------
            // 5) Reset de propiedades del destino
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            NotificadorLinux.Enviar($"Target {d.Iqn} disconnected");

            LogService.Debug($"[ISCSI] #{id} ← Desconectar OK en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to disconnect target {d.Iqn}", 6000, "critical");
        }
    }
}


//-------
    
    
    // ======================================================================
    //  PERSISTENCIA — fstab + systemd
    // ======================================================================
    
    public static async Task AplicarPersistencia(IscsiDestino d)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → AplicarPersistencia IQN='{d.Iqn}', Persistir={d.Persistir}");

    using (LoadingService.Show($"Applying persistence for {d.Iqn}..."))
    {
        try
        {
            var task = Task.Run(() =>
            {
                // ============================================================
                // Asegurar que el mountpoint existe (ConfigManager)
                // ============================================================
                if (!Directory.Exists(d.MountPoint))
                {
                    Directory.CreateDirectory(d.MountPoint);
                    ShellHelper.EjecutarComoRoot(
                        $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                    );
                }

                if (d.Persistir)
                {
                    LogService.Debug($"[ISCSI] #{id} Guardando persistencia en fstab/systemd...");
                    GuardarEnFstab(d, id);
                    CrearServicioLogin(d, id);
                    CrearMountUnit(d, d.FsType, id);
                    HabilitarServicios(id);
                }
                else
                {
                    LogService.Debug($"[ISCSI] #{id} Eliminando persistencia...");
                    EliminarDeFstab(d, id);
                    EliminarServicioSystemd(d, id);
                    EliminarMountUnit(d, id);
                    ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
                }
            });

            var completed = await Task.WhenAny(task, Task.Delay(5000));

            if (completed != task)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en AplicarPersistencia");
                NotificadorLinux.Enviar($"[TIMEOUT] Persistence operation for {d.Iqn} took too long.", 6000, "critical");
                return;
            }

            LogService.Debug($"[ISCSI] #{id} ← AplicarPersistencia OK en {sw.ElapsedMilliseconds} ms");
            NotificadorLinux.Enviar($"Persistence updated for {d.Iqn}", 4000, "normal");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR AplicarPersistencia: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to apply persistence for {d.Iqn}", 6000, "critical");
        }
    }
}



    // ======================================================================
    //  FSTAB — Guardar entrada persistente
    // ======================================================================
 
    private static async Task GuardarEnFstab(IscsiDestino d, long id)
{
    using (LoadingService.Show($"Updating fstab for {d.Iqn}..."))
    {
        try
        {
            if (!d.TieneFilesystem || string.IsNullOrWhiteSpace(d.MountPoint))
            {
                LogService.Write($"[ISCSI] #{id} No se puede persistir: no hay filesystem o mountpoint.");
                return;
            }

            // ============================================================
            // Asegurar que el mountpoint existe y tiene permisos correctos
            // ============================================================
            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            string entry = $"{d.PartitionPath} {d.MountPoint} auto _netdev 0 0";
            LogService.Debug($"[ISCSI] #{id} Añadiendo entrada a fstab: {entry}");

            var task = Task.Run(() =>
            {
                // Eliminar entradas previas
                ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.PartitionPath}#d\" /etc/fstab");
                ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.MountPoint}#d\" /etc/fstab");

                // Añadir nueva entrada
                ShellHelper.EjecutarComoRoot(
                    $"sh -c \"echo '{entry}' >> /etc/fstab\""
                );
            });

            var completed = await Task.WhenAny(task, Task.Delay(5000));

            if (completed != task)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en GuardarEnFstab");
                NotificadorLinux.Enviar($"[TIMEOUT] Updating fstab for {d.Iqn} took too long.", 6000, "critical");
                return;
            }

            LogService.Debug($"[ISCSI] #{id} fstab actualizado correctamente.");
            NotificadorLinux.Enviar($"fstab updated for {d.Iqn}", 4000, "normal");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR GuardarEnFstab: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to update fstab for {d.Iqn}", 6000, "critical");
        }
    }
}



    // ======================================================================
    //  FSTAB — Eliminar entrada persistente
    // ======================================================================
   
    private static async Task EliminarDeFstab(IscsiDestino d, long id)
{
    using (LoadingService.Show($"Removing fstab entry for {d.Iqn}..."))
    {
        try
        {
            if (string.IsNullOrWhiteSpace(d.MountPoint))
            {
                LogService.Write($"[ISCSI] #{id} No se puede eliminar de fstab: mountpoint vacío.");
                return;
            }

            // ============================================================
            // Asegurar que el mountpoint existe (ConfigManager)
            // ============================================================
            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            LogService.Debug($"[ISCSI] #{id} Eliminando entrada de fstab para {d.MountPoint}");

            var task = Task.Run(() =>
            {
                ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.MountPoint}#d\" /etc/fstab");
                ShellHelper.EjecutarComoRoot($"sed -i \"\\#{d.PartitionPath}#d\" /etc/fstab");
            });

            var completed = await Task.WhenAny(task, Task.Delay(5000));

            if (completed != task)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en EliminarDeFstab");
                NotificadorLinux.Enviar($"[TIMEOUT] Removing fstab entry for {d.Iqn} took too long.", 6000, "critical");
                return;
            }

            LogService.Debug($"[ISCSI] #{id} Entrada de fstab eliminada correctamente.");
            NotificadorLinux.Enviar($"fstab entry removed for {d.Iqn}", 4000, "normal");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR EliminarDeFstab: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to remove fstab entry for {d.Iqn}", 6000, "critical");
        }
    }
}



    // ======================================================================
    //  SYSTEMD — Crear servicio de login iSCSI
    // ======================================================================
  
    private static void CrearServicioLogin(IscsiDestino d, long id)
    {
        try
        {
            string safe = SystemdSafe(d.Iqn);
            string path = $"/etc/systemd/system/iscsi-login-{safe}.service";

            LogService.Debug($"[ISCSI] #{id} Creando servicio systemd: {path}");

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

            // Crear archivo
            ShellHelper.EjecutarComoRoot(
                $"sh -c \"echo '{contenido.Replace("'", "'\\''")}' > {path}\""
            );

            // Recargar systemd
            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // Habilitar servicio
            ShellHelper.EjecutarComoRoot($"systemctl enable iscsi-login-{safe}.service");

            LogService.Debug($"[ISCSI] #{id} Servicio creado correctamente: {path}");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR CrearServicioLogin: {ex.Message}");
        }
    }

    
    // ======================================================================
    //  SYSTEMD — Crear unidad .mount
    // ======================================================================
   
    private static void CrearMountUnit(IscsiDestino d, string fsType, long id)
    {
        try
        {
            // ============================================================
            // Asegurar que el mountpoint existe y tiene permisos correctos
            // ============================================================
            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            string safeMount = d.MountPoint
                .Trim('/')
                .Replace("/", "-")
                .Replace(".", "_");

            string unitName = $"{safeMount}.mount";
            string path = $"/etc/systemd/system/{unitName}";

            LogService.Debug($"[ISCSI] #{id} Creando mount unit: {path}");

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

            // Crear archivo .mount
            ShellHelper.EjecutarComoRoot(
                $"sh -c \"echo '{contenido.Replace("'", "'\\''")}' > {path}\""
            );

            // Recargar systemd
            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // Habilitar unidad
            ShellHelper.EjecutarComoRoot($"systemctl enable {unitName}");

            LogService.Debug($"[ISCSI] #{id} Mount unit creada correctamente: {path}");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR CrearMountUnit: {ex.Message}");
        }
    }


    
    // ======================================================================
    //  SYSTEMD — Eliminar servicio
    // ======================================================================
   
    private static void EliminarServicioSystemd(IscsiDestino d, long id)
    {
        try
        {
            string safe = SystemdSafe(d.Iqn);
            string service = $"/etc/systemd/system/iscsi-login-{safe}.service";

            LogService.Debug($"[ISCSI] #{id} Eliminando servicio systemd: {service}");

            if (File.Exists(service))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-login-{safe}.service");
                ShellHelper.EjecutarComoRoot($"rm -f {service}");

                LogService.Debug($"[ISCSI] #{id} Servicio eliminado correctamente: {service}");
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Servicio no existe, nada que eliminar: {service}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR EliminarServicioSystemd: {ex.Message}");
        }
    }

    
    // ======================================================================
    //  SYSTEMD — Eliminar mount unit
    // ======================================================================
  
    private static void EliminarMountUnit(IscsiDestino d, long id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(d.MountPoint))
            {
                LogService.Debug($"[ISCSI] #{id} MountPoint vacío, no hay mount unit que eliminar.");
                return;
            }

            string safeMount = d.MountPoint
                .Trim('/')
                .Replace("/", "-")
                .Replace(".", "_");

            string mountUnit = $"/etc/systemd/system/{safeMount}.mount";

            LogService.Debug($"[ISCSI] #{id} Eliminando mount unit: {mountUnit}");

            if (File.Exists(mountUnit))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable {safeMount}.mount");
                ShellHelper.EjecutarComoRoot($"rm -f {mountUnit}");

                LogService.Debug($"[ISCSI] #{id} Mount unit eliminada correctamente: {mountUnit}");
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Mount unit no existe, nada que eliminar: {mountUnit}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR EliminarMountUnit: {ex.Message}");
        }
    }

    
    // ======================================================================
    //  SYSTEMD — Habilitar servicios necesarios
    // ======================================================================
    private static void HabilitarServicios(long id)
    {
        try
        {
            LogService.Debug($"[ISCSI] #{id} Habilitando servicios systemd...");

            // iscsid es común en todas las distros
            ShellHelper.EjecutarComoRoot("systemctl enable iscsid");

            // Algunas distros usan open-iscsi, otras no
            var check = ShellHelper.EjecutarComoRoot(
                "systemctl list-unit-files | grep -q open-iscsi"
            );

            if (check.ExitCode == 0)
            {
                LogService.Debug($"[ISCSI] #{id} Habilitando open-iscsi...");
                ShellHelper.EjecutarComoRoot("systemctl enable open-iscsi");
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} open-iscsi no existe en este sistema (OK)");
            }

            LogService.Debug($"[ISCSI] #{id} Servicios habilitados correctamente.");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR HabilitarServicios: {ex.Message}");
        }
    }

    // ======================================================================
    //  DETECTAR PERSISTENCIA REAL
    // ======================================================================
    public static bool DetectarPersistencia(IscsiDestino d)
    {
        // Protección total: si no hay mountpoint, no puede haber persistencia
        if (d == null || string.IsNullOrWhiteSpace(d.MountPoint))
            return false;

        //  Protección: PartitionPath puede ser null justo después de conectar
        string part = d.PartitionPath ?? "";
        string mp = d.MountPoint ?? "";

        // --- FSTAB ---
        if (File.Exists("/etc/fstab"))
        {
            string fstab = File.ReadAllText("/etc/fstab") ?? "";

            if (!string.IsNullOrEmpty(fstab))
            {
                if ((!string.IsNullOrEmpty(part) && fstab.Contains(part)) ||
                    (!string.IsNullOrEmpty(mp) && fstab.Contains(mp)))
                {
                    return true;
                }
            }
        }

        // --- SYSTEMD SERVICE ---
        string safe = SystemdSafe(d.Iqn);
        string service = $"/etc/systemd/system/iscsi-login-{safe}.service";

        if (File.Exists(service))
            return true;

        // --- SYSTEMD MOUNT UNIT ---
        string safeMount = mp
            .Trim('/')
            .Replace("/", "-")
            .Replace(".", "_");

        if (!string.IsNullOrWhiteSpace(safeMount))
        {
            string mountUnit = $"/etc/systemd/system/{safeMount}.mount";
            if (File.Exists(mountUnit))
                return true;
        }

        return false;
    }

    // ======================================================================
    //  INICIALIZAR DESTINO — GPT + partición + formateo + montaje
    // ======================================================================

public static async Task InicializarDestino(IscsiDestino d, string label, string fsType)
{
    long id = NextTraceId();
    var sw = Stopwatch.StartNew();
    LogService.Debug($"[ISCSI] #{id} → InicializarDestino IQN='{d.Iqn}', FS='{fsType}', Label='{label}'");

    using (LoadingService.Show($"Initializing disk ({fsType})..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Asegurar que el destino está conectado
            // --------------------------------------------------------------
            if (!d.Conectado)
            {
                LogService.Debug($"[ISCSI] #{id} Destino no conectado → conectando...");
                await Conectar(d);
            }

            // ============================================================
            //  EJECUTAR TODA LA OPERACIÓN PESADA EN HILO SEPARADO
            // ============================================================
            var task = Task.Run(async () =>
            {
                // --------------------------------------------------------------
                // 2) Desmontar si está montado
                // --------------------------------------------------------------
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    LogService.Debug($"[ISCSI] #{id} Desmontando volumen...");
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }

                // --------------------------------------------------------------
                // 3) Borrar tabla de particiones
                // --------------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Borrando tabla de particiones...");
                ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {d.PartitionPath}");

                // --------------------------------------------------------------
                // 4) Crear tabla GPT
                // --------------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Creando tabla GPT...");
                ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mklabel gpt");

                // --------------------------------------------------------------
                // 5) Crear partición primaria
                // --------------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Creando partición primaria...");
                ShellHelper.EjecutarComoRoot($"parted -s {d.PartitionPath} mkpart primary 0% 100%");

                await Task.Delay(1200);

                // --------------------------------------------------------------
                // 6) Detectar nueva partición real
                // --------------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Detectando nueva partición...");
                var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
                var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 2)
                    throw new Exception("No se detectó la nueva partición tras crearla.");

                d.PartitionPath = "/dev/" + lines[1].Trim();
                LogService.Debug($"[ISCSI] #{id} Nueva partición detectada: {d.PartitionPath}");

                // --------------------------------------------------------------
                // 7) Formatear según filesystem elegido
                // --------------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Formateando en {fsType}...");

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
                // 8) Montaje automático compatible (NTFS → ntfs-3g)
                // --------------------------------------------------------------
                string mountFs = fsType == "ntfs" ? "ntfs-3g" : fsType;

                LogService.Debug($"[ISCSI] #{id} Montando volumen como {mountFs}...");
                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );

                // --------------------------------------------------------------
                // 9) Aplicar permisos configurados al mountpoint
                // --------------------------------------------------------------
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );

                // --------------------------------------------------------------
                // 10) Actualizar persistencia
                // --------------------------------------------------------------
                d.Persistir = DetectarPersistencia(d);
            });

            // ============================================================
            //  TIMEOUT REAL (10 segundos)
            // ============================================================
            var completed = await Task.WhenAny(task, Task.Delay(10000));

            if (completed != task)
            {
                LogService.Error($"[ISCSI] #{id} TIMEOUT en InicializarDestino");
                NotificadorLinux.Enviar($"[TIMEOUT] Initializing {d.Iqn} took too long.", 6000, "critical");
                return;
            }

            // --------------------------------------------------------------
            // 11) Notificación final
            // --------------------------------------------------------------
            NotificadorLinux.Enviar(
                $"Target {d.Iqn} initialized as {fsType} with label '{label}'"
            );

            LogService.Debug($"[ISCSI] #{id} ← InicializarDestino OK en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR InicializarDestino: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to initialize target {d.Iqn}", 6000, "critical");
        }
    }
}


    // ======================================================================
    //  DETECTAR SI EL SISTEMA SOPORTA UN FILESYSTEM
    // ======================================================================
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
