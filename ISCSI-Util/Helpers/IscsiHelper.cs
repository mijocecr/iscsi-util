using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using ISCSI_Util.Models;
using ISCSI_Util.Utils;

namespace ISCSI_Util.Helpers;

public static class IscsiHelper
{
    // ============================================================
    // Sanitización del IQN para usar en nombres de archivo/servicio
    // ============================================================

    public static string SanitizarNombre(string iqn)
    {
        char[] invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', ' ' })
            .ToArray();

        return new string(iqn.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    #region Discover iSCSI Targets

    public static List<IscsiDestino> Descubrir(string ip)
    {
        var destinos = new List<IscsiDestino>();
        try
        {
            string output = Ejecutar("sudo", $"-S iscsiadm -m discovery -t sendtargets -p {ip}");
            string sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");

            var sesiones = string.IsNullOrWhiteSpace(sesionesOut)
                ? Array.Empty<string>()
                : sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var byPath = Ejecutar("ls", "-1 /dev/disk/by-path/")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                string iqn = tokens.LastOrDefault(t => t.StartsWith("iqn."));
                if (string.IsNullOrEmpty(iqn)) continue;

                bool conectado = sesiones.Any(s => s.Contains(iqn));

                if (destinos.Any(d => d.Iqn == iqn && d.Ip == ip))
                    continue;

                var destino = new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = conectado,
                    Seleccionado = false,
                    TieneFilesystem = false
                };

                destino.DevicePath = byPath.FirstOrDefault(dev => dev.Contains(ip) && dev.Contains("lun"))
                    ?.Trim();

                if (!string.IsNullOrEmpty(destino.DevicePath))
                    destino.DevicePath = Path.Combine("/dev/disk/by-path", destino.DevicePath);

                destinos.Add(destino);

                if (destino.Conectado)
                    CompletarInformacionDestino(destino);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al descubrir destinos: {ex.Message}");
        }

        // NotificadorLinux.Enviar($"Se descubrieron {destinos.Count} destinos.");
        return destinos;
    }

    #endregion

    // ============================================================
    // Helpers
    // ============================================================

    private static string Ejecutar(string fileName, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
        {
            var pass = Credenciales.AdminPassword?.TrimEnd('\r', '\n');
            process.StandardInput.WriteLine(pass);
            process.StandardInput.Flush();
            process.StandardInput.Close();
        }

        const int timeoutMs = 15000;
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            return string.Empty;
        }

        process.WaitForExit();

        string output = outputBuilder.ToString();
        string error = errorBuilder.ToString();

        if (fileName == "sudo" && process.ExitCode != 0)
        {
            bool esErrorEsperado =
                error.Contains("No active sessions", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Unknown operation", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("command -v dos2unix", StringComparison.OrdinalIgnoreCase);

            if (esErrorEsperado)
                return output;

            Console.WriteLine($"[Ejecutar] Comando sudo falló: {fileName} {args}\n{error}");
            return string.Empty;
        }

        return output;
    }

    private static int EjecutarConCodigo(string fileName, string args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();

        if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
        {
            process.StandardInput.Write(Credenciales.AdminPassword + "\n");
            process.StandardInput.Flush();
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static string ObtenerGrupoUsuario()
    {
        var grupo = Ejecutar("id", "-gn").Trim();
        return string.IsNullOrWhiteSpace(grupo) ? "users" : grupo;
    }

    private static string DetectarFsType(string blkidOut)
    {
        if (blkidOut.Contains("TYPE=\"ext2\"")) return "ext2";
        if (blkidOut.Contains("TYPE=\"ext3\"")) return "ext3";
        if (blkidOut.Contains("TYPE=\"ext4\"")) return "ext4";
        if (blkidOut.Contains("TYPE=\"xfs\"")) return "xfs";
        if (blkidOut.Contains("TYPE=\"btrfs\"")) return "btrfs";
        if (blkidOut.Contains("TYPE=\"f2fs\"")) return "f2fs";
        if (blkidOut.Contains("TYPE=\"ntfs\"")) return "ntfs";
        if (blkidOut.Contains("TYPE=\"vfat\"")) return "vfat";
        if (blkidOut.Contains("TYPE=\"exfat\"")) return "exfat";
        if (blkidOut.Contains("TYPE=\"iso9660\"")) return "iso9660";
        return "ext4";
    }

    // ============================================================
    // Conectar
    // ============================================================

    public static void Conectar(IscsiDestino destino)
    {
        try
        {
            var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            bool yaConectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Contains(destino.Iqn));

            if (!yaConectado)
            {
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip}");

                if (destino.UsaChap || destino.UsaMutualChap)
                {
                    Ejecutar("sudo",
                        $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                        "--op=update --name node.session.auth.authmethod --value=CHAP");

                    if (destino.UsaChap)
                    {
                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.username --value={destino.UsuarioChap}");

                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.password --value={destino.PasswordChap}");
                    }

                    if (destino.UsaMutualChap)
                    {
                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.username_in --value={destino.UsuarioMutualChap}");

                        Ejecutar("sudo",
                            $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} " +
                            $"--op=update --name node.session.auth.password_in --value={destino.PasswordMutualChap}");
                    }
                }

                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --login");
            }

            destino.MountPoint = $"/mnt/iscsi/{SanitizarNombre(destino.Iqn)}";
            Ejecutar("sudo", $"-S mkdir -p {destino.MountPoint}");

            destino.DevicePath = null;
            for (int i = 0; i < 10; i++)
            {
                var output = Ejecutar("ls", "-1 /dev/disk/by-path/");
                var match = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .FirstOrDefault(line => line.Contains(destino.Ip) && line.Contains("lun"));
                if (match != null)
                {
                    destino.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    break;
                }
                System.Threading.Thread.Sleep(1000);
            }

            if (string.IsNullOrWhiteSpace(destino.DevicePath))
                throw new InvalidOperationException($"No se encontró symlink para {destino.Iqn}");

            var lsblkOut = Ejecutar("lsblk", "-rno NAME " + destino.DevicePath);
            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            destino.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : destino.DevicePath;

            var blkidOut = Ejecutar("sudo", "-S blkid " + destino.PartitionPath);
            if (string.IsNullOrWhiteSpace(blkidOut))
            {
                // NotificadorLinux.Enviar($"Destino {destino.Iqn} no tiene filesystem.");
                destino.Conectado = true;
                return;
            }

            string fsType = DetectarFsType(blkidOut);

            int rcMount = EjecutarConCodigo("mountpoint", $"-q {destino.MountPoint}");
            if (rcMount != 0)
            {
                Ejecutar("sudo", $"-S mount -t {fsType} {destino.PartitionPath} {destino.MountPoint}");
            }

            string grupo = ObtenerGrupoUsuario();
            Ejecutar("sudo", $"-S chgrp {grupo} {destino.MountPoint}");
            Ejecutar("sudo", $"-S chmod 770 {destino.MountPoint}");
            Ejecutar("sudo", $"-S chmod g+s {destino.MountPoint}");

            destino.Conectado = true;
            // NotificadorLinux.Enviar($"Destino {destino.Iqn} montado en {destino.MountPoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Error al conectar destino {destino.Iqn}: {ex.Message}");
            // NotificadorLinux.Enviar($"[ERROR] Fallo al conectar destino {destino.Iqn}");
        }
    }

