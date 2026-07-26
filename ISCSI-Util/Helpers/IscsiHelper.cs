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
    //  NORMALIZAR PORTAL (IP + PUERTO)
    // ============================================================

    private static (string Ip, int Port) NormalizarPortal(string portal)
    {
        if (string.IsNullOrWhiteSpace(portal))
            return ("127.0.0.1", 3260);

        if (portal.Contains(":"))
        {
            var partes = portal.Split(':', 2);
            if (int.TryParse(partes[1], out int p))
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

        string s = blkidOut.ToLowerInvariant();

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
    //  DETECTAR CHAP / MUTUAL CHAP (ACTUALIZADO)
    // ============================================================

    public static void DetectarChap(IscsiDestino d)
    {
        long id = NextTraceId();
        LogService.Debug($"[ISCSI] #{id} DetectarChap → {d.Iqn} ({d.Ip})");

        try
        {
            var (ipSolo, port) = NormalizarPortal(d.Ip);

            var check = ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {ipSolo}"
            );

            bool nodoExiste = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

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

            string config = show.Stdout ?? "";

            string authMethod = ExtraerValor(config, "node.session.auth.authmethod");
            string user      = ExtraerValor(config, "node.session.auth.username");
            string pass      = ExtraerValor(config, "node.session.auth.password");
            string userIn    = ExtraerValor(config, "node.session.auth.username_in");
            string passIn    = ExtraerValor(config, "node.session.auth.password_in");

            bool chapEnabled = authMethod.Equals("CHAP", StringComparison.OrdinalIgnoreCase);

            bool userEmpty   = string.IsNullOrWhiteSpace(user)   || user   == "<empty>";
            bool passEmpty   = string.IsNullOrWhiteSpace(pass)   || pass   == "<empty>";
            bool userInEmpty = string.IsNullOrWhiteSpace(userIn) || userIn == "<empty>";
            bool passInEmpty = string.IsNullOrWhiteSpace(passIn) || passIn == "<empty>";

            d.UsaChap        = chapEnabled && !userEmpty && !passEmpty;
            d.UsaMutualChap  = chapEnabled && !userInEmpty && !passInEmpty;

            d.UsuarioChap        = userEmpty   ? "" : user;
            d.PasswordChap       = passEmpty   ? "" : pass;
            d.UsuarioMutualChap  = userInEmpty ? "" : userIn;
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
    //  DISCOVER — Descubrir destinos iSCSI (ACTUALIZADO)
    // ============================================================

    public static async Task<List<IscsiDestino>> Descubrir(string ip)
    {
        long id = NextTraceId();
        TraceIn(id, "Descubrir", $"IP='{ip}'");

        LogService.Write($"[ISCSI] Discovering targets at {ip}...");

        var destinos = new List<IscsiDestino>();

        using (LoadingService.Show($"Discovering targets at {ip}..."))
        {
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

                var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

                int countParseados = 0;

                foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("iqn.")) continue;

                    var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    var portalRaw = partes[0];
                    var portal = portalRaw.Split(',')[0];

                    if (!portal.Contains(":"))
                        portal = $"{portal}:3260";

                    if (!portal.StartsWith(ip))
                        continue;

                    string iqn = partes.LastOrDefault(s => s.StartsWith("iqn."));
                    if (string.IsNullOrWhiteSpace(iqn))
                        continue;

                    bool conectado = sesiones.Contains(iqn, StringComparison.OrdinalIgnoreCase);

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

                LogService.Debug($"[ISCSI] #{id} Detectando CHAP en paralelo para {destinos.Count} targets...");

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
    }

    // ============================================================
    //  COMPLETAR INFORMACIÓN — DevicePath, PartitionPath, FS
    // ============================================================

    
    public static async Task CompletarInformacionDestino(IscsiDestino d, long parentId)
{
    long id = NextTraceId();
    TraceIn(id, "CompletarInformacion", d.Iqn);

    try
    {
        if (!d.Conectado)
        {
            LogService.Debug($"[ISCSI] #{id} Target {d.Iqn} no está conectado. Saltando detección.");
            d.TieneFilesystem = false;
            d.FsType = "";
            d.MountPoint = "";
            d.PartitionPath = null;
            d.DevicePath = null;
            return;
        }

        LogService.Debug($"[ISCSI] #{id} Buscando symlink en /dev/disk/by-path para {d.Iqn}...");

        var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var (ipSolo, port) = NormalizarPortal(d.Ip);

        var match = byPath.FirstOrDefault(l =>
            l.Contains(ipSolo, StringComparison.OrdinalIgnoreCase) &&
            l.Contains("lun", StringComparison.OrdinalIgnoreCase)
        );

        if (match != null)
        {
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();
            LogService.Debug($"[ISCSI] #{id} Symlink detectado: {d.DevicePath}");
        }
        else
        {
            LogService.Error($"[ISCSI] #{id} No se encontró symlink para {d.Iqn}.");
            d.PartitionPath = null;
            return;
        }

        // ============================================================
        // DETECCIÓN FIABLE DE PARTICIÓN (CORREGIDO)
        // ============================================================

        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

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

        d.PartitionPath = partition;
        LogService.Debug($"[ISCSI] #{id} PartitionPath = {d.PartitionPath ?? "(sin partición)"}");

        // ============================================================
        // DETECCIÓN DE MOUNTPOINT
        // ============================================================

        var mounts = ShellHelper.EjecutarComoRoot("mount").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string[] posibles =
        {
            d.PartitionPath ?? "",
            d.DevicePath ?? "",
            d.PartitionPath != null ? "/dev/" + Path.GetFileName(d.PartitionPath) : "",
            "/dev/" + Path.GetFileName(d.DevicePath ?? "")
        };

        d.MountPoint = "";

        foreach (var m in mounts)
        {
            foreach (var p in posibles)
            {
                if (!string.IsNullOrWhiteSpace(p) && m.StartsWith(p + " "))
                {
                    var parts = m.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        d.MountPoint = parts[2];
                        LogService.Debug($"[ISCSI] #{id} MountPoint detectado: {d.MountPoint}");
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(d.MountPoint))
                break;
        }

        if (string.IsNullOrWhiteSpace(d.MountPoint))
            LogService.Debug($"[ISCSI] #{id} No se detectó mountpoint para {d.Iqn}.");

        // ============================================================
        // DETECCIÓN DE FILESYSTEM
        // ============================================================

        if (d.PartitionPath == null)
        {
            d.TieneFilesystem = false;
            d.FsType = "";
            LogService.Debug($"[ISCSI] #{id} RAW sin partición → no hay filesystem.");
        }
        else
        {
            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");
            string outBlk = blkid.Stdout ?? "";

            d.TieneFilesystem =
                outBlk.Contains("TYPE=\"ext") ||
                outBlk.Contains("TYPE=\"xfs\"") ||
                outBlk.Contains("TYPE=\"btrfs\"") ||
                outBlk.Contains("TYPE=\"f2fs\"") ||
                outBlk.Contains("TYPE=\"ntfs\"") ||
                outBlk.Contains("TYPE=\"vfat\"") ||
                outBlk.Contains("TYPE=\"exfat\"");

            if (d.TieneFilesystem)
            {
                d.FsType = DetectarFsType(outBlk);
                LogService.Debug($"[ISCSI] #{id} Filesystem detectado: {d.FsType}");
            }
            else
            {
                d.FsType = "";
                LogService.Debug($"[ISCSI] #{id} No se detectó filesystem en {d.PartitionPath}");
            }
        }

        d.UsaChap = d.RequiresChap || d.HasLocalChapConfigured;
        d.UsaMutualChap = d.RequiresMutualChap || d.HasLocalMutualConfigured;

        TraceOut(id, "CompletarInformacion");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR CompletarInformacion: {ex.Message}");
    }
}

    
// ======================================================================
//  CONECTAR — Login iSCSI + detección + montaje (ACTUALIZADO)
// ======================================================================

public static async Task Conectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Conectar", d.Iqn);

    LogService.Debug($"[ISCSI] #{id} >>> INICIO Conectar() para IQN={d.Iqn}, IP={d.Ip}");

    using (LoadingService.Show($"Connecting to {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Crear mountpoint único por IQN
            // --------------------------------------------------------------
            string basePath = ConfigManager.MountBasePath;

            string safeIqn = d.Iqn
                .Replace(":", "_")
                .Replace("/", "_")
                .Replace(".", "_")
                .Replace("-", "_");

            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(d.Iqn)
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

            // --------------------------------------------------------------
            // 2) Comprobar si ya está conectado
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            var (ipSolo, port) = NormalizarPortal(d.Ip);

            // --------------------------------------------------------------
            // 3) LOGIN iSCSI (solo el IQN seleccionado)
            // --------------------------------------------------------------
            if (!yaConectado)
            {
                // Crear nodo si no existe
                var checkNode = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo}"
                );

                bool nodoExiste = !checkNode.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

                if (!nodoExiste)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=new"
                    );
                }

                // CHAP
                if (d.UsaChap || d.UsaMutualChap)
                {
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.authmethod --value=CHAP"
                    );

                    if (d.UsaChap)
                    {
                        string user = string.IsNullOrWhiteSpace(d.UsuarioChap) ? d.LocalUser : d.UsuarioChap;
                        string pass = string.IsNullOrWhiteSpace(d.PasswordChap) ? d.LocalPass : d.PasswordChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.username --value=\"{user}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.password --value=\"{pass}\""
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        string userIn = string.IsNullOrWhiteSpace(d.UsuarioMutualChap) ? d.LocalUserIn : d.UsuarioMutualChap;
                        string passIn = string.IsNullOrWhiteSpace(d.PasswordMutualChap) ? d.LocalPassIn : d.PasswordMutualChap;

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
                        );
                    }
                }

                // LOGIN SOLO AL IQN SELECCIONADO
                var loginResult = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --login"
                );

                if (loginResult.ExitCode != 0 &&
                    !loginResult.Stderr.Contains("already present", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Login iSCSI falló: {loginResult.Stderr}");
                }

                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Detectar symlink correcto
            // --------------------------------------------------------------
            d.DevicePath = null;

            for (int i = 0; i < 10; i++)
            {
                var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

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

            // --------------------------------------------------------------
            // 5) Detectar partición correcta (CORREGIDO)
            // --------------------------------------------------------------
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

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

            // --------------------------------------------------------------
            // 6) Detectar filesystem
            // --------------------------------------------------------------
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                d.TieneFilesystem = false;
                d.FsType = "";
                d.Conectado = true;
                TraceOut(id, "Conectar", "NO_FS");
                return;
            }

            d.TieneFilesystem = true;
            d.FsType = DetectarFsType(blkid.Stdout);

            // --------------------------------------------------------------
            // 7) Montar
            // --------------------------------------------------------------
            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                string mountFs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;

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
        }
    }
}



