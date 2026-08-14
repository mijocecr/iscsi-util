using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    private static string Safe(string iqn)
    {
        return IscsiHelper.SanitizarNombre(iqn)
            .Replace('.', '_')
            .Replace('-', '_');
    }

    
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

        // 3. Generar mountpoint estable en /mnt/iscsi
        string safe = Safe(d.Iqn);
        string hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(d.Iqn))
        ).Substring(0, 8);

        string baseMount = ConfigManager.MountBasePath ?? "/mnt/iscsi";
        if (!baseMount.StartsWith('/'))
            baseMount = "/" + baseMount;

        string nuevaRuta = Path.Combine(baseMount, $"{safe}_{hash}");
        d.MountPoint = nuevaRuta;
        return nuevaRuta;
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

        string safe = Safe(d.Iqn);
        string mp = ObtenerMountPointTarget(d);

        try
        {
            if (File.Exists("/etc/fstab"))
            {
                string fstab = File.ReadAllText("/etc/fstab");
                if (fstab.Contains(mp, StringComparison.OrdinalIgnoreCase))
                {
                    TraceOut(id, "Detect", "FSTAB");
                    return true;
                }
            }
        }
        catch { }

        string service = $"/etc/systemd/system/iscsi-{safe}.service";
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
            string safe = Safe(d.Iqn);
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
            string entry = $"UUID={uuid} {mp} {fs} defaults,_netdev 0 0";

            ShellHelper.EjecutarComoRoot($"sed -i '\\#{mp.Replace("/", "\\/")}#d' /etc/fstab");
            ShellHelper.EjecutarComoRoot($"bash -c \"echo '{entry}' >> /etc/fstab\"");

            string portalValido = string.IsNullOrWhiteSpace(d.PortalReal) ? d.Ip : d.PortalReal;
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            // ⭐ LOGIN AUTOMÁTICO + MOUNT PERSISTENTE
            string unit = $@"
[Unit]
Description=iSCSI persistent mount for {d.Iqn}
After=network-online.target iscsid.service iscsi.service
Requires=network-online.target iscsid.service iscsi.service

[Service]
Type=oneshot
ExecStartPre=/usr/bin/iscsiadm -m node -T {d.Iqn} -p {portalValido} --login
ExecStart=/usr/bin/mount {mp}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

            File.WriteAllText("/tmp/tmp_unit.service", unit);
            ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_unit.service {servicePath}");
            ShellHelper.EjecutarComoRoot($"chmod 644 {servicePath}");

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            ShellHelper.EjecutarComoRoot($"systemctl enable iscsi-{safe}.service");

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
            string safe = Safe(d.Iqn);
            string mp = ObtenerMountPointTarget(d);

            // 1) Eliminar entrada fstab
            ShellHelper.EjecutarComoRoot($"sed -i '\\#{mp.Replace("/", "\\/")}#d' /etc/fstab");

            // 2) Eliminar servicio systemd
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            if (File.Exists(servicePath))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");
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