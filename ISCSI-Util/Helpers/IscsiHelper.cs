
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using ISCSI_Util.Models;
using ISCSI_Util.Utils;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Helper class for managing iSCSI operations including discovery, connection, and configuration.
/// Provides methods to interact with iscsiadm and manage persistent connections.
/// </summary>
public static class IscsiHelper
{
    #region Discover iSCSI Targets

    /// <summary>
    /// Discovers iSCSI targets available on the specified IP address.
    /// Returns a list of discovered targets with their current connection status.
    /// Sends a desktop notification with the count of discovered targets.
    /// </summary>
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
                    TieneFilesystem = false  // Initialize as false for discovered targets
                };

                destino.DevicePath = byPath.FirstOrDefault(dev => dev.Contains(ip) && dev.Contains("lun"))
                    ?.Trim();

                if (!string.IsNullOrEmpty(destino.DevicePath))
                    destino.DevicePath = Path.Combine("/dev/disk/by-path", destino.DevicePath);

                destinos.Add(destino);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al descubrir destinos: {ex.Message}");
        }

        NotificadorLinux.Enviar($"Se descubrieron {destinos.Count} destinos.");
        return destinos;
    }

    #endregion

    // Helpers
  
    
    // Helper genérico que inyecta la contraseña guardada
    
  
    
    private static string Ejecutar(string fileName, string args)
{
    Console.WriteLine($"[DEBUG] Ejecutar → fileName='{fileName}', args='{args}'");

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

    Console.WriteLine("[DEBUG] Iniciando proceso...");
    process.Start();

    // Comenzar lectura asíncrona
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    // Inyectar contraseña solo si es sudo -S
    if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
    {
        Console.WriteLine("[DEBUG] Inyectando contraseña en stdin...");
    var pass = Credenciales.AdminPassword?.TrimEnd('\r', '\n');
process.StandardInput.WriteLine(pass);
process.StandardInput.Flush();
process.StandardInput.Close();
    }
    else
    {
        Console.WriteLine("[DEBUG] No se inyectó contraseña (no es sudo -S o está vacía).");
    }

    // Esperar con timeout
    const int timeoutMs = 5000;
    if (!process.WaitForExit(timeoutMs))
    {
        NotificadorLinux.Enviar("[ERROR] Timeout esperando al proceso, se aborta.");
        Console.WriteLine("[ERROR] Timeout esperando al proceso, se aborta.");
        try { process.Kill(); } catch { /* ignorar */ }
        return string.Empty;
    }

    // Asegurar que todo el buffer asíncrono haya sido volcado
    process.WaitForExit(); // llamada adicional segura para finalizar eventos

    string output = outputBuilder.ToString();
    string error = errorBuilder.ToString();

    Console.WriteLine($"[DEBUG] ExitCode={process.ExitCode}");
    if (!string.IsNullOrWhiteSpace(error))
        Console.WriteLine($"[DEBUG] stderr:\n{error}");

    // Detección de fallo de autenticación sin depender del texto
  
    
    if (fileName == "sudo" && process.ExitCode != 0)
    {
        NotificadorLinux.Enviar("[ERROR] Fallo de autenticación de sudo (ExitCode != 0). Se aborta.");
        Console.WriteLine("[ERROR] Fallo de autenticación de sudo (ExitCode != 0). Se aborta.");
        Credenciales.AdminPassword = string.Empty;

        try
        {
            // cerrar stdin para que sudo reciba EOF
            if (!process.StandardInput.BaseStream.CanWrite)
                process.StandardInput.Close();
        }
        catch { /* ignorar */ }

        try
        {
            if (!process.HasExited)
                process.Kill(); // terminar sudo de inmediato
        }
        catch { /* ignorar */ }

        return string.Empty;
    }


    Console.WriteLine("[DEBUG] Proceso terminado correctamente.");
    Console.WriteLine("[DEBUG] stdout:\n" + output);
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

        // Inyectar contraseña solo si es sudo -S
        if (fileName == "sudo" && args.Contains("-S") && !string.IsNullOrEmpty(Credenciales.AdminPassword))
        {
            process.StandardInput.Write(Credenciales.AdminPassword + "\n");
            process.StandardInput.Flush();
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Retrieves the current user's group name using the 'id' command.
    /// Falls back to 'users' group if detection fails.
    /// </summary>
    private static string ObtenerGrupoUsuario()
    {
        var grupo = Ejecutar("id", "-gn").Trim();
        return string.IsNullOrWhiteSpace(grupo) ? "users" : grupo;
    }

    /// <summary>
    /// Detects the filesystem type from blkid output.
    /// Supports ext2/3/4, xfs, btrfs, f2fs, ntfs, vfat, exfat, iso9660.
    /// Falls back to ext4 if type cannot be determined.
    /// </summary>
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
        return "ext4"; // fallback
    }

    /// <summary>
    /// Connects to an iSCSI target and mounts it.
    /// Handles CHAP authentication if configured.
    /// Creates mount directory and sets appropriate permissions.
    /// Sends desktop notification on completion.
    /// </summary>
    public static void Conectar(IscsiDestino destino)
    {
    try
    {
        Console.WriteLine($"[DEBUG] Iniciando conexión para IQN={destino.Iqn}, IP={destino.Ip}, UsaChap={destino.UsaChap}");

        var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
        bool yaConectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s.Contains(destino.Iqn));

        if (!yaConectado)
        {
            Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip}");

            if (destino.UsaChap)
            {
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP");
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.username --value={destino.UsuarioChap}");
                Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.password --value={destino.PasswordChap}");
            }

            Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --login");
        }

        destino.MountPoint = $"/mnt/iscsi/{FileSystemUtils.SanitizarNombre(destino.Iqn)}";
        Ejecutar("sudo", $"-S mkdir -p {destino.MountPoint}");

        // Esperar symlink
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
            throw new InvalidOperationException($"No se encontró symlink para {destino.Iqn} (IP {destino.Ip}).");

        // Detectar partición o disco bruto
        var lsblkOut = Ejecutar("lsblk", "-rno NAME " + destino.DevicePath);
        var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length > 1)
        {
            destino.PartitionPath = "/dev/" + lines[1].Trim();
        }
        else
        {
            destino.PartitionPath = destino.DevicePath; // disco bruto, aún puede tener FS
            Console.WriteLine("[DEBUG] Disco bruto detectado, se intentará montar directamente.");
        }

        // Usar sudo para blkid
        var blkidOut = Ejecutar("sudo", "-S blkid " + destino.PartitionPath);
        if (string.IsNullOrWhiteSpace(blkidOut))
        {
            Console.WriteLine("[DEBUG] No se detectó filesystem válido en " + destino.PartitionPath);
            NotificadorLinux.Enviar($"Destino {destino.Iqn} no tiene filesystem. Cree uno antes de montar.");
            destino.Conectado = true;
            return;
        }

        string fsType = DetectarFsType(blkidOut);

        Ejecutar("sudo", $"-S mount -t {fsType} {destino.PartitionPath} {destino.MountPoint}");

        string grupo = ObtenerGrupoUsuario();
        Ejecutar("sudo", $"-S chgrp {grupo} {destino.MountPoint}");
        Ejecutar("sudo", $"-S chmod 770 {destino.MountPoint}");
        Ejecutar("sudo", $"-S chmod g+s {destino.MountPoint}");

        destino.Conectado = true;
        NotificadorLinux.Enviar($"Destino {destino.Iqn} conectado y montado en {destino.MountPoint}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Error al conectar destino {destino.Iqn}: {ex.Message}");
        NotificadorLinux.Enviar($"[ERROR] Fallo al conectar destino {destino.Iqn}");
    }
}

    
    

    // Los demás métodos (Desconectar, CrearServicioPersistencia, Eliminar

    // Desconectar
  
    
    public static void Desconectar(IscsiDestino destino, bool eliminarPersistencia = false)
{
    try
    {
        // 1. Desmontar solo si está montado
        if (!string.IsNullOrEmpty(destino.MountPoint))
        {
            int rcMount = EjecutarConCodigo("mountpoint", $"-q {destino.MountPoint}");
            if (rcMount == 0) // 0 = está montado
            {
                NotificadorLinux.Enviar($"Desmontando {destino.MountPoint}...");
                Ejecutar("sudo", $"-S umount " + destino.MountPoint);
            }
            else
            {
                NotificadorLinux.Enviar($"{destino.MountPoint} ya estaba desmontado.");
            }
        }

        // 2. Logout solo si la sesión sigue activa
        var sesionesOut = Ejecutar("sudo", "-S iscsiadm -m session");
        bool conectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Any(s => s.Contains(destino.Iqn));

        if (conectado)
        {
            NotificadorLinux.Enviar($"Cerrando sesión iSCSI para {destino.Iqn}...");
            Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --logout");
        }
        else
        {
            NotificadorLinux.Enviar($"La sesión iSCSI {destino.Iqn} ya estaba cerrada.");
        }

        destino.Conectado = false;

        // 3. Si se pide eliminar persistencia, limpiar servicio + script + fstab
        if (eliminarPersistencia)
        {
            string safeName = FileSystemUtils.SanitizarNombre(destino.Iqn);
            string serviceName = $"iscsi-{safeName}.service";
            string servicePath = $"/etc/systemd/system/{serviceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{safeName}.sh";

            Ejecutar("sudo", $"-S systemctl disable " + serviceName);
            Ejecutar("sudo", $"-S rm -f " + servicePath);
            Ejecutar("sudo", $"-S rm -f " + scriptPath);
            Ejecutar("sudo", "-S systemctl daemon-reload");

            // 🔒 Eliminar entrada en fstab de forma segura
            Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak"); // backup
            Ejecutar("sudo", $"-S sed -i '/{destino.MountPoint}/d' /etc/fstab");
            Ejecutar("sudo", "-S mount -a"); // validar que sigue siendo correcto

            // Marcar nodo como manual
            Ejecutar("sudo", 
                $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value manual");

            NotificadorLinux.Enviar($"Persistencia eliminada para {destino.Iqn}");
        }

        NotificadorLinux.Enviar($"Destino {destino.Iqn} desconectado correctamente.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error al desconectar destino {destino.Iqn}: {ex.Message}");
    }
}


    
    
 
    /// <summary>
    /// Configures persistent connection for an iSCSI target.
    /// Sets automatic startup and creates a systemd service for mounting.
    /// Detects filesystem type and sets appropriate mount permissions.
    /// </summary>
    public static void ConfigurarPersistencia(IscsiDestino destino, string fsType)
    {
        try
        {
            Ejecutar("sudo", $"-S iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op update --name node.startup --value automatic");

            var blkidOut = Ejecutar("sudo", $"-S blkid {destino.PartitionPath}");
            string uuid = blkidOut.Split(' ')
                .FirstOrDefault(s => s.StartsWith("UUID="))?
                .Replace("UUID=", "")
                .Trim('"');

            if (string.IsNullOrEmpty(uuid))
                throw new Exception($"No se pudo obtener UUID para {destino.PartitionPath}");

            string fstabEntry = $"UUID={uuid} {destino.MountPoint} {fsType} defaults,_netdev 0 0";

            string fstabContent = Ejecutar("cat", "/etc/fstab");
            bool uuidExists = fstabContent.Split('\n').Any(line => line.Contains($"UUID={uuid}"));

            if (!uuidExists)
            {
                Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak");
                Ejecutar("sudo", $"-S bash -c \"echo '{fstabEntry}' | tee -a /etc/fstab\"");
                Ejecutar("sudo", "-S mount -a");

                NotificadorLinux.Enviar($"Persistencia configurada para {destino.Iqn} en {destino.MountPoint}");
            }
            else
            {
                NotificadorLinux.Enviar($"El UUID {uuid} ya existe en /etc/fstab, no se añadió duplicado.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al configurar persistencia para {destino.Iqn}: {ex.Message}");
        }
    }

    
// CrearServicioPersistencia
   

public static void CrearServicioPersistencia(IscsiDestino destino)
{
    try
    {
        // Usar IQN completo en el nombre del servicio/script
        string rawServiceName = $"iscsi-{destino.Iqn}.service";
        string servicePath = $"/etc/systemd/system/{rawServiceName}";
        string scriptPath = $"/usr/local/bin/mount-iscsi-{destino.Iqn}.sh";

        // 1. Script robusto (solo si no existe)
        var scriptExists = Ejecutar("sudo", $"-S bash -c \"test -f '{scriptPath}' && echo exists\"");
        if (!scriptExists.Contains("exists"))
        {
            string scriptContent = $@"#!/bin/bash
TARGET=""{destino.Iqn}""
PORTAL=""{destino.Ip}""
MOUNTPOINT=""{destino.MountPoint}""

# Login si no hay sesión activa
if ! iscsiadm -m session | grep -q ""$TARGET""; then
  iscsiadm -m node -T ""$TARGET"" -p ""$PORTAL"" --login
  for i in {{1..10}}; do
    if ls /dev/disk/by-path/*""$TARGET""*lun* &>/dev/null; then
      break
    fi
    sleep 1
  done
fi

# Montar si no está montado
if ! mountpoint -q ""$MOUNTPOINT""; then
  mount ""$MOUNTPOINT""
fi

exit 0
";
            // Crear script como root
            Ejecutar("sudo", $"-S bash -c \"cat > '{scriptPath}' <<'EOF'\n{scriptContent}\nEOF\"");
            Ejecutar("sudo", $"-S chmod +x '{scriptPath}'");
        }

        // 2. Servicio systemd (solo si no existe)
        var serviceExists = Ejecutar("sudo", $"-S bash -c \"test -f '{servicePath}' && echo exists\"");
        if (!serviceExists.Contains("exists"))
        {
            string serviceContent = $@"
[Unit]
Description=Conectar iSCSI y montar {destino.Iqn}
After=network-online.target iscsid.service
Requires=network-online.target iscsid.service

[Service]
Type=oneshot
ExecStart={scriptPath}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
";
            Ejecutar("sudo", $"-S bash -c \"cat > '{servicePath}' <<'EOF'\n{serviceContent}\nEOF\"");
        }

        // 3. Recargar systemd y habilitar
        Ejecutar("sudo", "-S systemctl daemon-reload");
        Ejecutar("sudo", $"-S systemctl enable '{rawServiceName}'");

        NotificadorLinux.Enviar($"Servicio {rawServiceName} creado y habilitado para {destino.Iqn}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error al crear servicio persistente para {destino.Iqn}: {ex.Message}");
    }
}




// EliminarServicioPersistencia

    /// <summary>
    /// Removes the systemd service and mount script created for persistent connections.
    /// Disables automatic mounting after system reboot.
    /// </summary>
    public static void EliminarServicioPersistencia(IscsiDestino destino)
    {
        try
        {
            string rawServiceName = $"iscsi-{destino.Iqn}.service";
            string servicePath = $"/etc/systemd/system/{rawServiceName}";
            string scriptPath = $"/usr/local/bin/mount-iscsi-{destino.Iqn}.sh";

            // 1. Deshabilitar y eliminar servicio + script
            Ejecutar("sudo", $"-S systemctl disable " + rawServiceName);

            Ejecutar("sudo", $"-S bash -c \"rm -f '{servicePath}'\"");
            Ejecutar("sudo", $"-S bash -c \"rm -f '{scriptPath}'\"");
            Ejecutar("sudo", "-S systemctl daemon-reload");

            // 2. Eliminar entrada en fstab de forma segura
            Ejecutar("sudo", "-S cp /etc/fstab /etc/fstab.bak"); // backup
            Ejecutar("sudo", $"-S bash -c \"sed -i '\\|{destino.MountPoint}|d' /etc/fstab\"");
            Ejecutar("sudo", "-S mount -a"); // validar que sigue siendo correcto

            // 3. Marcar nodo como manual
            Ejecutar("sudo",
                $"-S iscsiadm -m node -T '{destino.Iqn}' -p {destino.Ip} --op update --name node.startup --value manual");

            NotificadorLinux.Enviar($"Persistencia eliminada para {destino.Iqn}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al eliminar servicio persistente para {destino.Iqn}: {ex.Message}");
        }
    }


    
    
    /// <summary>
    /// Ensures the iscsid systemd service is running.
    /// Restarts it if not already active.
    /// Required for iSCSI operations to function.
    /// </summary>
    public static void AsegurarServicioIscsid()
    {
        try
        {
            // Comprobar estado actual del servicio
            var estado = Ejecutar("systemctl", "is-active iscsid").Trim();

            if (estado != "active")
            {
                NotificadorLinux.Enviar("El servicio iscsid no está activo. Habilitando y arrancando...");

                // Habilitar y arrancar el servicio inmediatamente
                Ejecutar("sudo", "-S systemctl enable --now iscsid");

                // Recargar systemd para asegurar que reconoce el demonio
                Ejecutar("sudo", "-S systemctl daemon-reexec");

                Console.WriteLine("[INFO] Servicio iscsid habilitado y arrancado.");
            }
            else
            {
                Console.WriteLine("[DEBUG] iscsid ya está activo.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] No se pudo asegurar el servicio iscsid: {ex.Message}");
            NotificadorLinux.Enviar("[ERROR] Fallo al comprobar/arrancar iscsid.");
        }
    }

    ////// Modificacion 30/3/26
    
    //Lista las Sesiones activas
    
    /// <summary>
    /// Retrieves a list of currently connected iSCSI targets.
    /// Parses iscsiadm session output and extracts target information.
    /// </summary>
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
                // Ejemplo:
                // tcp: [1] 192.168.1.10:3260,1 iqn.2024-01.com.server:target1

                var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3) continue;

                // ⭐ IP está en tokens[2], no en tokens[1]
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

    
    /// <summary>
    /// Completes the target information with device path and mount point.
    /// Finds the device in /dev/disk/by-path/ and detects the actual partition.
    /// </summary>
    public static void CompletarInformacionDestino(IscsiDestino d)
    {
        // 1. Buscar symlink en /dev/disk/by-path/
        var byPath = Ejecutar("ls", "-1 /dev/disk/by-path/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(line =>
            line.Contains(d.Ip) && line.Contains("lun"));

        if (match != null)
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();

        // 2. Detectar partición real
        if (!string.IsNullOrWhiteSpace(d.DevicePath))
        {
            var lsblkOut = Ejecutar("lsblk", "-rno NAME " + d.DevicePath);
            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length > 1)
                d.PartitionPath = "/dev/" + lines[1].Trim();
            else
                d.PartitionPath = d.DevicePath;
        }

        // 3. Detectar mountpoint
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

        // 4. Detectar si tiene filesystem (solo si está conectado)
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

    /// <summary>
    /// Initializes an iSCSI target partition by formatting it with ext4.
    /// </summary>
    public static void InicializarDestino(IscsiDestino destino)
    {
        if (string.IsNullOrWhiteSpace(destino.PartitionPath))
        {
            Console.WriteLine($"Error: No se encontró ruta de partición para {destino.Iqn}");
            return;
        }

        try
        {
            Console.WriteLine($"Formateando {destino.PartitionPath} con ext4...");
            Ejecutar("sudo", $"-S mkfs.ext4 -F {destino.PartitionPath}");
            
            destino.TieneFilesystem = true;
            NotificadorLinux.Enviar($"Destino {destino.Iqn} inicializado con éxito");
            Console.WriteLine($"Destino {destino.Iqn} inicializado correctamente");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al inicializar {destino.Iqn}: {ex.Message}");
            destino.TieneFilesystem = false;
        }
    }

    
    /////////////////////////// 

}