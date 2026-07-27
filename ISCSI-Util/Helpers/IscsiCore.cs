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
    // DISCOVER (CLI-safe, igual que GUI.Descubrir)
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
    // DETECTAR FS (igual que GUI)
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
    // COMPLETE INFO (CLI) → delega en IscsiHelper.CompletarInformacionDestino
    // ---------------------------------------------------------
    public static async Task CompleteInfo(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO CompleteInfo() IQN={d.Iqn}");

        try
        {
            await IscsiHelper.CompletarInformacionDestino(d, id);
            LogService.Debug($"[CORE] #{id} >>> FIN CompleteInfo()");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR CompleteInfo: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // CONNECT (CLI) → igual que IscsiHelper.Conectar, pero sin GUI
    // ---------------------------------------------------------
    public static async Task Connect(IscsiDestino d)
    {
        long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LogService.Debug($"[CORE] #{id} >>> INICIO Connect() IQN={d.Iqn}, IP={d.Ip}");

        try
        {
            // 1) Mountpoint persistente igual que GUI
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

            // 2) Comprobar si ya está conectado
            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            bool yaConectado = sesiones.Contains(d.Iqn, StringComparison.OrdinalIgnoreCase);

            var (ipSolo, _) = NormalizarPortal(d.Ip);

            // 3) LOGIN
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

            // 4) Detectar symlink + partición + FS + mountpoint (igual que GUI)
            await IscsiHelper.CompletarInformacionDestino(d, id);

            if (!d.TieneFilesystem)
            {
                d.Conectado = true;
                LogService.Debug($"[CORE] #{id} >>> FIN Connect() (NO_FS)");
                return;
            }

            // 5) Montar si no está montado
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

    // ---------------------------------------------------------
    // DISCONNECT (CLI) → igual que IscsiHelper.Desconectar
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

            LogService.Debug($"[CORE] #{id} >>> FIN Disconnect()");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR Disconnect: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // DISCONNECT + DELETE NODE (CLI) → alineado con GUI.Desconectar_Borrar
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

            LogService.Debug($"[CORE] #{id} >>> FIN DisconnectDelete()");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CORE] #{id} ERROR DisconnectDelete: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------
    // INITIALIZE (CLI) → igual que IscsiHelper.InicializarDestino
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
    // OBTENER PORTAL REAL (CLI) → igual que GUI
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
    // MOUNT (CLI) → usa PartitionPath y luego CompleteInfo
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