    // ============================================================
    // Desconectar
    // ============================================================

    public static void Desconectar(IscsiDestino destino, bool eliminarPersistencia = false)
    {
        try
        {
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                int rcMount = EjecutarConCodigo("mountpoint", $"-q {destino.MountPoint}");
                if (rcMount == 0)
                {
                    Ejecutar("sudo", $"-S umount {destino.MountPoint}");
                }
            }

            var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
            bool conectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                        .Any(s => s.Contains(destino.Iqn));

            if (conectado)
            {
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --logout");
            }

            destino.Conectado = false;

            if (eliminarPersistencia)
                EliminarServicioPersistencia(destino);

            // NotificadorLinux.Enviar($"Destino {destino.Iqn} desconectado.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al desconectar destino {destino.Iqn}: {ex.Message}");
        }
    }

    // ============================================================
    // Completar información
    // ============================================================

    public static void CompletarInformacionDestino(IscsiDestino d)
    {
        var byPath = Ejecutar("ls", "-1 /dev/disk/by-path/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(line =>
            line.Contains(d.Ip) && line.Contains("lun"));

        if (match != null)
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();

        if (!string.IsNullOrWhiteSpace(d.DevicePath))
        {
            var lsblkOut = Ejecutar("lsblk", "-rno NAME " + d.DevicePath);
            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;
        }

        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            var mounts = Ejecutar("mount", "");
            var line = mounts.Split('\n')
                .FirstOrDefault(l => l.Contains(d.PartitionPath));

            if (line != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    d.MountPoint = parts[2];
            }
        }

        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            try
            {
                var blkidOut = Ejecutar("sudo", $"-S blkid -p {d.PartitionPath}");
                d.TieneFilesystem = !string.IsNullOrWhiteSpace(blkidOut) && blkidOut.Contains("TYPE=");
            }
            catch
            {
                d.TieneFilesystem = false;
            }
        }
    }

    // ============================================================
    // Inicializar destino
    // ============================================================

    public static void InicializarDestino(IscsiDestino destino)
    {
        if (string.IsNullOrWhiteSpace(destino.PartitionPath))
        {
            Console.WriteLine($"Error: No se encontró ruta de partición para {destino.Iqn}");
            return;
        }

        try
        {
            Ejecutar("sudo", $"-S mkfs.ext4 -F {destino.PartitionPath}");
            destino.TieneFilesystem = true;
            // NotificadorLinux.Enviar($"Destino {destino.Iqn} inicializado con éxito");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al inicializar {destino.Iqn}: {ex.Message}");
            destino.TieneFilesystem = false;
        }
    }

    // ============================================================
    // Configurar persistencia (fstab)
    // ============================================================

    public static void ConfigurarPersistencia(IscsiDestino destino, string fsType)
    {
        try
        {
            Ejecutar("sudo",
                $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value automatic");

            var blkidOut = Ejecutar("sudo", $"-S blkid {destino.PartitionPath}");
            string uuid = blkidOut.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            if (string.IsNullOrEmpty(uuid))
                throw new Exception($"No se pudo obtener UUID para {destino.PartitionPath}");

            Ejecutar("sudo", $"-S mkdir -p {destino.MountPoint}");

            string fstabEntry =
                $"UUID={uuid} {destino.MountPoint} {fsType} defaults,_netdev,x-systemd.requires=iscsid.service,x-systemd.after=iscsid.service 0 0";

            string fstabContent = Ejecutar("cat", "/etc/fstab");
            bool uuidExists = fstabContent.Split('\n').Any(line => line.Contains($"UUID={uuid}"));

            if (!uuidExists)
            {
                Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak");
                Ejecutar("sudo", $"-S bash -c \"echo '{fstabEntry}' | tee -a /etc/fstab\"");
                Ejecutar("sudo", "-S systemctl daemon-reload");
                Ejecutar("sudo", "-S mount -a");

                // NotificadorLinux.Enviar($"Persistencia configurada para {destino.Iqn} en {destino.MountPoint}");
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
    // Crear servicio systemd + script
    // ============================================================

    public static void CrearServicioPersistencia(IscsiDestino destino)
    {
        try
        {
            string safeName = SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";

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

            Ejecutar("sudo",
                $"-S bash -c \"cat > {scriptPath} <<'EOF'\n{scriptContent}\nEOF\"");

            Ejecutar("sudo", $"-S chmod 755 {scriptPath}");
            Ejecutar("sudo", $"-S chown root:root {scriptPath}");

            Ejecutar("sudo",
                $"-S bash -c \"command -v dos2unix >/dev/null 2>&1 && dos2unix {scriptPath}\"");

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

            Ejecutar("sudo",
                $"-S bash -c \"cat > {servicePath} <<'EOF'\n{serviceContent}\nEOF\"");

            Ejecutar("sudo", "-S systemctl daemon-reload");
            Ejecutar("sudo", $"-S systemctl enable {rawServiceName}");

            // NotificadorLinux.Enviar($"Servicio {rawServiceName} creado y habilitado para {destino.Iqn}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al crear servicio persistente para {destino.Iqn}: {ex.Message}");
        }
    }

    // ============================================================
    // Eliminar persistencia
    // ============================================================

    public static void EliminarServicioPersistencia(IscsiDestino destino)
    {
        try
        {
            string safeName = SanitizarNombre(destino.Iqn);

            string rawServiceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";
            string wantsPath = $"/etc/systemd/system/multi-user.target.wants/{rawServiceName}";

            Ejecutar("sudo", $"-S systemctl disable {rawServiceName}");
            Ejecutar("sudo", $"-S bash -c \"rm -f {wantsPath}\"");
            Ejecutar("sudo", $"-S bash -c \"rm -f {servicePath}\"");
            Ejecutar("sudo", $"-S bash -c \"rm -f {scriptPath}\"");

            Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak");
            Ejecutar("sudo", $"-S bash -c \"sed -i '\\|{destino.MountPoint}|d' /etc/fstab\"");

            Ejecutar("sudo", "-S systemctl daemon-reload");
            Ejecutar("sudo", "-S mount -a");

            Ejecutar("sudo",
                $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value manual");

            string generatedMount = $"/run/systemd/generator/mnt-iscsi-{safeName}.mount";
            Ejecutar("sudo", $"-S bash -c \"rm -f {generatedMount}\"");

            // NotificadorLinux.Enviar($"Persistencia eliminada para {destino.Iqn}");
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
            var estado = Ejecutar("systemctl", "is-active iscsid").Trim();

            if (estado != "active")
            {
                // NotificadorLinux.Enviar("El servicio iscsid no está activo. Habilitando...");

                Ejecutar("sudo", "-S systemctl enable --now iscsid");
                Ejecutar("sudo", "-S systemctl daemon-reexec");

                Console.WriteLine("[INFO] Servicio iscsid habilitado y arrancado.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] No se pudo asegurar el servicio iscsid: {ex.Message}");
            // NotificadorLinux.Enviar("[ERROR] Fallo al comprobar/arrancar iscsid.");
        }
    }

    // ============================================================
    // Obtener destinos conectados
    // ============================================================

    public static List<IscsiDestino> ObtenerDestinosConectados()
    {
        var destinos = new List<IscsiDestino>();

        try
        {
            string sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");

            if (string.IsNullOrWhiteSpace(sesionesOut))
                return destinos;

            var sesiones = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var s in sesiones)
            {
                var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3) continue;

                string ip = tokens[2].Split(':')[0];
                string iqn = tokens.LastOrDefault(t => t.StartsWith("iqn."));
                if (string.IsNullOrEmpty(iqn)) continue;

                destinos.Add(new IscsiDestino
                {
                    Ip = ip,
                    Iqn = iqn,
                    Conectado = true,
                    Seleccionado = false
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al obtener destinos conectados: {ex.Message}");
        }

        return destinos;
    }
}
