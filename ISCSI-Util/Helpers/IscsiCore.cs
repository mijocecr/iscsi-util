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
                return destinos;

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

            // ============================================================
            // NUEVA DETECCIÓN CHAP (igual que GUI)
            // ============================================================
            foreach (var d in destinos)
            {
                var raw = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op show"
                ).Stdout;

                bool chapEnabled = raw.Contains("node.session.auth.authmethod = CHAP");
                bool hasReverse = raw.Contains("node.session.auth.username_in") ||
                                  raw.Contains("node.session.auth.password_in");

                d.UsaChap = chapEnabled && !hasReverse;
                d.UsaMutualChap = chapEnabled && hasReverse;

                d.LocalUser = ExtractValue(raw, "node.session.auth.username");
                d.LocalPass = ExtractValue(raw, "node.session.auth.password");
                d.LocalUserIn = ExtractValue(raw, "node.session.auth.username_in");
                d.LocalPassIn = ExtractValue(raw, "node.session.auth.password_in");
            }

            return destinos;
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR Discover: {ex.Message}");
            return destinos;
        }
    }

    private static string ExtractValue(string raw, string key)
    {
        var line = raw.Split('\n').FirstOrDefault(l => l.Contains(key));
        if (line == null) return "";

        var parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";

        return parts[1].Trim();
    }

    // ---------------------------------------------------------
    // NORMALIZAR PORTAL
    // ---------------------------------------------------------
    private static (string ipSolo, int port) NormalizarPortal(string portal)
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

    // ---------------------------------------------------------
    // DETECTAR FS
    // ---------------------------------------------------------
    private static string DetectarFsType(string blkidOutput)
    {
        if (string.IsNullOrWhiteSpace(blkidOutput))
            return "";

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
    // COMPLETE INFO (CLI-only)
    // ---------------------------------------------------------
    public static async Task CompleteInfo(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO CompleteInfo() IQN={d.Iqn}");

        try
        {
            CompletarInfoCLI(d);
            LogService.Debug($"[CORE] #{id} >>> FIN CompleteInfo()");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR CompleteInfo: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // COMPLETAR INFO SOLO PARA CLI (NO GUI)
    // ---------------------------------------------------------
   
    private static void CompletarInfoCLI(IscsiDestino d)
    {
        var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/")
            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var (ipSolo, _) = NormalizarPortal(d.Ip);

        // 1) Detectar symlink del disco base (igual que GUI)
        var link = byPath.FirstOrDefault(l =>
            l.Contains(ipSolo, StringComparison.OrdinalIgnoreCase) &&
            l.Contains("lun", StringComparison.OrdinalIgnoreCase) &&
            !l.Contains("part", StringComparison.OrdinalIgnoreCase)
        );

        if (link == null)
        {
            d.DevicePath = null;
            d.PartitionPath = null;
            d.FsType = null;
            d.TieneFilesystem = false;
            return;
        }

        d.DevicePath = "/dev/disk/by-path/" + link.Trim();

        // 2) Detectar partición real
        var lsblkRaw = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {d.DevicePath}").Stdout;
        var lsblkLines = lsblkRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var l in lsblkLines)
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2 && p[1] == "part")
            {
                d.PartitionPath = "/dev/" + p[0];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            d.TieneFilesystem = false;
            return;
        }

        // 3) Detectar filesystem
        var blkidRaw = ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}").Stdout;
        d.FsType = DetectarFsType(blkidRaw);

        d.TieneFilesystem = !string.IsNullOrWhiteSpace(d.FsType);
    }


    
    // ---------------------------------------------------------
    // CONNECT
    // ---------------------------------------------------------
    public static async Task Connect(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO Connect() IQN={d.Iqn}, IP={d.Ip}");

        try
        {
            string safe = IscsiHelper.SanitizarNombre(d.Iqn)
                .Replace('.', '_')
                .Replace('-', '_');

            d.MountPoint = Path.Combine(ConfigManager.MountBasePath, safe);

            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot(
                    $"chmod {ConfigManager.DefaultPermissions} \"{d.MountPoint}\""
                );
            }

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            var (ipSolo, _) = NormalizarPortal(d.Ip);

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

            CompletarInfoCLI(d);

            if (!d.TieneFilesystem)
            {
                d.Conectado = true;
                return;
            }

            var mpCheck = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");

            if (mpCheck.ExitCode != 0)
            {
                string mountFs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;

                ShellHelper.EjecutarComoRoot(
                    $"mount -t {mountFs} {d.PartitionPath} \"{d.MountPoint}\""
                );
            }

            d.Conectado = true;
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR Connect: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // DISCONNECT
    // ---------------------------------------------------------
    public static async Task Disconnect(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO Disconnect() IQN={d.Iqn}");

        try
        {
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

            if (!string.IsNullOrWhiteSpace(d.MountPoint) &&
                Directory.Exists(d.MountPoint))
            {
                ShellHelper.EjecutarComoRoot(
                    $"rm -rf \"{d.MountPoint}\""
                );
            }

            var sesiones = ShellHelper.EjecutarComoRoot(
                "iscsiadm -m session"
            ).Stdout;

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

            d.Conectado = false;
            d.TieneFilesystem = false;
            d.DevicePath = null;
            d.PartitionPath = null;
            d.FsType = null;
            d.MountPoint = null;
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR Disconnect: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // DISCONNECT + DELETE NODE
    // ---------------------------------------------------------
    public static async Task DisconnectDelete(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO DisconnectDelete() IQN={d.Iqn}");

        try
        {
            await Disconnect(d);

            var (ipSolo, _) = NormalizarPortal(d.Ip);

            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {ipSolo} --op=delete"
            );

            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discoverydb -t sendtargets -p {ipSolo} --op=delete"
            );

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
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR DisconnectDelete: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // INITIALIZE
    // ---------------------------------------------------------
    public static async Task Initialize(IscsiDestino d, string label, string fsType)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO Initialize() IQN={d.Iqn}");

        try
        {
            await IscsiHelper.InicializarDestino(d, label, fsType);
            LogService.Debug($"[CORE] #{id} >>> FIN Initialize()");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR Initialize: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // OBTENER PORTAL REAL
    // ---------------------------------------------------------
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

    // ---------------------------------------------------------
    // MOUNT
    // ---------------------------------------------------------
    public static async Task Mount(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return;

        if (!Directory.Exists(d.MountPoint))
            Directory.CreateDirectory(d.MountPoint);

        ShellHelper.EjecutarComoRoot($"mount {d.PartitionPath} \"{d.MountPoint}\"");

        await Task.Delay(300);

        await CompleteInfo(d);
    }
}
