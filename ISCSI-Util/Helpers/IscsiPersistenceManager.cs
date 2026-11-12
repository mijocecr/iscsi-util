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
            string mountReal = ShellHelper.EjecutarComoRoot(
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

        TraceIn(id, "Apply", d.Iqn);

        try
        {
            string serviceName = IscsiNaming.ServiceName(d.Iqn, d.LunId);
            string mp = ObtenerMountPointTarget(d);

            if (!Directory.Exists(mp))
            {
                Directory.CreateDirectory(mp);
                ShellHelper.EjecutarComoRoot($"chmod {ConfigManager.DefaultPermissions} \"{mp}\"");
            }

            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
            string uuid = ExtraerUUID(blkid.Stdout);

            if (string.IsNullOrWhiteSpace(uuid))
                throw new Exception("No UUID detected.");

            string fs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;
            string entry = $"UUID={uuid} {mp} {fs} {FstabHelper.NetworkOptions} 0 0";

            FstabHelper.RemoveMountPoint(mp);
            FstabHelper.AddEntry(entry);

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
                ShellHelper.EjecutarComoRoot($"install -m 644 \"{tempUnit}\" \"{servicePath}\"");
            }
            finally
            {
                try { File.Delete(tempUnit); } catch { }
            }

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            ShellHelper.EjecutarComoRoot($"systemctl enable {serviceName}.service");

            d.Persistir = true;

            TraceOut(id, "Apply");
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] #{id} ERROR Apply: {ex.Message}");
            TraceOut(id, "Apply", "ERROR");
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
            FstabHelper.RemoveMountPoint(mp);

            // 2) Eliminar servicio systemd
            string servicePath = IscsiNaming.ServicePath(d.Iqn, d.LunId);

            if (File.Exists(servicePath))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable {serviceName}.service");
                ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            }

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            string portalValido = string.IsNullOrWhiteSpace(d.PortalReal) ? d.Ip : d.PortalReal;

            // 3) LOGOUT COMPLETO
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalValido} --logout"
            );

            // 4) ELIMINAR NODO
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {portalValido} --op delete"
            );

            // 5) ELIMINAR REGISTRO DE DISCOVERY
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m discoverydb -t sendtargets -p {portalValido} --op delete"
            );

            // 6) LIMPIAR DIRECTORIO DE MONTAJE SI QUEDÓ VACÍO
            if (Directory.Exists(mp))
            {
                ShellHelper.EjecutarComoRoot($"rmdir \"{mp}\" 2>/dev/null");
            }

            d.Persistir = false;

            TraceOut(id, "Remove");
        }
        catch (Exception ex)
        {
            LogService.Error($"[PERSIST] #{id} ERROR Remove: {ex.Message}");
            TraceOut(id, "Remove", "ERROR");
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