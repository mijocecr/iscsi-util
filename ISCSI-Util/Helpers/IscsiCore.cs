using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;


    public static class IscsiCore
    {
        // ---------------------------------------------------------
        // DISCOVER (CLI-safe)
        // ---------------------------------------------------------
     public static async Task<List<IscsiDestino>> Discover(string ip)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} Discover → IP='{ip}'");

    var destinos = new List<IscsiDestino>();

    try
    {
        var discovery = await Task.Run(() =>
            ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}")
        );

        if (string.IsNullOrWhiteSpace(discovery.Stdout))
        {
            LogService.Debug($"[CORE] #{id} Discovery vacío.");
            return destinos;
        }

        var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;

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

            var d = new IscsiDestino
            {
                Ip = portal,
                PortalReal = portal,
                Iqn = iqn,
                Conectado = conectado,
                Seleccionado = false,
                TieneFilesystem = false
            };

            destinos.Add(d);
        }

        // Detectar CHAP en paralelo
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

        return destinos;
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR Discover: {ex.Message}");
        return destinos;
    }
}

        // ---------------------------------------------------------
        // COMPLETAR INFORMACIÓN (CLI-safe)
        // ---------------------------------------------------------
       
        public static async Task CompleteInfo(IscsiDestino d)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} >>> INICIO CompleteInfo() IQN={d.Iqn}");

    try
    {
        // --------------------------------------------------------------
        // 1) Si no está conectado, limpiar y salir
        // --------------------------------------------------------------
        if (!d.Conectado)
        {
            LogService.Debug($"[CORE] #{id} Target {d.Iqn} no está conectado. Saltando detección.");
            d.TieneFilesystem = false;
            d.FsType = "";
            d.MountPoint = "";
            return;
        }

        // --------------------------------------------------------------
        // 2) Detectar symlink en /dev/disk/by-path
        // --------------------------------------------------------------
        LogService.Debug($"[CORE] #{id} Buscando symlink en /dev/disk/by-path...");

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
            LogService.Debug($"[CORE] #{id} Symlink detectado: {d.DevicePath}");
        }
        else
        {
            LogService.Error($"[CORE] #{id} No se encontró symlink para {d.Iqn}.");
            return;
        }

        // --------------------------------------------------------------
        // 3) Detectar partición con lsblk
        // --------------------------------------------------------------
        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length > 1)
        {
            d.PartitionPath = "/dev/" + lines[1].Trim();
        }
        else
        {
            d.PartitionPath = null;
        }

        LogService.Debug($"[CORE] #{id} PartitionPath = {d.PartitionPath ?? "(sin partición)"}");

        // --------------------------------------------------------------
        // 4) Detectar mountpoint real
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
                        LogService.Debug($"[CORE] #{id} MountPoint detectado: {d.MountPoint}");
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(d.MountPoint))
                break;
        }

        if (string.IsNullOrWhiteSpace(d.MountPoint))
            LogService.Debug($"[CORE] #{id} No se detectó mountpoint para {d.Iqn}.");

        // --------------------------------------------------------------
        // 5) Detectar filesystem
        // --------------------------------------------------------------
        if (d.PartitionPath == null)
        {
            d.TieneFilesystem = false;
            d.FsType = "";
            LogService.Debug($"[CORE] #{id} RAW sin partición → no hay filesystem.");
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
                LogService.Debug($"[CORE] #{id} Filesystem detectado: {d.FsType}");
            }
            else
            {
                d.FsType = "";
                LogService.Debug($"[CORE] #{id} No se detectó filesystem en {d.PartitionPath}");
            }
        }

        // --------------------------------------------------------------
        // 6) Actualizar flags CHAP/MUTUAL CHAP
        // --------------------------------------------------------------
        d.UsaChap = d.RequiresChap || d.HasLocalChapConfigured;
        d.UsaMutualChap = d.RequiresMutualChap || d.HasLocalMutualConfigured;

        LogService.Debug($"[CORE] #{id} >>> FIN CompleteInfo()");
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR CompleteInfo: {ex.Message}");
        throw;
    }
}

        
        // ---------------------------------------------------------
        // CONNECT (CLI-safe)
        // ---------------------------------------------------------
        
        public static async Task Connect(IscsiDestino d)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} >>> INICIO Connect() IQN={d.Iqn}, IP={d.Ip}");

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
        // 3) LOGIN iSCSI
        // --------------------------------------------------------------
        if (!yaConectado)
        {
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

            // ----------------------------------------------------------
            // CHAP
            // ----------------------------------------------------------
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

            // ----------------------------------------------------------
            // LOGIN con timeout
            // ----------------------------------------------------------
            var loginTask = Task.Run(() =>
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --login"
                )
            );

            var completed = await Task.WhenAny(loginTask, Task.Delay(5000));
            if (completed != loginTask)
                throw new Exception("TIMEOUT en login iSCSI");

            var loginResult = loginTask.Result;

            if (loginResult.ExitCode != 0 &&
                !loginResult.Stderr.Contains("already present", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Login falló: {loginResult.Stderr}");
            }

            await Task.Delay(300);
        }

        // --------------------------------------------------------------
        // 4) Detectar symlink
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
        // 5) Detectar partición
        // --------------------------------------------------------------
        var lsblk = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME {d.DevicePath}");
        var lines = lsblk.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        d.PartitionPath = lines.Length > 1
            ? "/dev/" + lines[1].Trim()
            : d.DevicePath;

        // --------------------------------------------------------------
        // 6) Detectar filesystem
        // --------------------------------------------------------------
        var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

        if (string.IsNullOrWhiteSpace(blkid.Stdout))
        {
            d.TieneFilesystem = false;
            d.FsType = "";
            d.Conectado = true;
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
        LogService.Debug($"[CORE] #{id} >>> FIN Connect()");
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR Connect: {ex.Message}");
        throw;
    }
}


        private static (string ipSolo, int port) NormalizarPortal(string portal)
        {
            if (string.IsNullOrWhiteSpace(portal))
                return ("", 3260);

            // Si viene como "192.168.1.10:3260"
            if (portal.Contains(":"))
            {
                var partes = portal.Split(':', StringSplitOptions.RemoveEmptyEntries);
                string ip = partes[0].Trim();

                if (int.TryParse(partes[1], out int p))
                    return (ip, p);

                return (ip, 3260);
            }

            // Si viene sin puerto → usar 3260
            return (portal.Trim(), 3260);
        }

        
        
        private static string DetectarFsType(string blkidOutput)
        {
            if (string.IsNullOrWhiteSpace(blkidOutput))
                return "";

            // Buscar TYPE="xxxx"
            int idx = blkidOutput.IndexOf("TYPE=\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return "";

            idx += "TYPE=\"".Length;

            int end = blkidOutput.IndexOf("\"", idx);
            if (end < 0)
                return "";

            string fs = blkidOutput.Substring(idx, end - idx).Trim();

            return fs.ToLowerInvariant();
        }

        
        // ---------------------------------------------------------
        // DISCONNECT (CLI-safe)
        // ---------------------------------------------------------
        
        public static async Task Disconnect(IscsiDestino d)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} >>> INICIO Disconnect() IQN={d.Iqn}");

    try
    {
        // --------------------------------------------------------------
        // 1) Desmontar si está montado
        // --------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(d.MountPoint))
        {
            var mpCheck = ShellHelper.EjecutarComoRoot(
                $"mountpoint -q \"{d.MountPoint}\""
            );

            if (mpCheck.ExitCode == 0)
            {
                // umount lazy
                ShellHelper.EjecutarComoRoot(
                    $"umount -l \"{d.MountPoint}\""
                );
                await Task.Delay(300);

                // comprobar si sigue montado
                mpCheck = ShellHelper.EjecutarComoRoot(
                    $"mountpoint -q \"{d.MountPoint}\""
                );

                if (mpCheck.ExitCode == 0)
                {
                    // umount force
                    ShellHelper.EjecutarComoRoot(
                        $"umount -f \"{d.MountPoint}\""
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
            ShellHelper.EjecutarComoRoot(
                $"rm -rf \"{d.MountPoint}\""
            );
        }

        // --------------------------------------------------------------
        // 3) Logout si la sesión sigue activa
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
        // 4) Limpiar estado del destino
        // --------------------------------------------------------------
        d.Conectado = false;
        d.TieneFilesystem = false;
        d.DevicePath = null;
        d.PartitionPath = null;
        d.FsType = null;
        d.MountPoint = null;

        LogService.Debug($"[CORE] #{id} >>> FIN Disconnect()");
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR Disconnect: {ex.Message}");
        throw;
    }
}

        

        // ---------------------------------------------------------
        // DISCONNECT + DELETE NODE (CLI-safe)
        // ---------------------------------------------------------
        
        public static async Task DisconnectDelete(IscsiDestino d)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} >>> INICIO DisconnectDelete() IQN={d.Iqn}");

    try
    {
        // --------------------------------------------------------------
        // 1) Desmontar si está montado
        // --------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(d.MountPoint))
        {
            var mpCheck = ShellHelper.EjecutarComoRoot(
                $"mountpoint -q \"{d.MountPoint}\""
            );

            if (mpCheck.ExitCode == 0)
            {
                ShellHelper.EjecutarComoRoot(
                    $"umount -l \"{d.MountPoint}\""
                );
                await Task.Delay(300);

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
        // 2) Eliminar directorio de montaje
        // --------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
            Directory.Exists(d.MountPoint))
        {
            ShellHelper.EjecutarComoRoot(
                $"rm -rf \"{d.MountPoint}\""
            );
        }

        // --------------------------------------------------------------
        // 3) Logout si la sesión sigue activa
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
        // 4) Eliminar persistencia local (fstab + systemd)
        // --------------------------------------------------------------
        string safe = d.SafeName;

        ShellHelper.EjecutarComoRoot(
            $"sed -i '/{safe}/d' /etc/fstab"
        );

        ShellHelper.EjecutarComoRoot(
            $"rm -f /etc/systemd/system/{safe}.mount"
        );

        ShellHelper.EjecutarComoRoot(
            $"rm -f /etc/systemd/system/{safe}.automount"
        );

        ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

        // --------------------------------------------------------------
        // 5) Eliminar nodo iSCSI
        // --------------------------------------------------------------
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete"
        );

        // --------------------------------------------------------------
        // 6) Eliminar discoverydb
        // --------------------------------------------------------------
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m discoverydb -t sendtargets -p {d.Ip} --op=delete"
        );

        // --------------------------------------------------------------
        // 7) Limpiar estado del destino
        // --------------------------------------------------------------
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

        LogService.Debug($"[CORE] #{id} >>> FIN DisconnectDelete()");
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR DisconnectDelete: {ex.Message}");
        throw;
    }
}


        // ---------------------------------------------------------
        // INITIALIZE (CLI-safe)
        // ---------------------------------------------------------
       public static async Task Initialize(IscsiDestino d, string label, string fsType)
{
    long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    LogService.Debug($"[CORE] #{id} >>> INICIO Initialize() IQN={d.Iqn}");

    try
    {
        // --------------------------------------------------------------
        // 1) Si no está conectado, conectar primero
        // --------------------------------------------------------------
        if (!d.Conectado)
            await Connect(d);

        if (string.IsNullOrWhiteSpace(d.DevicePath))
            throw new Exception("DevicePath no detectado antes de inicializar.");

        string device = d.DevicePath;

        // --------------------------------------------------------------
        // 2) Ejecutar inicialización en un Task paralelo (igual que GUI)
        // --------------------------------------------------------------
        var task = Task.Run(async () =>
        {
            // ----------------------------------------------------------
            // Desmontar si está montado
            // ----------------------------------------------------------
            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
            if (mpCheck.ExitCode == 0)
            {
                ShellHelper.EjecutarComoRoot($"umount -l \"{d.MountPoint}\"");
                await Task.Delay(300);
            }

            // ----------------------------------------------------------
            // Borrar GPT
            // ----------------------------------------------------------
            ShellHelper.EjecutarComoRoot($"sgdisk --zap-all {device}");

            // ----------------------------------------------------------
            // Crear tabla GPT
            // ----------------------------------------------------------
            ShellHelper.EjecutarComoRoot($"parted -s {device} mklabel gpt");

            // ----------------------------------------------------------
            // Crear partición primaria
            // ----------------------------------------------------------
            ShellHelper.EjecutarComoRoot($"parted -s {device} mkpart primary 0% 100%");
            await Task.Delay(1200);

            // ----------------------------------------------------------
            // Detectar partición con lsblk
            // ----------------------------------------------------------
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

            // ----------------------------------------------------------
            // Crear filesystem
            // ----------------------------------------------------------
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

            // ----------------------------------------------------------
            // Montar filesystem
            // ----------------------------------------------------------
            string mountFs = fsType == "ntfs" ? "ntfs-3g" : fsType;

            ShellHelper.EjecutarComoRoot(
                $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
            );
        });

        await task;

        LogService.Debug($"[CORE] #{id} >>> FIN Initialize()");
    }
    catch (Exception ex)
    {
        LogService.Error($"[CORE] #{id} ERROR Initialize: {ex.Message}");
        throw;
    }
}

       
       
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

        // Filtrar líneas tipo "node.session..."
        if (portal.StartsWith("node.", StringComparison.OrdinalIgnoreCase))
            return null;

        // Validar que tenga IP y puerto
        if (!portal.Contains('.') || !portal.Contains(':'))
            return null;

        return portal;
    }
    catch
    {
        return null;
    }
}


public static async Task Mount(IscsiDestino d)
{
    if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
        return;

    // Asegurar que el directorio existe
    if (!Directory.Exists(d.MountPoint))
        Directory.CreateDirectory(d.MountPoint);

    // Intentar montar directamente
    ShellHelper.EjecutarComoRoot($"mount {d.DevicePath} \"{d.MountPoint}\"");

    // Esperar un poco para que udev actualice symlinks
    await Task.Delay(300);

    // Completar info de nuevo (igual que GUI)
    await CompleteInfo(d);
}


       
    }