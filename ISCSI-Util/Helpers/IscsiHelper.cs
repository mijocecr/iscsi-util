using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var portalIscsi = port == 3260 ? ipSolo : $"{ipSolo}:{port}";

            var check = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi}"
            );

            var nodoExiste = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

            if (!nodoExiste)
            {
                LogService.Debug($"[ISCSI] #{id} Nodo no existe. Creándolo temporalmente...");
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=new"
                );
            }

            LogService.Debug($"[ISCSI] #{id} Leyendo configuración CHAP real...");

            var show = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} -o show"
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
                    $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=delete"
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
                d.SizeGb = 0;
                d.SizeBytes = 0;
                return;
            }

            // 2) Detectar symlink / dispositivo activo
            var byPathOut = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/ 2>/dev/null").Stdout ?? "";
            var byPath = byPathOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // La coincidencia debe ser por IQN + LUN: un mismo portal puede
            // exponer varios targets y un target varios LUNs.
            var match = IscsiByPath.Find(byPath, d.Iqn, d.LunId, fallbackToFirstLun: d.LunId == 0);

            if (match == null)
            {
                d.DevicePath = null;
                d.PartitionPath = null;
                d.MountPoint = null;
                d.TieneFilesystem = false;
                d.FsType = "";
                d.SizeGb = 0;
                d.SizeBytes = 0;
                return;
            }

            d.LunId = match.LunId;
            d.DevicePath = "/dev/disk/by-path/" + match.Link;

            // El tamaño debe ser el del LUN completo, no el de su partición.
            var sizeOutput = ShellHelper.EjecutarComoRoot(
                $"blockdev --getsize64 \"{d.DevicePath}\" 2>/dev/null"
            );

            if (sizeOutput.ExitCode != 0 ||
                !long.TryParse(sizeOutput.Stdout?.Trim(), out var sizeBytes) ||
                sizeBytes <= 0)
            {
                sizeOutput = ShellHelper.EjecutarComoRoot(
                    $"lsblk -b -dn -o SIZE \"{d.DevicePath}\""
                );

                long.TryParse(sizeOutput.Stdout?.Trim(), out sizeBytes);
            }

            d.SizeBytes = sizeBytes > 0 ? sizeBytes : 0;
            d.SizeGb = d.SizeBytes > 0
                ? (int)Math.Round(d.SizeBytes / 1_000_000_000d)
                : 0;

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
                targetPath = d.DevicePath;

            // El dispositivo efectivo puede ser una partición o el propio LUN
            // (superfloppy). Guardarlo siempre permite montar y persistir ambos.
            d.PartitionPath = targetPath;

            // 4) Detectar Filesystem
            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {targetPath}");
            d.FsType = DetectarFsType(blkid.Stdout ?? "");

            d.TieneFilesystem = !string.IsNullOrWhiteSpace(d.FsType) &&
                                !d.FsType.Equals("raw", StringComparison.OrdinalIgnoreCase);

            // 5) Detectar punto de montaje en runtime.
            // findmnt resuelve el dispositivo real y evita depender del texto de
            // `mount`, que muestra /dev/sdX aunque se montara por by-path.
            d.MountPoint = ObtenerPuntoMontajeReal(targetPath);

            // 6) Detectar persistencia
            d.Persistir = DetectarPersistencia(d);

            if (d.Persistir && string.IsNullOrEmpty(d.MountPoint))
            {
                d.MountPoint = IscsiNaming.MountPoint(
                    ConfigManager.MountBasePath,
                    d.Iqn,
                    d.LunId
                );
            }

            TraceOut(id, "CompletarInformacion");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR CompletarInformacion: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Devuelve el punto de montaje real de un dispositivo. Resuelve primero
    /// enlaces by-path para que findmnt compare contra el /dev/sdX del kernel.
    /// </summary>
    public static string? ObtenerPuntoMontajeReal(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return null;

        var real = ShellHelper.EjecutarComoRoot($"readlink -f \"{device}\"").Stdout?.Trim();
        if (string.IsNullOrWhiteSpace(real))
            real = device;

        var result = ShellHelper.EjecutarComoRoot(
            $"findmnt -rn -S \"{real}\" -o TARGET"
        );

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return null;

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();
    }

    /// <summary>
    /// Comprueba si una ruta es un punto de montaje activo.
    /// </summary>
    public static bool EstaMontado(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            return false;

        var result = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{mountPoint}\"");
        return result.ExitCode == 0;
    }

    public static async Task ConectarSesion(SessionInfo s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.Iqn) || string.IsNullOrWhiteSpace(s.Portal))
            return;

        var target = new IscsiDestino
        {
            Iqn = s.Iqn,
            Ip = s.Portal,
            PortalReal = s.Portal,
            LunId = s.LunId
        };

        try
        {
            await Conectar(target, reutilizarAutenticacionGuardada: true);
            ActualizarSesion(s, target);
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] Error al conectar sesión {s.Iqn}: {ex.Message}");
        }
    }

    public static async Task MontarSesion(SessionInfo s)
    {
        await ConectarSesion(s);
    }

    public static Task DesmontarSesion(SessionInfo s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.MountPoint))
            return Task.CompletedTask;

        var check = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{s.MountPoint}\"");
        if (check.ExitCode == 32)
        {
            s.MountPoint = "";
            return Task.CompletedTask;
        }
        if (check.ExitCode != 0)
        {
            LogService.Error($"[ISCSI] No se pudo comprobar el montaje {s.MountPoint}: {check.Stderr}");
            NotificadorLinux.Enviar($"[ERROR] No se pudo comprobar el montaje {s.MountPoint}.", 6000, "critical");
            return Task.CompletedTask;
        }

        var result = ShellHelper.EjecutarComoRoot($"umount -- \"{s.MountPoint}\"");
        if (result.ExitCode != 0)
        {
            LogService.Error($"[ISCSI] No se pudo desmontar {s.MountPoint}: {result.Stderr}");
            NotificadorLinux.Enviar($"[ERROR] No se pudo desmontar {s.MountPoint}.", 6000, "critical");
            return Task.CompletedTask;
        }

        s.MountPoint = "";
        return Task.CompletedTask;
    }
    
    
    public static Task DesmontarDestino(IscsiDestino destino)
    {
        if (destino == null || string.IsNullOrWhiteSpace(destino.MountPoint))
            return Task.CompletedTask;

        var check = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{destino.MountPoint}\"");
        if (check.ExitCode == 32)
        {
            destino.MountPoint = "";
            return Task.CompletedTask;
        }
        if (check.ExitCode != 0)
        {
            LogService.Error($"[ISCSI] No se pudo comprobar el montaje {destino.MountPoint}: {check.Stderr}");
            NotificadorLinux.Enviar($"[ERROR] No se pudo comprobar el montaje {destino.MountPoint}.", 6000, "critical");
            return Task.CompletedTask;
        }

        var result = ShellHelper.EjecutarComoRoot($"umount -- \"{destino.MountPoint}\"");
        if (result.ExitCode != 0)
        {
            LogService.Error($"[ISCSI] No se pudo desmontar {destino.MountPoint}: {result.Stderr}");
            NotificadorLinux.Enviar($"[ERROR] No se pudo desmontar {destino.MountPoint}.", 6000, "critical");
            return Task.CompletedTask;
        }

        destino.MountPoint = "";
        return Task.CompletedTask;
    }
    
    
    public static Task MontarDestino(IscsiDestino destino)
    {
        if (destino == null || string.IsNullOrWhiteSpace(destino.MountPoint))
            return Task.CompletedTask;

        // Montar la partición si existe; en discos superfloppy el LUN completo
        // es el dispositivo efectivo.
        string source = !string.IsNullOrWhiteSpace(destino.PartitionPath)
            ? destino.PartitionPath
            : destino.DevicePath ?? "";

        if (string.IsNullOrWhiteSpace(source))
            return Task.CompletedTask;

        // Asegurarse de que el directorio de montaje existe
        if (!Directory.Exists(destino.MountPoint))
        {
            try
            {
                Directory.CreateDirectory(destino.MountPoint);
            }
            catch (Exception ex)
            {
                LogService.Error($"[ISCSI] No se pudo crear {destino.MountPoint}: {ex.Message}");
                NotificadorLinux.Enviar("[ERROR] No se pudo crear el punto de montaje.", 6000, "critical");
                return Task.CompletedTask;
            }
        }

        string fs = (destino.FsType ?? "").Trim().ToLowerInvariant();
        string fsDriver = fs == "ntfs" ? "ntfs-3g" : fs;

        string usuarioReal = Environment.GetEnvironmentVariable("SUDO_USER") ?? "";
        if (string.IsNullOrWhiteSpace(usuarioReal) || usuarioReal == "root")
            usuarioReal = ShellHelper.EjecutarComoRoot("logname").Stdout?.Trim() ?? "1000";

        string uid = ShellHelper.EjecutarComoRoot($"id -u {usuarioReal}").Stdout?.Trim() ?? "1000";
        string gid = ShellHelper.EjecutarComoRoot($"id -g {usuarioReal}").Stdout?.Trim() ?? "1000";

        string mountCommand;
        if (fs is "ntfs" or "exfat" or "vfat")
        {
            mountCommand = $"mount -t {fsDriver} -o uid={uid},gid={gid},umask=000 " +
                           $"\"{source}\" \"{destino.MountPoint}\"";
        }
        else
        {
            mountCommand = string.IsNullOrWhiteSpace(fs)
                ? $"mount \"{source}\" \"{destino.MountPoint}\""
                : $"mount -t {fs} \"{source}\" \"{destino.MountPoint}\"";
        }

        var result = ShellHelper.EjecutarComoRoot(mountCommand);
        if (result.ExitCode != 0)
        {
            LogService.Error($"[ISCSI] No se pudo montar {source} en {destino.MountPoint}: {result.Stderr}");
            NotificadorLinux.Enviar("[ERROR] No se pudo montar el volumen.", 6000, "critical");
            return Task.CompletedTask;
        }

        // Permisos de escritura para el usuario de la sesión en filesystems POSIX.
        if (fs is not ("ntfs" or "exfat" or "vfat" or "iso9660"))
            ShellHelper.EjecutarComoRoot($"setfacl -m u:{uid}:rwx \"{destino.MountPoint}\"");

        destino.MountPoint = ObtenerPuntoMontajeReal(source) ?? destino.MountPoint;
        return Task.CompletedTask;
    }
    

    public static async Task DesconectarSesion(SessionInfo s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.Iqn) || string.IsNullOrWhiteSpace(s.Portal))
            return;

        if (!string.IsNullOrWhiteSpace(s.MountPoint))
        {
            var mountPointCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{s.MountPoint}\"");
            if (mountPointCheck.ExitCode == 0)
            {
                NotificadorLinux.Enviar("Desmonta el volumen antes de desconectar la sesión.", 5000, "warning");
                return;
            }
            if (mountPointCheck.ExitCode != 32)
            {
                LogService.Error($"[ISCSI] No se pudo comprobar el montaje {s.MountPoint}: {mountPointCheck.Stderr}");
                NotificadorLinux.Enviar("[ERROR] No se pudo comprobar el montaje del destino.", 6000, "critical");
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(s.Device))
        {
            var mountedDevice = ShellHelper.EjecutarComoRoot(
                $"findmnt -rn -S \"{s.Device}\" -o TARGET");
            if (mountedDevice.ExitCode > 1)
            {
                LogService.Error($"[ISCSI] No se pudo comprobar si {s.Device} está montado: {mountedDevice.Stderr}");
                NotificadorLinux.Enviar("[ERROR] No se pudo comprobar si el destino está montado.", 6000, "critical");
                return;
            }

            if (mountedDevice.ExitCode == 0 && !string.IsNullOrWhiteSpace(mountedDevice.Stdout))
            {
                LogService.Error($"[ISCSI] No se desconecta {s.Iqn}: el dispositivo sigue montado.");
                NotificadorLinux.Enviar("Desmonta el volumen antes de desconectar la sesión.", 5000, "warning");
                return;
            }
        }

        var cmd = $"iscsiadm -m node -T {s.Iqn} -p {s.Portal} --logout";
        var res = ShellHelper.EjecutarComoRoot(cmd);

        if (res.ExitCode == 0)
        {
            s.Connected = false;
            s.MountPoint = "";
            s.Device = "";
            s.Filesystem = "";
        }
        else
        {
            LogService.Error($"[ISCSI] Error al desconectar sesión {s.Iqn}: {res.Stderr}");
            NotificadorLinux.Enviar($"[ERROR] No se pudo desconectar {s.Iqn}.", 6000, "critical");
            return;
        }

        await Task.CompletedTask;
    }

    private static void ActualizarSesion(SessionInfo session, IscsiDestino target)
    {
        session.Connected = target.Conectado;
        session.LunId = target.LunId;
        session.Device = target.PartitionPath ?? target.DevicePath ?? "";
        session.Filesystem = target.FsType ?? "";
        session.MountPoint = target.TieneFilesystem ? target.MountPoint ?? "" : "";
        if (target.Conectado && session.ConnectedSince == default)
            session.ConnectedSince = DateTime.Now;
    }

    // ======================================================================
    //  CONECTAR — Login iSCSI + detección + montaje
    // ======================================================================

    // ============================================================
