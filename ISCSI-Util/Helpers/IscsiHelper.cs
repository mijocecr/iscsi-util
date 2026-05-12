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
    //  DETECTAR FILESYSTEM
    // ============================================================

    private static string DetectarFsType(string blkidOut)
    {
        if (string.IsNullOrWhiteSpace(blkidOut))
            return "raw";

        // Normalizar
        string s = blkidOut.ToLowerInvariant();

        // Filesystems reales
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

        // LUKS
        if (s.Contains("type=\"crypto_luks\"")) return "luks";

        // LVM
        if (s.Contains("type=\"lvm2_member\"")) return "lvm";

        // RAID
        if (s.Contains("type=\"linux_raid_member\"")) return "raid";

        // ZFS
        if (s.Contains("type=\"zfs_member\"")) return "zfs";

        // Si blkid devuelve solo PTTYPE="gpt" o "dos" → NO es filesystem
        if (s.Contains("pttype="))
            return "raw";

        // Si no se detecta nada → RAW
        return "raw";
    }


    // ============================================================
    //  DETECTAR CHAP / MUTUAL CHAP
    // ============================================================

  public static void DetectarChap(IscsiDestino d)
{
    long id = NextTraceId();
    LogService.Debug($"[ISCSI] #{id} DetectarChap → {d.Iqn} ({d.Ip})");

    try
    {
        // --------------------------------------------------------------
        // 1) Comprobar si el nodo existe
        // --------------------------------------------------------------
        var check = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260"
        );

        bool nodoExiste = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

        if (!nodoExiste)
        {
            LogService.Debug($"[ISCSI] #{id} Nodo no existe. Creándolo temporalmente...");
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=new"
            );
        }
        else
        {
            LogService.Debug($"[ISCSI] #{id} Nodo existente detectado.");
        }

        // --------------------------------------------------------------
        // 2) Leer configuración real
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Leyendo configuración CHAP real...");

        var show = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 -o show"
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

        // --------------------------------------------------------------
        // 3) Asignar flags al modelo
        // --------------------------------------------------------------
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

        // --------------------------------------------------------------
        // 4) Borrar nodo temporal si fue creado
        // --------------------------------------------------------------
        if (!nodoExiste)
        {
            LogService.Debug($"[ISCSI] #{id} Eliminando nodo temporal...");
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=delete"
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
    long id = NextTraceId();
    TraceIn(id, "Descubrir", $"IP='{ip}'");

    LogService.Write($"[ISCSI] Discovering targets at {ip}...");

    var destinos = new List<IscsiDestino>();

    using (LoadingService.Show($"Discovering targets at {ip}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Discovery (única operación lenta)
            // --------------------------------------------------------------
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

            // Sesiones activas
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

            // --------------------------------------------------------------
            // 2) Parseo rápido + FILTRO por portal solicitado
            // --------------------------------------------------------------
            int countParseados = 0;

            foreach (var line in discovery.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var portalRaw = partes[0];
                var portal = portalRaw.Split(',')[0];

                if (!portal.Contains(":"))
                    portal = $"{portal}:3260";

                // 🔥 FILTRO: solo aceptar targets del portal solicitado
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

            // --------------------------------------------------------------
            // 3) Detectar CHAP en paralelo (rápido, no bloquea UI)
            // --------------------------------------------------------------
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
            return;
        }

        // --------------------------------------------------------------
        // 1) Detectar symlink en /dev/disk/by-path
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Buscando symlink en /dev/disk/by-path para {d.Iqn}...");

        var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(l =>
            l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
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
            return;
        }

        // --------------------------------------------------------------
        // 2) Detectar partición (si existe)
        // --------------------------------------------------------------
        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Si no hay partición → RAW → PartitionPath = null
        if (lines.Length > 1)
        {
            d.PartitionPath = "/dev/" + lines[1].Trim();
        }
        else
        {
            d.PartitionPath = null;
        }

        LogService.Debug($"[ISCSI] #{id} PartitionPath = {d.PartitionPath ?? "(sin partición)"}");

        // --------------------------------------------------------------
        // 3) Detectar mountpoint
        // --------------------------------------------------------------
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

        // --------------------------------------------------------------
        // 4) Detectar filesystem (corregido)
        // --------------------------------------------------------------
        if (d.PartitionPath == null)
        {
            // RAW sin partición → no hay filesystem
            d.TieneFilesystem = false;
            d.FsType = "";
            LogService.Debug($"[ISCSI] #{id} RAW sin partición → no hay filesystem.");
        }
        else
        {
            var blkid = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");
            string outBlk = blkid.Stdout ?? "";

            // Detección estricta de FS real
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
//  CONECTAR — Login iSCSI + detección + montaje
// ======================================================================

public static async Task Conectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Conectar", d.Iqn);

    using (LoadingService.Show($"Connecting to {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Crear mountpoint único por IQN (evita colisiones)
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Generando mountpoint único para {d.Iqn}...");

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

                LogService.Debug($"[ISCSI] #{id} Mountpoint creado: {d.MountPoint}");
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Mountpoint ya existía: {d.MountPoint}");
            }

            // --------------------------------------------------------------
            // 2) Comprobar si ya está conectado
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            LogService.Debug($"[ISCSI] #{id} Ya conectado = {yaConectado}");

            // --------------------------------------------------------------
            // 3) LOGIN iSCSI (solo si no está conectado)
            // --------------------------------------------------------------
            if (!yaConectado)
            {
                LogService.Debug($"[ISCSI] #{id} Preparando nodo iSCSI...");

                var checkNode = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}"
                );

                bool nodoExiste = !checkNode.Stderr.Contains("No records found");

                if (!nodoExiste)
                {
                    LogService.Debug($"[ISCSI] #{id} Nodo no existe. Creándolo...");
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=new"
                    );
                }

                // ----------------------------------------------------------
                // 3C) Aplicar CHAP si procede
                // ----------------------------------------------------------
                if (d.UsaChap || d.UsaMutualChap)
                {
                    LogService.Debug($"[ISCSI] #{id} Aplicando CHAP / Mutual CHAP...");

                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
                    );

                    if (d.UsaChap)
                    {
                        string user = string.IsNullOrWhiteSpace(d.UsuarioChap) ? d.LocalUser : d.UsuarioChap;
                        string pass = string.IsNullOrWhiteSpace(d.PasswordChap) ? d.LocalPass : d.PasswordChap;

                        LogService.Debug($"[ISCSI] #{id} CHAP outgoing user='{user}'");

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username --value=\"{user}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password --value=\"{pass}\""
                        );
                    }

                    if (d.UsaMutualChap)
                    {
                        string userIn = string.IsNullOrWhiteSpace(d.UsuarioMutualChap) ? d.LocalUserIn : d.UsuarioMutualChap;
                        string passIn = string.IsNullOrWhiteSpace(d.PasswordMutualChap) ? d.LocalPassIn : d.PasswordMutualChap;

                        LogService.Debug($"[ISCSI] #{id} Mutual CHAP incoming user='{userIn}'");

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
                        );

                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
                        );
                    }
                }

                // ----------------------------------------------------------
                // 3D) LOGIN con timeout
                // ----------------------------------------------------------
                LogService.Debug($"[ISCSI] #{id} Ejecutando login iSCSI...");

                var loginTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --login"
                    )
                );

                var completed = await Task.WhenAny(loginTask, Task.Delay(5000));
                if (completed != loginTask)
                    throw new Exception("TIMEOUT en login iSCSI");

                LogService.Debug($"[ISCSI] #{id} Login completado.");
                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Detectar symlink en /dev/disk/by-path
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Buscando symlink en /dev/disk/by-path...");

            d.DevicePath = null;

            for (int i = 0; i < 10; i++)
            {
                var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                var match = byPath.FirstOrDefault(l =>
                    l.Contains(d.Ip, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("lun", StringComparison.OrdinalIgnoreCase)
                );

                if (match != null)
                {
                    d.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    LogService.Debug($"[ISCSI] #{id} Symlink detectado: {d.DevicePath}");
                    break;
                }

                await Task.Delay(200);
            }

            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception("No se encontró symlink del dispositivo iSCSI.");

            // --------------------------------------------------------------
            // 5) Detectar partición
            // --------------------------------------------------------------
            var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
            var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;

            LogService.Debug($"[ISCSI] #{id} PartitionPath = {d.PartitionPath}");

            // --------------------------------------------------------------
            // 6) Detectar filesystem
            // --------------------------------------------------------------
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            if (string.IsNullOrWhiteSpace(blkid.Stdout))
            {
                LogService.Debug($"[ISCSI] #{id} No filesystem detectado.");
                d.TieneFilesystem = false;
                d.FsType = "";
                d.Conectado = true;
                TraceOut(id, "Conectar", "NO_FS");
                return;
            }

            d.TieneFilesystem = true;
            d.FsType = DetectarFsType(blkid.Stdout);

            LogService.Debug($"[ISCSI] #{id} Filesystem detectado: {d.FsType}");

            // --------------------------------------------------------------
            // 7) Montar (con mountpoint único)
            // --------------------------------------------------------------
            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                string mountFs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;

                LogService.Debug($"[ISCSI] #{id} Montando {d.PartitionPath} en {d.MountPoint} con FS={mountFs}");

                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Ya estaba montado: {d.MountPoint}");
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

            // ❌ Ignorar basura tipo "node.name"
            if (portal.StartsWith("node.", StringComparison.OrdinalIgnoreCase))
                return null;

            // ❌ Ignorar cosas que no parezcan IP:PUERTO
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
//  PERSISTENCIA — EXACTAMENTE COMO EL HELPER ORIGINAL + PORTAL REAL
// ======================================================================

public static async Task AplicarPersistencia(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "AplicarPersistencia", d.Iqn);

    using (LoadingService.Show($"Applying persistence for {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Crear mountpoint si no existe
            // --------------------------------------------------------------
            if (!Directory.Exists(d.MountPoint))
            {
                LogService.Debug($"[ISCSI] #{id} Mountpoint no existe. Creando: {d.MountPoint}");

                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} Mountpoint ya existe: {d.MountPoint}");
            }

            // --------------------------------------------------------------
            // 2) Detectar portal real
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Detectando portal real...");

            string? portalReal = ObtenerPortalReal(d);

            if (!string.IsNullOrWhiteSpace(portalReal))
            {
                d.Ip = portalReal;
                LogService.Debug($"[ISCSI] #{id} Portal real detectado: {portalReal}");
            }
            else
            {
                LogService.Debug($"[ISCSI] #{id} No se pudo detectar portal real. Usando IP actual: {d.Ip}");
            }

            // --------------------------------------------------------------
            // 3) Aplicar o eliminar persistencia
            // --------------------------------------------------------------
            if (d.Persistir)
            {
                LogService.Write($"[ISCSI] #{id} Activando persistencia para {d.Iqn}");

                await GuardarEnFstab_Original(d, id);
                await CrearServicioPersistencia_Original(d, id);

                LogService.Debug($"[ISCSI] #{id} Ejecutando daemon-reload y enable...");
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
                ShellHelper.EjecutarComoRoot($"systemctl enable iscsi-{SystemdSafe(d.Iqn)}.service");
            }
            else
            {
                LogService.Write($"[ISCSI] #{id} Eliminando persistencia para {d.Iqn}");

                await EliminarPersistencia_Original(d, id);

                LogService.Debug($"[ISCSI] #{id} Ejecutando daemon-reload tras eliminar persistencia...");
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            }

            TraceOut(id, "AplicarPersistencia");
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
//  FSTAB — EXACTAMENTE COMO EL ORIGINAL (UUID + _netdev)
// ======================================================================

private static async Task GuardarEnFstab_Original(IscsiDestino d, long id)
{
    LogService.Debug($"[ISCSI] #{id} GuardarEnFstab_Original → Iniciando para {d.Iqn}");

    using (LoadingService.Show($"Updating fstab for {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Obtener UUID
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Obteniendo UUID de {d.PartitionPath}...");

            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
            string uuid = blkid.Stdout.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            if (string.IsNullOrWhiteSpace(uuid))
            {
                LogService.Error($"[ISCSI] #{id} No se pudo obtener UUID para {d.PartitionPath}");
                return;
            }

            LogService.Debug($"[ISCSI] #{id} UUID detectado: {uuid}");

            // --------------------------------------------------------------
            // 2) Construir entrada fstab
            // --------------------------------------------------------------
            string entry = $"UUID={uuid} {d.MountPoint} auto _netdev 0 0";
            string mpEsc = d.MountPoint.Replace("/", "\\/");

            LogService.Debug($"[ISCSI] #{id} Entrada fstab generada: {entry}");

            // --------------------------------------------------------------
            // 3) Escribir en fstab
            // --------------------------------------------------------------
            LogService.Debug($"[ISCSI] #{id} Eliminando entradas previas y añadiendo nueva...");

            await Task.Run(() =>
            {
                ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
                ShellHelper.EjecutarComoRoot($"bash -c 'echo \"{entry}\" >> /etc/fstab'");
            });

            LogService.Debug($"[ISCSI] #{id} fstab actualizado correctamente.");

            TraceOut(id, "GuardarEnFstab_Original");
        }
        catch (Exception ex)
        {
            LogService.Error($"[ISCSI] #{id} ERROR GuardarEnFstab_Original: {ex.Message}");
        }
    }
}

// ======================================================================
//  CREAR SERVICIO SYSTEMD — EXACTAMENTE COMO EL ORIGINAL + PORTAL REAL
// ======================================================================




private static async Task CrearServicioPersistencia_Original(IscsiDestino d, long id)
{
    LogService.Debug($"[ISCSI] #{id} CrearServicioPersistencia_Original → Iniciando para {d.Iqn}");

    try
    {
        string safe = SystemdSafe(d.Iqn);

        string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
        string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

        LogService.Debug($"[ISCSI] #{id} Rutas generadas: script={scriptPath}, service={servicePath}");

        // --------------------------------------------------------------
        // 1) Generar script bash temporal
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Generando script temporal...");

        string scriptContent =
$@"#!/bin/bash
# VMCF_2026
TARGET=""{d.Iqn}""
PORTAL=""{d.Ip}""
MOUNTPOINT=""{d.MountPoint}""

if [ ""{d.UsuarioChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.authmethod --value=CHAP
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username --value=""{d.UsuarioChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password --value=""{d.PasswordChap}""
fi

if [ ""{d.UsuarioMutualChap}"" != """" ]; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.username_in --value=""{d.UsuarioMutualChap}""
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --op=update --name node.session.auth.password_in --value=""{d.PasswordMutualChap}""
fi

if ! iscsiadm -m session | grep -q ""$TARGET""; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
  for i in {{1..10}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

mount -a -O _netdev
exit 0
";

        File.WriteAllText("/tmp/tmp_script.sh", scriptContent);
        LogService.Debug($"[ISCSI] #{id} Script temporal escrito en /tmp/tmp_script.sh");

        // --------------------------------------------------------------
        // 2) Mover script a /usr/local/bin con permisos root
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Moviendo script a {scriptPath}...");

        ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_script.sh {scriptPath}");
        ShellHelper.EjecutarComoRoot($"chmod 755 {scriptPath}");
        ShellHelper.EjecutarComoRoot($"chown root:root {scriptPath}");

        LogService.Debug($"[ISCSI] #{id} Script instalado correctamente.");

        // --------------------------------------------------------------
        // 3) Generar archivo .service temporal
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Generando servicio systemd temporal...");

        string serviceContent =
$@"[Unit]
Description=Conectar iSCSI y montar {d.Iqn}
After=network-online.target NetworkManager-wait-online.service iscsid.service iscsi.service remote-fs.target
Requires=network-online.target NetworkManager-wait-online.service iscsid.service iscsi.service
Before=remote-fs-pre.target
Wants=remote-fs-pre.target

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
";

        File.WriteAllText("/tmp/tmp_service.service", serviceContent);
        LogService.Debug($"[ISCSI] #{id} Servicio temporal escrito en /tmp/tmp_service.service");

        // --------------------------------------------------------------
        // 4) Mover servicio a /etc/systemd/system
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Moviendo servicio a {servicePath}...");

        ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_service.service {servicePath}");
        ShellHelper.EjecutarComoRoot($"chmod 644 {servicePath}");
        ShellHelper.EjecutarComoRoot($"chown root:root {servicePath}");

        LogService.Debug($"[ISCSI] #{id} Servicio systemd instalado correctamente.");

        TraceOut(id, "CrearServicioPersistencia_Original");
    }
    catch (Exception ex)
    {
        LogService.Error($"[ISCSI] #{id} ERROR CrearServicioPersistencia_Original: {ex.Message}");
    }

    await Task.CompletedTask;
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

        LogService.Debug($"[ISCSI] #{id} Rutas detectadas: script={scriptPath}, service={servicePath}");

        // --------------------------------------------------------------
        // 1. Deshabilitar servicio
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Deshabilitando servicio systemd...");
        ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");

        // --------------------------------------------------------------
        // 2. Eliminar servicio
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Eliminando archivo de servicio...");
        ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");

        // --------------------------------------------------------------
        // 3. Eliminar script
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Eliminando script asociado...");
        ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

        // --------------------------------------------------------------
        // 4. Eliminar entrada fstab
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Eliminando entrada fstab para mountpoint {d.MountPoint}...");
        string mpEsc = d.MountPoint.Replace("/", "\\/");
        ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");

        // --------------------------------------------------------------
        // 5. Dejar node.startup en manual
        // --------------------------------------------------------------
        LogService.Debug($"[ISCSI] #{id} Ajustando node.startup a manual...");
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op update --name node.startup --value manual"
        );

        LogService.Debug($"[ISCSI] #{id} Persistencia eliminada correctamente.");

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

        // ============================================================
        // 1) FSTAB — detección robusta
        // ============================================================
        try
        {
            if (File.Exists("/etc/fstab"))
            {
                string fstab = File.ReadAllText("/etc/fstab");

                // Coincidencia exacta por mountpoint (no substring parcial)
                string pattern = $" {d.MountPoint} ";

                if (fstab.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Ignorar errores de lectura
        }

        // ============================================================
        // 2) Servicio systemd — detección robusta
        // ============================================================
        try
        {
            string safe = SystemdSafe(d.Iqn);
            string service = $"/etc/systemd/system/iscsi-{safe}.service";

            if (File.Exists(service))
                return true;
        }
        catch
        {
            // Ignorar errores de acceso
        }

        return false;
    }


// ======================================================================
//  DESCONECTAR — desmontaje + logout + limpieza
// ======================================================================

public static async Task Desconectar(IscsiDestino d)
{
    long id = NextTraceId();
    TraceIn(id, "Desconectar", d.Iqn);

    using (LoadingService.Show($"Disconnecting {d.Iqn}..."))
    {
        try
        {
            // --------------------------------------------------------------
            // 1) Desmontar si está montado (solo su mountpoint único)
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot(
                    $"mountpoint -q \"{d.MountPoint}\""
                );

                if (mpCheck.ExitCode == 0)
                {
                    // Lazy unmount primero
                    ShellHelper.EjecutarComoRoot(
                        $"umount -l \"{d.MountPoint}\""
                    );
                    await Task.Delay(300);

                    // Si sigue montado, forzar
                    mpCheck = ShellHelper.EjecutarComoRoot(
                        $"mountpoint -q \"{d.MountPoint}\""
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"umount -f \"{d.MountPoint}\""
                        );
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje (solo el suyo)
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot(
                    $"rm -rf \"{d.MountPoint}\""
                );
            }

            // --------------------------------------------------------------
            // 3) Logout iSCSI (solo si existe sesión activa)
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot(
                "iscsiadm -m session"
            ).Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                var completed = await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Reset de propiedades (pero NO borrar nodo ni CHAP)
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.FsType = null;

            // MUY IMPORTANTE:
            // Mantener MountPoint = null para que TargetsView no lo considere montado
            d.MountPoint = null;

            // CHAP y persistencia se mantienen (solo se aplican en Conectar)

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
            // --------------------------------------------------------------
            // 1) Desmontar si está montado (solo su mountpoint único)
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                var mpCheck = ShellHelper.EjecutarComoRoot(
                    $"mountpoint -q \"{d.MountPoint}\""
                );

                if (mpCheck.ExitCode == 0)
                {
                    // Lazy unmount
                    ShellHelper.EjecutarComoRoot(
                        $"umount -l \"{d.MountPoint}\""
                    );
                    await Task.Delay(300);

                    // Si sigue montado → forzar
                    mpCheck = ShellHelper.EjecutarComoRoot(
                        $"mountpoint -q \"{d.MountPoint}\""
                    );

                    if (mpCheck.ExitCode == 0)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"umount -f \"{d.MountPoint}\""
                        );
                        await Task.Delay(200);
                    }
                }
            }

            // --------------------------------------------------------------
            // 2) Eliminar directorio de montaje (solo el suyo)
            // --------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot(
                    $"rm -rf \"{d.MountPoint}\""
                );
            }

            // --------------------------------------------------------------
            // 3) Logout iSCSI si hay sesión activa
            // --------------------------------------------------------------
            var sesiones = ShellHelper.EjecutarComoRoot(
                "iscsiadm -m session"
            ).Stdout;

            if (!string.IsNullOrWhiteSpace(sesiones) &&
                sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase))
            {
                var logoutTask = Task.Run(() =>
                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout"
                    )
                );

                await Task.WhenAny(logoutTask, Task.Delay(5000));
                await Task.Delay(300);
            }

            // --------------------------------------------------------------
            // 4) Eliminar persistencia (fstab + systemd)
            // --------------------------------------------------------------
            string safe = d.SafeName;

            // fstab
            ShellHelper.EjecutarComoRoot(
                $"sed -i '/{safe}/d' /etc/fstab"
            );

            // systemd mount unit
            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.mount"
            );

            // systemd automount unit
            ShellHelper.EjecutarComoRoot(
                $"rm -f /etc/systemd/system/{safe}.automount"
            );

            // recargar systemd
            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            // --------------------------------------------------------------
            // 5) Eliminar nodo iSCSI
            // --------------------------------------------------------------
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete"
            );

            // --------------------------------------------------------------
            // 6) Eliminar discoverydb (si existe)
            // --------------------------------------------------------------
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discoverydb -t sendtargets -p {d.Ip} --op=delete"
            );

            // --------------------------------------------------------------
            // 7) Reset completo del objeto (estado limpio)
            // --------------------------------------------------------------
            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.MountPoint = null;
            d.FsType = null;

            // CHAP detectado → limpiar porque el nodo ya no existe
            d.RequiresChap = false;
            d.RequiresMutualChap = false;
            d.HasLocalChapConfigured = false;
            d.HasLocalMutualConfigured = false;

            d.LocalUser = "";
            d.LocalPass = "";
            d.LocalUserIn = "";
            d.LocalPassIn = "";

            // CHAP configurado por el usuario → limpiar también
            d.UsaChap = false;
            d.UsaMutualChap = false;

            d.UsuarioChap = "";
            d.PasswordChap = "";
            d.UsuarioMutualChap = "";
            d.PasswordMutualChap = "";

            // Persistencia
            d.Persistir = false;
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
            // ----------------------------------------------------------
            // 0) Asegurar conexión
            // ----------------------------------------------------------
            if (!d.Conectado)
                await Conectar(d);

            // ----------------------------------------------------------
            // 1) Asegurar que DevicePath existe
            // ----------------------------------------------------------
            if (string.IsNullOrWhiteSpace(d.DevicePath))
                throw new Exception("DevicePath no detectado antes de inicializar.");

            string device = d.DevicePath; // /dev/disk/by-path/ip-iscsi-lun-0

            var task = Task.Run(async () =>
            {
                // ------------------------------------------------------
                // 2) Desmontar si estaba montado
                // ------------------------------------------------------
                var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
                if (mpCheck.ExitCode == 0)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                    await Task.Delay(300);
                }

                // ------------------------------------------------------
                // 3) Borrar tabla de particiones (RAW)
                // ------------------------------------------------------
                ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {device}");

                // ------------------------------------------------------
                // 4) Crear GPT
                // ------------------------------------------------------
                ShellHelper.EjecutarComoRoot($"parted -s {device} mklabel gpt");

                // ------------------------------------------------------
                // 5) Crear partición primaria
                // ------------------------------------------------------
                ShellHelper.EjecutarComoRoot($"parted -s {device} mkpart primary 0% 100%");
                await Task.Delay(1200); // permitir que el kernel detecte la partición

                // ------------------------------------------------------
                // 6) Detectar nueva partición
                // ------------------------------------------------------
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

                // ------------------------------------------------------
                // 7) Formatear
                // ------------------------------------------------------
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

                // ------------------------------------------------------
                // 8) Montar
                // ------------------------------------------------------
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

        // Mapear FS → comando mkfs
        string cmd = fs switch
        {
            "ext2"  => "mkfs.ext2",
            "ext3"  => "mkfs.ext3",
            "ext4"  => "mkfs.ext4",
            "xfs"   => "mkfs.xfs",
            "btrfs" => "mkfs.btrfs",
            "f2fs"  => "mkfs.f2fs",
            "ntfs"  => "mkfs.ntfs",   // ntfs-3g formatting
            "exfat" => "mkfs.exfat",
            _ => ""
        };

        if (string.IsNullOrEmpty(cmd))
            return false;

        // Verificar si el comando existe en el sistema
        var check = ShellHelper.EjecutarComoRoot($"which {cmd}");
        return check.ExitCode == 0;
    }



}