// ======================================================================
//  OBTENER PORTAL REAL — universal, robusto, multi-servidor
// ======================================================================

public static string? ObtenerPortalReal(IscsiDestino d)
{
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
//  ELIMINAR PERSISTENCIA — EXACTAMENTE COMO EL ORIGINAL
// ======================================================================

private static async Task EliminarPersistencia_Original(IscsiDestino d, long id)
{
    LogService.Debug($"[ISCSI] #{id} EliminarPersistencia_Original → Iniciando para {d.Iqn}");

    try
    {
        string safe = SystemdSafe(d.Iqn);

        string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
        string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

        ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");
        ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
        ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

        string mpEsc = d.MountPoint.Replace("/", "\\/");
        ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");

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
//  DETECTAR PERSISTENCIA — EXACTAMENTE COMO EL ORIGINAL
// ======================================================================

public static bool DetectarPersistencia(IscsiDestino d)
{
    if (d == null || string.IsNullOrWhiteSpace(d.MountPoint))
        return false;

    try
    {
        if (File.Exists("/etc/fstab"))
        {
            string fstab = File.ReadAllText("/etc/fstab");
            string pattern = $" {d.MountPoint} ";

            if (fstab.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }
    catch { }

    try
    {
        string safe = SystemdSafe(d.Iqn);
        string service = $"/etc/systemd/system/iscsi-{safe}.service";

        if (File.Exists(service))
            return true;
    }
    catch { }

    return false;
}

// ======================================================================
//  DESCONECTAR — desmontaje + logout + limpieza ligera
// ======================================================================

public static async Task Desconectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Desconectar", d.Iqn);

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // Desmontar si está montado
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot(
                    $"mountpoint -q \"{d.MountPoint}\""
                );

                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);

                    mpCheck = ShellHelper.EjecutarComoRoot(
                        $"mountpoint -q \"{d.MountPoint}\""
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"");
                        await Task.Delay(200);
                    }
                }
            }

            // No borrar el directorio si lo usas para persistencia;
            // si quieres borrarlo, mantén esto:
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
            }

            // Logout solo del IQN seleccionado
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var (ipSolo, _) = NormalizarPortal(d.Ip);

                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --logout"
                    )
                );

                await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            d.Conectado      = false;
            d.TieneFilesystem = false;
            d.DevicePath     = null;
            d.PartitionPath  = null;
            d.FsType         = null;
            d.MountPoint     = null;

            NotificadorLinux.Enviar($"Target {d.Iqn} disconnected");
            TraceOut(id, "Desconectar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar: {ex.Message}");
        }
    }
}