// LIMPIAR AUTO-MOUNTS (CachyOS, GVFS, Udisks2)
// ============================================================
private static void ForceUnmountAutoMounts(string partition, string? keepMountPoint = null)
{
    if (string.IsNullOrWhiteSpace(partition))
        return;

    var mounts = ShellHelper.EjecutarComoRoot(
        $"findmnt -rn -o TARGET {partition}"
    ).Stdout?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    string basePath = (ConfigManager.MountBasePath ?? "").TrimEnd('/');

    foreach (var raw in mounts)
    {
        string mp = raw.Trim();
        if (mp.Length == 0)
            continue;

        // No desmontar el punto de montaje estable de esta aplicación ni el
        // que se acaba de calcular para este LUN.
        if (!string.IsNullOrWhiteSpace(keepMountPoint) &&
            string.Equals(mp, keepMountPoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            continue;

        if (basePath.Length > 0 &&
            (string.Equals(mp, basePath, StringComparison.OrdinalIgnoreCase) ||
             mp.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)))
            continue;

        ShellHelper.EjecutarComoRoot($"umount -l --force \"{mp}\"");
    }
}


// ============================================================
// MÉTODO CONECTAR (TU VERSIÓN, PERO FUNCIONAL)
// ============================================================
public static Task Conectar(IscsiDestino d)
{
    return Conectar(d, reutilizarAutenticacionGuardada: false);
}

private static async Task Conectar(IscsiDestino d, bool reutilizarAutenticacionGuardada)
{
    if (d == null) return;

    var id = NextTraceId();
    TraceIn(id, "Conectar", d.Iqn);

    LogService.Debug($"[ISCSI] #{id} >>> INICIO Conectar() para IQN={d.Iqn}, IP={d.Ip}");

    try
    {
        // ============================================================
        // 1) Comprobar si ya está conectado
        // ============================================================
        var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout ?? "";
        var yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

        var (ipSolo, port) = NormalizarPortal(d.Ip);
        var portalIscsi = port == 3260 ? ipSolo : $"{ipSolo}:{port}";

        // ============================================================
        // 2) LOGIN iSCSI
        // ============================================================
        if (!yaConectado)
        {
            var checkNode = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi}"
            );

            var nodoExiste = !checkNode.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

            if (!nodoExiste)
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=new"
                );

            // ============================================================
            // CAMBIO CLAVE / SOPORTE CHAP:
            // Se eliminó la restricción exclusiva de '!reutilizarAutenticacionGuardada'.
            // Ahora, si el destino requiere CHAP o Mutual CHAP (y tenemos credenciales),
            // actualizamos siempre la base de datos del nodo en open-iscsi antes del login.
            // Esto asegura que al conectar desde la pestaña de sesiones (donde el nodo ya existe),
            // las credenciales se inyecten correctamente y no falle por autenticación.
            // ============================================================
            if (d.UsaChap || d.UsaMutualChap)
            {
                // Forzar el método de autenticación a CHAP en el registro del nodo
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.authmethod --value=CHAP"
                );

                if (d.UsaChap)
                {
                    var user = string.IsNullOrWhiteSpace(d.UsuarioChap) ? d.LocalUser : d.UsuarioChap;
                    var pass = string.IsNullOrWhiteSpace(d.PasswordChap) ? d.LocalPass : d.PasswordChap;

                    if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.username --value=\"{user}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.password --value=\"{pass}\""
                        );
                    }
                }

                if (d.UsaMutualChap)
                {
                    var userIn = string.IsNullOrWhiteSpace(d.UsuarioMutualChap)
                        ? d.LocalUserIn
                        : d.UsuarioMutualChap;
                    var passIn = string.IsNullOrWhiteSpace(d.PasswordMutualChap)
                        ? d.LocalPassIn
                        : d.PasswordMutualChap;

                    if (!string.IsNullOrWhiteSpace(userIn) && !string.IsNullOrWhiteSpace(passIn))
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
                        );
                    }
                }
            }
            else if (!reutilizarAutenticacionGuardada)
            {
                // Si no utiliza CHAP, restablecemos el método a None por seguridad
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --op=update --name node.session.auth.authmethod --value=None"
                );
            }

            var loginResult = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalIscsi} --login"
            );

            if (loginResult.ExitCode != 0 &&
                !loginResult.Stderr.Contains("already present", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Login iSCSI falló: {loginResult.Stderr}");

            await Task.Delay(300);
        }

        // ============================================================
        // 3) Detectar symlink correcto
        // ============================================================
        d.DevicePath = null;

        for (var i = 0; i < 10; i++)
        {
            var byPathOut = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout ?? "";
            var byPath = byPathOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var match = IscsiByPath.Find(
                byPath,
                d.Iqn,
                d.LunId,
                fallbackToFirstLun: d.LunId == 0
            );

            if (match != null)
            {
                d.LunId = match.LunId;
                d.DevicePath = "/dev/disk/by-path/" + match.Link;
                break;
            }

            await Task.Delay(200);
        }

        if (string.IsNullOrWhiteSpace(d.DevicePath))
            throw new Exception("No se encontró symlink del dispositivo iSCSI.");

        // El LUN real puede haberse resuelto durante la detección (p. ej. un
        // target de un solo LUN distinto de 0). Recalcular la ruta ahora para
        // que el punto de montaje y la unidad systemd usen el LUN definitivo.
        d.MountPoint = IscsiNaming.MountPoint(
            ConfigManager.MountBasePath,
            d.Iqn,
            d.LunId
        );

        if (!Directory.Exists(d.MountPoint))
        {
            Directory.CreateDirectory(d.MountPoint);
            ShellHelper.EjecutarComoRoot(
                $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
            );
        }

        // ============================================================
        // 4) Detectar partición correcta
        // ============================================================
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

        // ============================================================
        // 5) LIMPIAR AUTO-MOUNTS DE CACHYOS
        // ============================================================
        ForceUnmountAutoMounts(d.PartitionPath, d.MountPoint);

        // ============================================================
        // 6) Detectar filesystem
        // ============================================================
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

        if (!EsFilesystemMontable(d.FsType))
        {
            d.TieneFilesystem = false;
            d.Conectado = true;
            TraceOut(id, "Conectar", $"NO_MOUNTABLE_FS:{d.FsType}");
            return;
        }

        // ============================================================
        // 7) Montar con permisos eficientes (sin chown -R)
        // ============================================================
        var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

        if (mpCheck.ExitCode != 0)
        {
            // A) Obtener usuario real
            string usuarioReal = Environment.GetEnvironmentVariable("SUDO_USER");
            if (string.IsNullOrWhiteSpace(usuarioReal) || usuarioReal == "root")
            {
                usuarioReal = ShellHelper.EjecutarComoRoot("logname").Stdout?.Trim() ?? "1000";
            }

            string uid = ShellHelper.EjecutarComoRoot($"id -u {usuarioReal}").Stdout?.Trim() ?? "1000";
            string gid = ShellHelper.EjecutarComoRoot($"id -g {usuarioReal}").Stdout?.Trim() ?? "1000";

            string fsLower = (d.FsType ?? "").ToLowerInvariant();

            if (fsLower == "ntfs" || fsLower == "exfat" || fsLower == "vfat")
            {
                string driver = fsLower == "ntfs" ? "ntfs-3g" : fsLower;
                ShellHelper.EjecutarComoRoot(
                    $"mount -t {driver} -o uid={uid},gid={gid},umask=000 {d.PartitionPath} \"{d.MountPoint}\""
                );
            }
            else
            {
                // EXT4/XFS/BTRFS → montar normal
                ShellHelper.EjecutarComoRoot(
                    $"mount -t {d.FsType} {d.PartitionPath} \"{d.MountPoint}\""
                );

                // ACL instantáneo
                ShellHelper.EjecutarComoRoot($"setfacl -m u:{uid}:rwx \"{d.MountPoint}\"");
            }
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
            var serviceName = IscsiNaming.ServiceName(d.Iqn, d.LunId);

            var scriptPath = IscsiNaming.ScriptPath(d.Iqn, d.LunId);
            var servicePath = IscsiNaming.ServicePath(d.Iqn, d.LunId);

            ShellHelper.EjecutarComoRoot($"systemctl disable {serviceName}.service");
            ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

            if (!string.IsNullOrWhiteSpace(d.MountPoint))
                FstabHelper.RemoveMountPoint(d.MountPoint);

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

        if (FstabHelper.HasMountPoint(d.MountPoint))
            return true;

        try
        {
            var service = IscsiNaming.ServicePath(d.Iqn, d.LunId);

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
            // 1) LOCALIZAR SERVICIOS PERSISTENTES DEL TARGET Y TODOS SUS LUNs
            // El nombre base cubre tanto el servicio histórico (LUN 0) como
            // los sufijos -lunN creados para el resto de unidades lógicas.
            var serviceBase = IscsiNaming.ServiceName(d.Iqn, 0);
            var unitList = ShellHelper.EjecutarComoRoot(
                $"ls -1 /etc/systemd/system/{serviceBase}*.service 2>/dev/null"
            ).Stdout ?? "";

            var unitFiles = unitList
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            // 2) RECOPILAR LOS PUNTOS DE MONTAJE DE TODOS LOS LUNs
            // Un target puede exponer varias unidades; al borrarlo no deben
            // quedar montajes, entradas de fstab ni directorios huérfanos.
            var mountPoints = new HashSet<string>(StringComparer.Ordinal);

            var configuredMountPoint = ObtenerPuntoMontaje(d);
            if (!string.IsNullOrWhiteSpace(configuredMountPoint))
                mountPoints.Add(configuredMountPoint);

            // Los servicios persistidos permiten recuperar incluso los LUNs
            // que ya no están conectados en este momento.
            foreach (var unitPath in unitFiles)
            {
                var unitContent = ShellHelper.EjecutarComoRoot(
                    $"cat \"{unitPath}\" 2>/dev/null"
                ).Stdout ?? "";

                foreach (var line in unitContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    const string prefix = "ExecStart=/usr/bin/mount ";
                    var trimmedLine = line.TrimStart();
                    if (!trimmedLine.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    var mp = trimmedLine[prefix.Length..].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(mp))
                        mountPoints.Add(mp);
                }
            }

            var byPath = (ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/ 2>/dev/null").Stdout ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var lun in IscsiByPath.ParseAll(byPath)
                         .Where(e => e.Iqn.Equals(d.Iqn, StringComparison.OrdinalIgnoreCase)))
            {
                mountPoints.Add(IscsiNaming.MountPoint(
                    ConfigManager.MountBasePath,
                    d.Iqn,
                    lun.LunId
                ));
            }

            // 3) DESMONTAR Y LIMPIAR DIRECTORIOS / FSTAB
            foreach (var mountPoint in mountPoints)
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

                if (Directory.Exists(mountPoint))
                    ShellHelper.EjecutarComoRoot($"rmdir \"{mountPoint}\"");

                FstabHelper.RemoveMountPoint(mountPoint);
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

            // 5) ELIMINAR SERVICIOS SYSTEMD DEL TARGET Y DE TODOS SUS LUNs
            foreach (var unitPath in unitFiles)
            {
                var unit = Path.GetFileName(unitPath);

                ShellHelper.EjecutarComoRoot($"systemctl disable \"{unit}\" 2>/dev/null");
                ShellHelper.EjecutarComoRoot($"rm -f \"{unitPath}\"");
            }

            ShellHelper.EjecutarComoRoot(
                $"rm -f /usr/local/bin/mount-{serviceBase}*.sh"
            );
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
                var device = ObtenerDispositivoDesdeSesion(d.Iqn, d.LunId, out var resolvedLun);
                d.LunId = resolvedLun;

                if (string.IsNullOrEmpty(device))
                {
                    var (ipSolo, _) = NormalizarPortal(d.Ip ?? "");
                    ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T \"{d.Iqn}\" -p \"{ipSolo}\" --rescan");
                    ShellHelper.EjecutarComoRoot("udevadm settle");
                    await Task.Delay(1000);

                    device = ObtenerDispositivoDesdeSesion(d.Iqn, d.LunId, out resolvedLun);
                    d.LunId = resolvedLun;
                }

                if (string.IsNullOrEmpty(device))
                    throw new Exception($"Could not find an active block device (/dev/sdX) assigned to IQN {d.Iqn}, LUN {d.LunId}.");

                d.DevicePath = device;

                // Garantizar punto de montaje uniforme (IQN + LUN)
                d.MountPoint = IscsiNaming.MountPoint(
                    ConfigManager.MountBasePath,
                    d.Iqn,
                    d.LunId
                );

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

    private static string? ObtenerDispositivoDesdeSesion(string? iqn, int lunId, out int resolvedLunId)
    {
        resolvedLunId = lunId;

        if (string.IsNullOrWhiteSpace(iqn))
            return null;

        try
        {
            // Strategy A: /dev/disk/by-path (IQN + LUN aware)
            var byPathOut = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/ 2>/dev/null").Stdout ?? "";
            var byPath = byPathOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var entry = IscsiByPath.Find(byPath, iqn, lunId, fallbackToFirstLun: lunId == 0);

            if (entry != null)
            {
                resolvedLunId = entry.LunId;

                var real = ShellHelper.EjecutarComoRoot(
                    $"realpath \"/dev/disk/by-path/{entry.Link}\""
                );

                if (real.ExitCode == 0 && !string.IsNullOrWhiteSpace(real.Stdout))
                    return real.Stdout.Trim();
            }

            // Strategy B: Sysfs scanning (IQN + LUN)
            if (Directory.Exists("/sys/class/iscsi_session"))
            {
                var candidates = new List<(string Device, int Lun)>();

                foreach (var sess in Directory.GetDirectories("/sys/class/iscsi_session"))
                {
                    var targetNameFile = Path.Combine(sess, "targetname");
                    if (!File.Exists(targetNameFile))
                        continue;

                    var targetName = File.ReadAllText(targetNameFile).Trim();
                    if (!targetName.Equals(iqn, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sid = Path.GetFileName(sess).Replace("session", "");

                    foreach (var bdir in Directory.GetDirectories("/sys/block"))
                    {
                        var devName = Path.GetFileName(bdir);
                        if (!devName.StartsWith("sd"))
                            continue;

                        var deviceSymlink = Path.Combine(bdir, "device");
                        if (!Directory.Exists(deviceSymlink))
                            continue;

                        var realPath = ShellHelper.EjecutarComoRoot($"realpath \"{deviceSymlink}\"")
                            .Stdout ?? "";

                        if (!realPath.Contains($"session{sid}") && !realPath.Contains($"target{sid}"))
                            continue;

                        var lun = ExtraerLunDesdeRutaScsi(realPath);
                        candidates.Add(($"/dev/{devName}", lun ?? lunId));
                    }
                }

                var sysfsDevice = ElegirPorLun(candidates, lunId);
                if (sysfsDevice != null)
                {
                    resolvedLunId = candidates.First(c => c.Device == sysfsDevice).Lun;
                    return sysfsDevice;
                }
            }

            // Strategy C: Fallback to lsblk + udevadm (IQN + LUN)
            var res = ShellHelper.EjecutarComoRoot("lsblk -dno NAME,TRAN");
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.Stdout))
            {
                var candidates = new List<(string Device, int Lun)>();

                foreach (var line in res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 ||
                        !parts[1].Equals("iscsi", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var devPath = "/dev/" + parts[0].Trim();
                    var info = ShellHelper.EjecutarComoRoot(
                        $"udevadm info --query=property --name={devPath}"
                    );

                    if (info.ExitCode != 0 ||
                        !(info.Stdout ?? "").Contains(iqn, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var lun = ExtraerLunDesdeUdev(info.Stdout, iqn);
                    candidates.Add((devPath, lun ?? lunId));
                }

                var fallbackDevice = ElegirPorLun(candidates, lunId);
                if (fallbackDevice != null)
                {
                    resolvedLunId = candidates.First(c => c.Device == fallbackDevice).Lun;
                    return fallbackDevice;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] Error resolving SCSI node: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Selecciona el dispositivo del LUN pedido. Si el LUN aún no se conoce
    /// (0) se usa el más bajo, igual que en los enlaces de by-path.
    /// </summary>
    private static string? ElegirPorLun(IEnumerable<(string Device, int Lun)> candidates, int lunId)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
            return null;

        var exact = list.FirstOrDefault(c => c.Lun == lunId);
        if (exact.Device != null)
            return exact.Device;

        return lunId == 0
            ? list.OrderBy(c => c.Lun).First().Device
            : null;
    }

    /// <summary>
    /// El último componente de la ruta sysfs de un dispositivo SCSI tiene el
    /// formato <c>host:channel:target:lun</c>.
    /// </summary>
    private static int? ExtraerLunDesdeRutaScsi(string? realPath)
    {
        if (string.IsNullOrWhiteSpace(realPath))
            return null;

        var last = realPath.Trim().TrimEnd('/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(last))
            return null;

        var parts = last.Split(':');
        return parts.Length == 4 && int.TryParse(parts[3], out var lun) ? lun : null;
    }

    private static int? ExtraerLunDesdeUdev(string? udevOutput, string iqn)
    {
        if (string.IsNullOrWhiteSpace(udevOutput))
            return null;

        foreach (var line in udevOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            const string marker = "ID_PATH=";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                continue;

            var path = line[(index + marker.Length)..].Trim();

            if (IscsiByPath.TryParse(path, out _, out var parsedIqn, out var lun) &&
                parsedIqn.Equals(iqn, StringComparison.OrdinalIgnoreCase))
                return lun;
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

    public static bool EsFilesystemMontable(string? fs)
    {
        return fs?.Trim().ToLowerInvariant() is
            "ext2" or "ext3" or "ext4" or "xfs" or "btrfs" or "f2fs" or
            "ntfs" or "vfat" or "exfat" or "iso9660";
    }


    // Helper centralizado para nombres de archivos y servicios
    public static string ObtenerNombreSeguro(string iqn)
    {
        return IscsiNaming.SystemdSafe(iqn);
    }

// Helper centralizado para obtener la ruta de montaje uniforme
    public static string ObtenerPuntoMontaje(IscsiDestino d)
    {
        if (d == null) return string.Empty;

        if (!string.IsNullOrWhiteSpace(d.MountPoint))
            return d.MountPoint;

        return IscsiNaming.MountPoint(
            ConfigManager.MountBasePath,
            d.Iqn,
            d.LunId
        );
    }
}