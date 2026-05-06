using System;
using System.IO;
using System.Linq;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class PersistenceHelper
{
    // ============================================================
    // Configurar persistencia (fstab)
    // ============================================================

    public static void ConfigurarPersistencia(IscsiDestino destino, string fsType)
    {
        try
        {
            // Habilitar auto‑login iSCSI
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value automatic");

            // Obtener UUID
            var (_, blkidOut, _) =
                ShellHelper.EjecutarComoRoot($"blkid {destino.PartitionPath}");

            string uuid = blkidOut.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            if (string.IsNullOrEmpty(uuid))
                throw new Exception($"No se pudo obtener UUID para {destino.PartitionPath}");

            // Asegurar mountpoint
            if (string.IsNullOrEmpty(destino.MountPoint))
            {
                string userBase = GetUserIscsiBase();
                destino.MountPoint = Path.Combine(userBase, IscsiHelper.SanitizarNombre(destino.Iqn));
            }
            Directory.CreateDirectory(destino.MountPoint);

            // Entrada fstab
            string fstabEntry =
                $"UUID={uuid} {destino.MountPoint} {fsType} user,noauto,_netdev,x-systemd.requires=iscsid.service,x-systemd.after=iscsid.service 0 0";

            // Leer fstab (NO root)
            string fstabContent =
                ShellHelper.RunCleanAsync("cat /etc/fstab").GetAwaiter().GetResult();

            bool uuidExists = fstabContent.Split('\n').Any(line => line.Contains($"UUID={uuid}"));

            if (!uuidExists)
            {
                ShellHelper.EjecutarComoRoot("cp /etc/fstab /etc/fstab.bak");
                ShellHelper.EjecutarComoRoot($"bash -c \"echo '{fstabEntry}' >> /etc/fstab\"");
                ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
                ShellHelper.EjecutarComoRoot("mount -a");
            }
            else
            {
                Console.WriteLine($"El UUID {uuid} ya existe en /etc/fstab, no se añadió duplicado.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al configurar persistencia para {destino.Iqn}: {ex.Message}");
        }
    }

    // ============================================================
    // Crear servicio persistente systemd
    // ============================================================

    public static void CrearServicioPersistencia(IscsiDestino destino)
    {
        try
        {
            string safeName = IscsiHelper.SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";

            // Script de auto‑montaje
            string scriptContent = $@"#!/bin/bash
TARGET=""{destino.Iqn}""
PORTAL=""{destino.Ip}""
MOUNTPOINT=""{destino.MountPoint}""

if ! iscsiadm -m session | grep -q ""$TARGET""; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
  for i in {{1..10}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

mount -a -O _netdev
exit 0
";

            // Guardar script
            ShellHelper.EjecutarComoRoot(
                $"bash -c \"cat > {scriptPath} <<'EOF'\n{scriptContent}\nEOF\"");

            ShellHelper.EjecutarComoRoot($"chmod 755 {scriptPath}");
            ShellHelper.EjecutarComoRoot($"chown root:root {scriptPath}");

            // Convertir a formato Unix si existe dos2unix
            ShellHelper.EjecutarComoRoot(
                $"bash -c \"command -v dos2unix >/dev/null 2>&1 && dos2unix {scriptPath}\"");

            // Servicio systemd
            string serviceContent = $@"
[Unit]
Description=Conectar iSCSI y montar {destino.Iqn}
After=network-online.target iscsid.service
Requires=network-online.target iscsid.service
Before=remote-fs-pre.target
Wants=remote-fs-pre.target

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";

            ShellHelper.EjecutarComoRoot(
                $"bash -c \"cat > {servicePath} <<'EOF'\n{serviceContent}\nEOF\"");

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            ShellHelper.EjecutarComoRoot($"systemctl enable {rawServiceName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al crear servicio persistente para {destino.Iqn}: {ex.Message}");
        }
    }

    // ============================================================
    // Eliminar persistencia (fstab + systemd)
    // ============================================================

    public static void EliminarServicioPersistencia(IscsiDestino destino)
    {
        try
        {
            string safeName = IscsiHelper.SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";
            string wantsPath = $"/etc/systemd/system/multi-user.target.wants/{rawServiceName}";

            // Deshabilitar servicio si existe
            var (_, checkService, _) =
                ShellHelper.EjecutarComoRoot($"systemctl status {rawServiceName}");

            if (!string.IsNullOrWhiteSpace(checkService))
            {
                ShellHelper.EjecutarComoRoot($"systemctl disable {rawServiceName}");
            }

            // Borrar archivos
            ShellHelper.EjecutarComoRoot($"bash -c \"[ -e '{wantsPath}' ] && rm -f '{wantsPath}'\"");
            ShellHelper.EjecutarComoRoot($"bash -c \"[ -e '{servicePath}' ] && rm -f '{servicePath}'\"");
            ShellHelper.EjecutarComoRoot($"bash -c \"[ -e '{scriptPath}' ] && rm -f '{scriptPath}'\"");

            // Limpiar fstab
            ShellHelper.EjecutarComoRoot("cp /etc/fstab /etc/fstab.bak");

            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                ShellHelper.EjecutarComoRoot(
                    $"bash -c \"sed -i '\\|{destino.MountPoint}|d' /etc/fstab\"");
            }

            ShellHelper.EjecutarComoRoot("systemctl daemon-reload");
            ShellHelper.EjecutarComoRoot("mount -a");

            // Deshabilitar auto‑login
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value manual");

            // Limpiar generadores systemd
            string gen1 = $"/run/systemd/generator/mnt-iscsi-{safeName}.mount";
            string gen2 = $"/run/systemd/generator.late/mnt-iscsi-{safeName}.mount";
            string gen3 = $"/run/systemd/generator/{rawServiceName}";
            string gen4 = $"/run/systemd/generator.late/{rawServiceName}";

            ShellHelper.EjecutarComoRoot(
                $"bash -c \"rm -f '{gen1}' '{gen2}' '{gen3}' '{gen4}' 2>/dev/null\"");

            // Intentar borrar mountpoint vacío
            if (!string.IsNullOrEmpty(destino.MountPoint) &&
                Directory.Exists(destino.MountPoint))
            {
                try { Directory.Delete(destino.MountPoint, recursive: false); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al eliminar servicio persistente para {destino.Iqn}: {ex.Message}");
        }
    }

    // ============================================================
    // Asegurar iscsid
    // ============================================================

    public static void AsegurarServicioIscsid()
    {
        try
        {
            var (_, estado, _) =
                ShellHelper.EjecutarComoRoot("systemctl is-active iscsid");

            if (!estado.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                NotificadorLinux.Enviar("El servicio iscsid no está activo. Habilitando...");

                ShellHelper.EjecutarComoRoot("systemctl enable --now iscsid");
                ShellHelper.EjecutarComoRoot("systemctl daemon-reexec");

                Console.WriteLine("[INFO] Servicio iscsid habilitado y arrancado.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] No se pudo asegurar el servicio iscsid: {ex.Message}");
            NotificadorLinux.Enviar("[ERROR] Fallo al comprobar/arrancar iscsid.");
        }
    }

    // ============================================================
    // Helper interno: ruta base
    // ============================================================

    private static string GetUserIscsiBase()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var basePath = Path.Combine(localApp, "iscsi");
        Directory.CreateDirectory(basePath);
        return basePath;
    }
}