// ======================================================================
//  DESCONECTAR + BORRAR NODO — versión completa
// ======================================================================

public static async Task Desconectar_Borrar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Desconectar_Borrar", d.Iqn);

    using (LoadingService.Show($"Removing {d.Iqn}..."))
    {
        try
        {
            // Desmontar
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot(
                    $"mountpoint -q \"{d.MountPoint}\""
                );

                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);

                    mpCheck = ShellHelper.EjecutarComoRoot(
                        $"mountpoint -q \"{d.MountPoint}\""
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot($"umount -f \"{d.MountPoint}\"");
                        await Task.Delay(200);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot($"rm -rf \"{d.MountPoint}\"");
            }

            // Logout solo del IQN
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var (ipSolo, _) = NormalizarPortal(d.Ip);

                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --logout"
                    )
                );

                await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // Limpiar fstab por UUID si es posible
            if (!string.IsNullOrWhiteSpace(d.PartitionPath))
            {
                var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
                string uuid = blkid.Stdout.Split(' ')
                    .FirstOrDefault(s => s.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))?
                    .Replace("UUID=", "")
                    .Trim('"');

                if (!string.IsNullOrWhiteSpace(uuid))
                {
                    ShellHelper.EjecutarComoRoot(
                        $"sed -i '\\#UUID={uuid} #d' /etc/fstab"
                    );
                }
            }

            // Si tienes units antiguas basadas en SafeName, las limpias aquí
            string safe = d.SafeName;

            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.mount"
            );

            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.automount"
            );

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // Borrar nodo y discoverydb
            var (ipSolo2, _) = NormalizarPortal(d.Ip);

            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {ipSolo2} --op=delete"
            );

            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discoverydb -t sendtargets -p {ipSolo2} --op=delete"
            );

            // Reset de estado del destino
            d.Conectado      = false;
            d.TieneFilesystem = false;
            d.DevicePath     = null;
            d.PartitionPath  = null;
            d.MountPoint     = null;
            d.FsType         = null;

            d.RequiresChap          = false;
            d.RequiresMutualChap    = false;
            d.HasLocalChapConfigured   = false;
            d.HasLocalMutualConfigured = false;

            d.LocalUser   = "";
            d.LocalPass   = "";
            d.LocalUserIn = "";
            d.LocalPassIn = "";

            d.UsaChap        = false;
            d.UsaMutualChap  = false;

            d.UsuarioChap        = "";
            d.PasswordChap       = "";
            d.UsuarioMutualChap  = "";
            d.PasswordMutualChap = "";

            d.Persistir       = false;
            d.PersistenteReal = false;

            NotificadorLinux.Enviar($"Target {d.Iqn} fully removed");
            TraceOut(id, "Desconectar_Borrar");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR Desconectar_Borrar: {ex.Message}");
        }
    }
}




