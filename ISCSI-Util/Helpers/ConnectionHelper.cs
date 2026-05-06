using System;
using System.IO;
using System.Linq;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class ConnectionHelper
{
    public static void Conectar(IscsiDestino destino)
    {
        Console.WriteLine($"[DEBUG] === Conectar() llamado para {destino.Iqn} @ {destino.Ip} ===");

        try
        {
            // ---------------------------------------------------------
            // 0. Preparar mountpoint
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 0) Preparando mountpoint");

            string userBase = GetUserIscsiBase();
            destino.MountPoint = Path.Combine(userBase, IscsiHelper.SanitizarNombre(destino.Iqn));
            Directory.CreateDirectory(destino.MountPoint);

            // ---------------------------------------------------------
            // 1. Comprobar sesiones existentes
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 1) Comprobando sesiones existentes");

            var (_, sesionesOut, _) =
                ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            bool yaConectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Contains(destino.Iqn));

            Console.WriteLine($"[DEBUG] ¿Ya conectado? {yaConectado}");

            // ---------------------------------------------------------
            // 2. Login si no está conectado
            // ---------------------------------------------------------
            if (!yaConectado)
            {
                Console.WriteLine("[DEBUG] 2) Login iSCSI");

                if (destino.UsaChap || destino.UsaMutualChap)
                {
                    Console.WriteLine("[DEBUG] Configurando CHAP");

                    ShellHelper.EjecutarComoRoot(
                        $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP");

                    if (destino.UsaChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.username --value={destino.UsuarioChap}");
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.password --value={destino.PasswordChap}");
                    }

                    if (destino.UsaMutualChap)
                    {
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.username_in --value={destino.UsuarioMutualChap}");
                        ShellHelper.EjecutarComoRoot(
                            $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --op=update --name node.session.auth.password_in --value={destino.PasswordMutualChap}");
                    }
                }

                var login = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --login");

                Console.WriteLine($"[DEBUG] Login exitcode = {login.ExitCode}");
            }

            // ---------------------------------------------------------
            // 3. Buscar DevicePath (by-path)
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 3) Buscando devicePath en /dev/disk/by-path");

            destino.DevicePath = null;

            for (int i = 0; i < 50; i++) // 10 segundos
            {
                string byPathRaw =
                    ShellHelper.RunCleanAsync("ls -1 /dev/disk/by-path/")
                    .GetAwaiter().GetResult();

                var match = byPathRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.Contains(destino.Ip) && line.Contains("lun"));

                if (match != null)
                {
                    destino.DevicePath = "/dev/disk/by-path/" + match.Trim();
                    Console.WriteLine($"[DEBUG] Encontrado by-path: {destino.DevicePath}");
                    break;
                }

                System.Threading.Thread.Sleep(200);
            }

            // ---------------------------------------------------------
            // 3b. Buscar /dev/sdX si no existe by-path
            // ---------------------------------------------------------
            if (string.IsNullOrWhiteSpace(destino.DevicePath))
            {
                Console.WriteLine("[DEBUG] 3b) Buscando devicePath en /dev/sdX");

                string lsblk =
                    ShellHelper.RunCleanAsync("lsblk -rno NAME,TYPE | grep disk")
                    .GetAwaiter().GetResult();

                var disks = lsblk.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var d in disks)
                {
                    var parts = d.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        string dev = "/dev/" + parts[0];

                        var (_, outp, _) =
                            ShellHelper.EjecutarComoRoot($"iscsiadm -m session -P 3 | grep -A5 {destino.Iqn}");

                        if (outp.Contains(dev))
                        {
                            destino.DevicePath = dev;
                            Console.WriteLine($"[DEBUG] Encontrado sdX: {destino.DevicePath}");
                            break;
                        }
                    }
                }
            }

            // ---------------------------------------------------------
            // 4. Si no hay device → abortar sin colgar
            // ---------------------------------------------------------
            if (string.IsNullOrWhiteSpace(destino.DevicePath))
            {
                Console.WriteLine("[DEBUG] ERROR: No se encontró devicePath");
                NotificadorLinux.Enviar($"No se encontró dispositivo para {destino.Iqn}.");
                return;
            }

            Console.WriteLine($"[DEBUG] DevicePath final = {destino.DevicePath}");

            // ---------------------------------------------------------
            // 5. Obtener partición
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 5) Obteniendo partición");

            string lsblkOut =
                ShellHelper.RunCleanAsync($"lsblk -rno NAME {destino.DevicePath}")
                .GetAwaiter().GetResult();

            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            destino.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : destino.DevicePath;

            Console.WriteLine($"[DEBUG] PartitionPath = {destino.PartitionPath}");

            // ---------------------------------------------------------
            // 6. Detectar filesystem
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 6) Detectando filesystem");

            var (_, blkidOut, _) =
                ShellHelper.EjecutarComoRoot($"blkid {destino.PartitionPath}");

            string fsType = FilesystemHelper.DetectarFsType(blkidOut);

            Console.WriteLine($"[DEBUG] fsType = {fsType}");

            // ---------------------------------------------------------
            // 7. Montar si no está montado
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 7) Montando si no está montado");

            string mp =
                ShellHelper.RunCleanAsync($"mountpoint -q \"{destino.MountPoint}\"")
                .GetAwaiter().GetResult();

            bool estaMontado = string.IsNullOrWhiteSpace(mp);

            Console.WriteLine($"[DEBUG] ¿Ya montado? {estaMontado}");

            if (!estaMontado)
            {
                var mount = ShellHelper.EjecutarComoRoot(
                    $"mount -t {fsType} {destino.PartitionPath} \"{destino.MountPoint}\"");

                Console.WriteLine($"[DEBUG] mount exitcode = {mount.ExitCode}");
            }

            // ---------------------------------------------------------
            // 8. Ajustar permisos
            // ---------------------------------------------------------
            Console.WriteLine("[DEBUG] 8) Ajustando permisos");

            string testFile = destino.MountPoint + "/.";

            string owner =
                ShellHelper.RunCleanAsync($"stat -c %u:%g \"{testFile}\"")
                .GetAwaiter().GetResult()
                .Trim();

            string uid =
                ShellHelper.RunCleanAsync("id -u")
                .GetAwaiter().GetResult()
                .Trim();

            string gid =
                ShellHelper.RunCleanAsync("id -g")
                .GetAwaiter().GetResult()
                .Trim();

            Console.WriteLine($"[DEBUG] owner={owner}, uid={uid}, gid={gid}");

            if (owner == "0:0")
            {
                Console.WriteLine("[DEBUG] Cambiando permisos del mountpoint");

                ShellHelper.EjecutarComoRoot(
                    $"chown -R {uid}:{gid} \"{destino.MountPoint}\"");
            }

            destino.Conectado = true;

            Console.WriteLine("[DEBUG] === Conexión COMPLETADA ===");
            NotificadorLinux.Enviar($"Destino {destino.Iqn} montado en {destino.MountPoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Error al conectar destino {destino.Iqn}: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Fallo al conectar destino {destino.Iqn}");
        }
    }

    private static string GetUserIscsiBase()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var basePath = Path.Combine(localApp, "iscsi");
        Directory.CreateDirectory(basePath);
        return basePath;
    }
}
