using System;
using System.IO;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiPersistenceManager
{
    private static long _traceCounter = 0;
    private static long NextTraceId() => ++_traceCounter;

    private static void TraceIn(long id, string method, string details = "")
        => LogService.Debug($"[PERSIST] #{id} → {method} {details}");

    private static void TraceOut(long id, string method, string result = "OK")
        => LogService.Debug($"[PERSIST] #{id} ← {method} [{result}]");

    private static string ObtenerMountPointTarget(IscsiDestino d)
    {
        // 1. Si ya tiene mountpoint asignado, usarlo
        if (!string.IsNullOrWhiteSpace(d.MountPoint))
            return d.MountPoint;

        // 2. Detectar si la partición está montada
        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            string mountReal = ShellHelper.RunClean(
                $"findmnt -n -o TARGET \"{d.PartitionPath}\""
            ).Stdout.Trim();

            // ❗ IGNORAR auto-mounts de CachyOS
            if (!string.IsNullOrWhiteSpace(mountReal))
            {
                if (!mountReal.StartsWith("/run/media") &&
                    !mountReal.StartsWith("/media") &&
                    !mountReal.Contains("gvfs"))
                {
                    // Es un mountpoint válido (persistente o manual)
                    d.MountPoint = mountReal;
                    return mountReal;
                }
            }
        }

        // 3. Generar mountpoint estable (IQN + LUN)
        string nuevaRuta = IscsiNaming.MountPoint(
            ConfigManager.MountBasePath,
            d.Iqn,
            d.LunId
        );

        d.MountPoint = nuevaRuta;
        return nuevaRuta;
    }

    // ============================================================
    // VERIFICACIÓN DE CREDENCIALES CHAP EN DIRECTORIOS LOCALES
    // ============================================================
    
    public static bool HasChapConfigured(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        try
        {
            string nodesDir = "/etc/iscsi/nodes";
            if (!Directory.Exists(nodesDir))
                return false;

            string iqnDir = Path.Combine(nodesDir, d.Iqn);
            if (!Directory.Exists(iqnDir))
                return false;

            foreach (var portalDir in Directory.GetDirectories(iqnDir))
            {
                string confFile = Path.Combine(portalDir, "default");
                if (File.Exists(confFile))
                {
                    string content = File.ReadAllText(confFile);
                    if (content.Contains("node.session.auth.authmethod = CHAP", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] Error checking CHAP config: {ex.Message}");
        }

        return false;
    }

    public static bool HasMutualChapConfigured(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        try
        {
            string nodesDir = "/etc/iscsi/nodes";
            if (!Directory.Exists(nodesDir))
                return false;

            string iqnDir = Path.Combine(nodesDir, d.Iqn);
            if (!Directory.Exists(iqnDir))
                return false;

            foreach (var portalDir in Directory.GetDirectories(iqnDir))
            {
                string confFile = Path.Combine(portalDir, "default");
                if (File.Exists(confFile))
                {
                    string content = File.ReadAllText(confFile);
                    if (content.Contains("node.session.auth.username_in", StringComparison.OrdinalIgnoreCase) ||
                        content.Contains("node.session.auth.password_in", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] Error checking Mutual CHAP config: {ex.Message}");
        }

        return false;
    }

    // ============================================================
    // DETECTAR PERSISTENCIA
    // ============================================================
    public static bool Detect(IscsiDestino d)
    {
        long id = NextTraceId();
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        TraceIn(id, "Detect", d.Iqn);

        string mp = ObtenerMountPointTarget(d);

        if (FstabHelper.HasMountPoint(mp))
        {
            TraceOut(id, "Detect", "FSTAB");
            return true;
        }

        string service = IscsiNaming.ServicePath(d.Iqn, d.LunId);
        if (File.Exists(service))
        {
            TraceOut(id, "Detect", "SERVICE");
            return true;
        }

        TraceOut(id, "Detect", "NONE");
        return false;
    }

    private static void EnsureSuccess(
        (int ExitCode, string Stdout, string Stderr) result,
        string description)
    {
        if (result.ExitCode == 0)
            return;

        var detail = string.IsNullOrWhiteSpace(result.Stderr)
            ? $"exit code {result.ExitCode}"
            : result.Stderr.Trim();

        throw new Exception($"{description} failed: {detail}");
    }

    // ============================================================
    // APPLY PERSISTENCE
    // ============================================================
    public static async Task ApplyAsync(IscsiDestino d)
    {
        long id = NextTraceId();
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
        {
            LogService.Error($"[PERSIST] #{id} Destino inválido.");
            return;
        }

        if (string.IsNullOrWhiteSpace(d.PartitionPath))
            throw new Exception("Persistence requires a partition path.");

        TraceIn(id, "Apply", d.Iqn);

        try
        {
            string serviceName = IscsiNaming.ServiceName(d.Iqn, d.LunId);
            string mp = ObtenerMountPointTarget(d);

            if (!Directory.Exists(mp))
            {
                Directory.CreateDirectory(mp);
                EnsureSuccess(
                    ShellHelper.EjecutarComoRoot($"chmod {ConfigManager.DefaultPermissions} \"{mp}\""),
                    "chmod mount point");
            }

            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
            EnsureSuccess(blkid, "blkid");

            string uuid = ExtraerUUID(blkid.Stdout);

            if (string.IsNullOrWhiteSpace(uuid))
                throw new Exception("No UUID detected.");

            string fs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;
            string entry = $"UUID={uuid} {mp} {fs} {FstabHelper.NetworkOptions} 0 0";

            if (!FstabHelper.RemoveMountPoint(mp))
                throw new Exception("Could not clean the existing /etc/fstab entry.");

            if (!FstabHelper.AddEntry(entry))
                throw new Exception("Could not write the new /etc/fstab entry.");

            string portalValido = string.IsNullOrWhiteSpace(d.PortalReal) ? d.Ip : d.PortalReal;
            string servicePath = IscsiNaming.ServicePath(d.Iqn, d.LunId);

            //  LOGIN AUTOMÁTICO + MOUNT PERSISTENTE
            string unit = $@"
[Unit]
Description=iSCSI persistent mount for {d.Iqn} (LUN {d.LunId})
After=network-online.target iscsid.service
Wants=network-online.target
Requires=iscsid.service

[Service]
Type=oneshot
ExecStartPre=/usr/bin/mkdir -p ""{mp}""
ExecStartPre=-/usr/bin/iscsiadm -m node -T {d.Iqn} -p {portalValido} --login
ExecStart=/usr/bin/mount ""{mp}""
TimeoutStartSec=30
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

            var tempUnit = Path.Combine(
                Path.GetTempPath(),
                $"iscsi-unit-{Guid.NewGuid():N}.service"
            );

            try
            {
                File.WriteAllText(tempUnit, unit);
                EnsureSuccess(
                    ShellHelper.EjecutarComoRoot($"install -m 644 \"{tempUnit}\" \"{servicePath}\""),
                    "install systemd unit");
            }
            finally
            {
                try { File.Delete(tempUnit); } catch { }
            }

            EnsureSuccess(
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload"),
                "systemctl daemon-reload");

            EnsureSuccess(
                ShellHelper.EjecutarComoRoot($"systemctl enable {serviceName}.service"),
                "systemctl enable");

            d.Persistir = true;

            TraceOut(id, "Apply");
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] #{id} ERROR Apply: {ex.Message}");
            TraceOut(id, "Apply", "ERROR");
            throw;
        }

        await Task.CompletedTask;
    }

    // ============================================================
    // REMOVE PERSISTENCIA
    // ============================================================
    public static async Task RemoveAsync(IscsiDestino d)
    {
        long id = NextTraceId();
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return;

        TraceIn(id, "Remove", d.Iqn);

        try
        {
            string serviceName = IscsiNaming.ServiceName(d.Iqn, d.LunId);
            string mp = ObtenerMountPointTarget(d);

            // 1) Eliminar entrada fstab
            if (!FstabHelper.RemoveMountPoint(mp))
                throw new Exception("Could not remove the /etc/fstab entry.");

            // 2) Eliminar servicio systemd
            string servicePath = IscsiNaming.ServicePath(d.Iqn, d.LunId);

            if (File.Exists(servicePath))
            {
                EnsureSuccess(
                    ShellHelper.EjecutarComoRoot($"systemctl disable {serviceName}.service"),
                    "systemctl disable");

                EnsureSuccess(
                    ShellHelper.EjecutarComoRoot($"rm -f {servicePath}"),
                    "remove systemd unit");
            }

            EnsureSuccess(
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload"),
                "systemctl daemon-reload");

            // La sesión iSCSI es compartida por todos los LUNs del target y
            // puede seguir en uso. "No Autostart" solo elimina la persistencia:
            // no hace logout ni borra el nodo.
            if (Directory.Exists(mp))
            {
                var rmdir = ShellHelper.EjecutarComoRoot($"rmdir \"{mp}\" 2>/dev/null");

                // rmdir falla si el punto de montaje sigue en uso; no es un
                // error de la persistencia, así que solo se registra.
                if (rmdir.ExitCode != 0)
                {
                    LogService.Debug(
                        $"[PERSIST] #{id} No se pudo borrar '{mp}' (puede seguir en uso).");
                }
            }

            d.Persistir = false;

            TraceOut(id, "Remove");
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] #{id} ERROR Remove: {ex.Message}");
            TraceOut(id, "Remove", "ERROR");
            throw;
        }

        await Task.CompletedTask;
    }

    private static string ExtraerUUID(string blkidOut)
    {
        if (string.IsNullOrWhiteSpace(blkidOut))
            return "";

        foreach (var part in blkidOut.Split(' '))
        {
            if (part.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))
                return part.Replace("UUID=", "").Trim().Trim('"');
        }

        return "";
    }
}