using System;
using System.IO;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiPersistenceManager_CLI
{
    private static string Safe(string iqn)
    {
        return IscsiHelper.SanitizarNombre(iqn)
            .Replace('.', '_')
            .Replace('-', '_');
    }

    // ============================================================
    // DETECT
    // ============================================================
    public static bool Detect(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        string safe = Safe(d.Iqn);
        string mp = Path.Combine(ConfigManager.MountBasePath, safe);

        if (File.Exists("/etc/fstab"))
        {
            string fstab = File.ReadAllText("/etc/fstab");
            if (fstab.Contains(mp, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string service = $"/etc/systemd/system/iscsi-{safe}.service";
        return File.Exists(service);
    }

    // ============================================================
    // APPLY
    // ============================================================
    public static async Task ApplyAsync(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return;

        string safe = Safe(d.Iqn);
        string mp = Path.Combine(ConfigManager.MountBasePath, safe);

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

        string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

        // ⭐ LOGIN AUTOMÁTICO + MOUNT PERSISTENTE
        string unit = $@"
[Unit]
Description=iSCSI persistent mount for {d.Iqn}
After=network-online.target iscsid.service iscsi.service
Requires=network-online.target iscsid.service iscsi.service

[Service]
Type=oneshot
ExecStartPre=/usr/bin/iscsiadm -m node -T {d.Iqn} -p {d.PortalReal} --login
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

        await Task.CompletedTask;
    }

    // ============================================================
    // REMOVE
    // ============================================================
   
    public static async Task RemoveAsync(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return;

        string safe = Safe(d.Iqn);
        string mp = Path.Combine(ConfigManager.MountBasePath, safe);

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

        // ⭐ 3) LOGOUT COMPLETO
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.PortalReal} --logout"
        );

        // ⭐ 4) ELIMINAR NODO
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.PortalReal} --op delete"
        );

        // ⭐ 5) ELIMINAR REGISTRO DE DISCOVERY
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m discovery -t sendtargets -p {d.PortalReal} --op delete"
        );

        d.Persistir = false;

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
