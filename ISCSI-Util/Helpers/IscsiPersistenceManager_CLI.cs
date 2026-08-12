using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    private static string ObtenerMountPointTarget(IscsiDestino d)
    {
        // 1. Si el modelo ya tiene asignado el punto de montaje, usarlo
        if (!string.IsNullOrWhiteSpace(d.MountPoint))
            return d.MountPoint;

        // 2. Si la partición ya está montada en el sistema, consultar su punto de montaje real
        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            string mountReal = ShellHelper.EjecutarComoRoot($"findmnt -n -o TARGET \"{d.PartitionPath}\"").Stdout.Trim();
            if (!string.IsNullOrWhiteSpace(mountReal))
            {
                d.MountPoint = mountReal;
                return mountReal;
            }
        }

        // 3. Generar la ruta usando el mismo Hash SHA1 que IscsiHelper para evitar discrepancias
        string safe = Safe(d.Iqn);
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Iqn))).Substring(0, 8);

        string baseMount = ConfigManager.MountBasePath ?? "/mnt/iscsi";
        if (!baseMount.StartsWith('/'))
            baseMount = "/" + baseMount;

        string nuevaRuta = Path.Combine(baseMount, $"{safe}_{hash}");
        d.MountPoint = nuevaRuta; // Asignar al modelo
        return nuevaRuta;
    }

    // ============================================================
    // DETECT
    // ============================================================
    public static bool Detect(IscsiDestino d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
            return false;

        string safe = Safe(d.Iqn);
        string mp = ObtenerMountPointTarget(d);

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