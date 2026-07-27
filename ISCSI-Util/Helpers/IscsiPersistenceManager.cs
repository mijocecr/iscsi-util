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

    private static string Safe(string iqn)
    {
        return IscsiHelper.SanitizarNombre(iqn)
            .Replace('.', '_')
            .Replace('-', '_');
    }

    // ============================================================
    // DETECTAR PERSISTENCIA (fstab + systemd)
    // ============================================================
    public static bool Detect(IscsiDestino d)
    {
        long id = NextTraceId();

        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        TraceIn(id, "Detect", d.Iqn);

        string safe = Safe(d.Iqn);
        string mp = Path.Combine(ConfigManager.MountBasePath, safe);

        // FSTAB
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

        // SYSTEMD
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
    // APPLY PERSISTENCE (fstab + systemd)
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
            string mp = Path.Combine(ConfigManager.MountBasePath, safe);

            // Crear directorio persistente
            if (!Directory.Exists(mp))
            {
                Directory.CreateDirectory(mp);
                ShellHelper.EjecutarComoRoot($"chmod {ConfigManager.DefaultPermissions} \"{mp}\"");
            }

            // Obtener UUID
            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");
            string uuid = ExtraerUUID(blkid.Stdout);

            if (string.IsNullOrWhiteSpace(uuid))
                throw new Exception("No UUID detected.");

            // FSTAB
            string fs = d.FsType == "ntfs" ? "ntfs-3g" : d.FsType;
            string entry = $"UUID={uuid} {mp} {fs} defaults,_netdev 0 0";

            ShellHelper.EjecutarComoRoot($"sed -i '\\#{mp.Replace("/", "\\/")}#d' /etc/fstab");
            ShellHelper.EjecutarComoRoot($"bash -c \"echo '{entry}' >> /etc/fstab\"");

            // SYSTEMD UNIT
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            string unit = $@"
[Unit]
Description=iSCSI persistent mount for {d.Iqn}
After=network-online.target iscsid.service
Requires=network-online.target

[Service]
Type=oneshot
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
    // REMOVE PERSISTENCE
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
            string mp = Path.Combine(ConfigManager.MountBasePath, safe);

            // FSTAB
            ShellHelper.EjecutarComoRoot($"sed -i '\\#{mp.Replace("/", "\\/")}#d' /etc/fstab");

            // SYSTEMD
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            if (File.Exists(servicePath))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");
                ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            }

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

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

    // ============================================================
    // EXTRAER UUID
    // ============================================================
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
