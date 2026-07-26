using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers
{
    public static class IscsiPersistenceManager_CLI
    {
        // --------------------------------------------------------------
        // APPLY — MISMO PATRÓN QUE LA GUI, SIN AVALONIA
        // --------------------------------------------------------------
        public static async Task ApplyAsync(IscsiDestino d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
                return;

            // 1) Igual que GUI
            EnsureMountPoint(d);

            // 2) Igual que GUI
            EnsureMountDirectory(d);

            // 3) Igual que GUI
            string portalPersistencia = ObtenerPortalPersistencia(d);

            // 4) Igual que GUI
            await GuardarEnFstab(d);

            // 5) Igual que GUI
            await CrearScriptYServicio(d, portalPersistencia);

            // 6) Igual que GUI
            FixCachyOSPresets();

            // 7) Igual que GUI
            await EnableServicio(d);
        }

        // --------------------------------------------------------------
        // REMOVE — MISMO PATRÓN QUE LA GUI
        // --------------------------------------------------------------
        public static async Task RemoveAsync(IscsiDestino d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
                return;

            string safe = SafeName(d.Iqn);
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            ShellHelper.EjecutarComoRoot($"systemctl disable iscsi-{safe}.service");
            ShellHelper.EjecutarComoRoot($"rm -f {servicePath}");
            ShellHelper.EjecutarComoRoot($"rm -f {scriptPath}");

            if (!string.IsNullOrWhiteSpace(d.MountPoint))
            {
                string mpEsc = d.MountPoint.Replace("/", "\\/");
                ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
            }

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");

            await Task.CompletedTask;
        }

        // --------------------------------------------------------------
        // DETECT — MISMO PATRÓN QUE LA GUI
        // --------------------------------------------------------------
        public static bool Detect(IscsiDestino d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Iqn))
                return false;

            if (!string.IsNullOrWhiteSpace(d.MountPoint) && File.Exists("/etc/fstab"))
            {
                string fstab = File.ReadAllText("/etc/fstab");
                if (fstab.Contains($" {d.MountPoint} "))
                    return true;
            }

            string safe = SafeName(d.Iqn);
            string service = $"/etc/systemd/system/iscsi-{safe}.service";

            return File.Exists(service);
        }

        // --------------------------------------------------------------
        // HELPERS — MISMO PATRÓN QUE LA GUI
        // --------------------------------------------------------------

        private static void EnsureMountPoint(IscsiDestino d)
        {
            if (!string.IsNullOrWhiteSpace(d.MountPoint))
                return;

            string basePath = "/mnt/iscsi";
            string safe = SafeName(d.Iqn);

            d.MountPoint = Path.Combine(basePath, safe);
        }

        private static void EnsureMountDirectory(IscsiDestino d)
        {
            if (!Directory.Exists(d.MountPoint))
            {
                Directory.CreateDirectory(d.MountPoint);
                ShellHelper.EjecutarComoRoot($"chmod 755 \"{d.MountPoint}\"");
            }
        }

        private static string ObtenerPortalPersistencia(IscsiDestino d)
        {
            string portalReal = IscsiCore.ObtenerPortalReal(d);
            return string.IsNullOrWhiteSpace(portalReal) ? d.Ip : portalReal;
        }

        private static async Task GuardarEnFstab(IscsiDestino d)
        {
            Console.WriteLine("=== DEBUG FSTAB ===");
            Console.WriteLine($"PartitionPath: {d.PartitionPath}");
            Console.WriteLine($"MountPoint:    {d.MountPoint}");
            Console.WriteLine($"FsType:        {d.FsType}");

            if (string.IsNullOrWhiteSpace(d.PartitionPath))
            {
                Console.WriteLine("ERROR: PartitionPath vacío → NO se puede escribir en fstab");
                return;
            }

            var blkid = ShellHelper.EjecutarComoRoot($"blkid {d.PartitionPath}");

            Console.WriteLine($"blkid stdout: {blkid.Stdout}");
            Console.WriteLine($"blkid stderr: {blkid.Stderr}");

            string uuid = blkid.Stdout.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))?
                .Replace("UUID=", "")
                .Trim('"');

            Console.WriteLine($"UUID detectado: {uuid}");

            if (string.IsNullOrWhiteSpace(uuid))
            {
                Console.WriteLine("ERROR: UUID vacío → NO se puede escribir en fstab");
                return;
            }

            string entry = $"UUID={uuid} {d.MountPoint} auto _netdev 0 0";
            string mpEsc = d.MountPoint.Replace("/", "\\/");

            Console.WriteLine($"Entrada fstab generada: {entry}");
            Console.WriteLine("Ejecutando sed para eliminar entradas previas...");

            var sed = ShellHelper.EjecutarComoRoot($"sed -i '\\#{mpEsc}#d' /etc/fstab");
            Console.WriteLine($"sed stdout: {sed.Stdout}");
            Console.WriteLine($"sed stderr: {sed.Stderr}");

            Console.WriteLine("Ejecutando echo para añadir entrada nueva...");

            var echo = ShellHelper.EjecutarComoRoot($"bash -c 'echo \"{entry}\" >> /etc/fstab'");
            Console.WriteLine($"echo stdout: {echo.Stdout}");
            Console.WriteLine($"echo stderr: {echo.Stderr}");

            Console.WriteLine("=== FIN DEBUG FSTAB ===");
        }


        private static async Task CrearScriptYServicio(IscsiDestino d, string portal)
        {
            string safe = SafeName(d.Iqn);

            string scriptPath = $"/usr/local/bin/mount-iscsi-{safe}.sh";
            string servicePath = $"/etc/systemd/system/iscsi-{safe}.service";

            string scriptContent =
$@"#!/bin/bash
TARGET=""{d.Iqn}""
PORTAL=""{portal}""
MOUNTPOINT=""{d.MountPoint}""

iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
mount -a -O _netdev
exit 0
";

            File.WriteAllText("/tmp/tmp_script.sh", scriptContent);
            ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_script.sh {scriptPath}");
            ShellHelper.EjecutarComoRoot($"chmod 755 {scriptPath}");

            string serviceContent =
$@"[Unit]
Description=Connect iSCSI target and mount {d.Iqn}
After=network-online.target iscsid.service

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

            File.WriteAllText("/tmp/tmp_service.service", serviceContent);
            ShellHelper.EjecutarComoRoot($"mv /tmp/tmp_service.service {servicePath}");
            ShellHelper.EjecutarComoRoot($"chmod 644 {servicePath}");

            await Task.CompletedTask;
        }

        private static void FixCachyOSPresets()
        {
            if (!File.Exists("/etc/cachyos-release"))
                return;

            string presetPath = "/etc/systemd/system-preset/99-iscsi.preset";
            string presetContent = "enable iscsi-*.service\nenable iscsi.service\n";

            ShellHelper.EjecutarComoRoot($"bash -c \"echo '{presetContent}' > {presetPath}\"");
            ShellHelper.EjecutarComoRoot("systemctl preset-all --verbose");
        }

        private static async Task EnableServicio(IscsiDestino d)
        {
            string safe = SafeName(d.Iqn);
            string unitPath = $"/etc/systemd/system/iscsi-{safe}.service";
            string symlinkPath = $"/etc/systemd/system/multi-user.target.wants/iscsi-{safe}.service";

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            ShellHelper.EjecutarComoRoot($"systemctl enable --force iscsi-{safe}.service");

            await Task.Delay(300);

            if (!File.Exists(symlinkPath))
                ShellHelper.EjecutarComoRoot($"ln -s {unitPath} {symlinkPath}");
        }

        private static string SafeName(string s)
        {
            return s
                .Replace(":", "_")
                .Replace(".", "_")
                .Replace("-", "_")
                .Replace(",", "_")
                .Replace(";", "_")
                .Replace("/", "_");
        }
    }
}