// ======================================================================
//  INICIALIZAR DESTINO — GPT + partición + formateo + montaje
// ======================================================================

public static async Task InicializarDestino(IscsiDestino d, string label, string fsType)
{
    long id = NextTraceId();
    TraceIn(id, "InicializarDestino", d.Iqn);

    using (LoadingService.Show($"Initializing disk ({fsType})..."))
    {
        try
        {
            if (!d.Conectado)
                await Conectar(d);

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception("DevicePath no detectado antes de inicializar.");

            string device = d.DevicePath;

            var task = Task.Run(async () =>
            {
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }

                ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {device}");
                ShellHelper.EjecutarComoRoot($"parted -s {device} mklabel gpt");
                ShellHelper.EjecutarComoRoot($"parted -s {device} mkpart primary 0% 100%");
                await Task.Delay(1200);

                var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {device}");
                var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length > 1)
                {
                    d.PartitionPath = "/dev/" + lines[1].Trim();
                }
                else
                {
                    throw new Exception("No se detectó partición después de crearla.");
                }

                string mkfs = fsType switch
                {
                    "ext4" => $"mkfs.ext4 -F -L \"{label}\" {d.PartitionPath}",
                    "xfs" => $"mkfs.xfs -f -L \"{label}\" {d.PartitionPath}",
                    "btrfs" => $"mkfs.btrfs -f -L \"{label}\" {d.PartitionPath}",
                    "f2fs" => $"mkfs.f2fs -f {d.PartitionPath}",
                    "ntfs" => $"mkfs.ntfs -F -L \"{label}\" {d.PartitionPath}",
                    "exfat" => $"mkfs.exfat -n \"{label}\" {d.PartitionPath}",
                    _ => $"mkfs.ext4 -F -L \"{label}\" {d.PartitionPath}"
                };

                ShellHelper.EjecutarComoRoot(mkfs);

                d.TieneFilesystem = true;
                d.FsType = fsType;

                string mountFs = fsType == "ntfs" ? "ntfs-3g" : fsType;

                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );
            });

            await task;

            NotificadorLinux.Enviar($"Target {d.Iqn} initialized and mounted");
            TraceOut(id, "InicializarDestino");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR InicializarDestino: {ex.Message}");
        }
    }
}

// ======================================================================
//  SOPORTA FILESYSTEM — requerido por InitializeDiskDialogService
// ======================================================================

public static bool SoportaFs(string fs)
{
    if (string.IsNullOrWhiteSpace(fs))
        return false;

    fs = fs.ToLowerInvariant();

    string cmd = fs switch
    {
        "ext2"  => "mkfs.ext2",
        "ext3"  => "mkfs.ext3",
        "ext4"  => "mkfs.ext4",
        "xfs"   => "mkfs.xfs",
        "btrfs" => "mkfs.btrfs",
        "f2fs"  => "mkfs.f2fs",
        "ntfs"  => "mkfs.ntfs",
        "exfat" => "mkfs.exfat",
        _ => ""
    };

    if (string.IsNullOrEmpty(cmd))
        return false;

    var check = ShellHelper.EjecutarComoRoot($"which {cmd}");
    return check.ExitCode == 0;
}

} // ← cierre final de la clase IscsiHelper
