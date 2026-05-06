using System;
using System.Collections.Generic;
using System.Linq;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class DiscoveryHelper
{
    // ============================================================
    // DESCUBRIR TARGETS iSCSI
    // ============================================================

    public static List<IscsiDestino> Descubrir(string ip)
    {
        var destinos = new List<IscsiDestino>();

        try
        {
            // Descubrimiento requiere root
            var (code1, output, err1) =
                ShellHelper.EjecutarComoRoot($"iscsiadm -m discovery -t sendtargets -p {ip}");

            // Sesiones activas
            var (code2, sesionesOut, err2) =
                ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            var sesiones = string.IsNullOrWhiteSpace(sesionesOut)
                ? Array.Empty<string>()
                : sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Listado de /dev/disk/by-path (NO requiere root)
            string byPathRaw =
                ShellHelper.RunCleanAsync("ls -1 /dev/disk/by-path/").GetAwaiter().GetResult();

            var byPath = string.IsNullOrWhiteSpace(byPathRaw)
                ? Array.Empty<string>()
                : byPathRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

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
                    destino.DevicePath = System.IO.Path.Combine("/dev/disk/by-path", destino.DevicePath);

                destinos.Add(destino);

                if (destino.Conectado)
                    CompletarInformacionDestino(destino);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al descubrir destinos: {ex.Message}");
        }

        return destinos;
    }

    // ============================================================
    // COMPLETAR INFORMACIÓN DEL DESTINO
    // ============================================================

    public static void CompletarInformacionDestino(IscsiDestino d)
    {
        // Listado de by-path
        string byPathRaw =
            ShellHelper.RunCleanAsync("ls -1 /dev/disk/by-path/").GetAwaiter().GetResult();

        var byPath = string.IsNullOrWhiteSpace(byPathRaw)
            ? Array.Empty<string>()
            : byPathRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var match = byPath.FirstOrDefault(line =>
            line.Contains(d.Ip) && line.Contains("lun"));

        if (match != null)
            d.DevicePath = "/dev/disk/by-path/" + match.Trim();

        // Obtener partición
        if (!string.IsNullOrWhiteSpace(d.DevicePath))
        {
            string lsblkOut =
                ShellHelper.RunCleanAsync($"lsblk -rno NAME {d.DevicePath}")
                .GetAwaiter().GetResult();

            var lines = lsblkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            d.PartitionPath = lines.Length > 1
                ? "/dev/" + lines[1].Trim()
                : d.DevicePath;
        }

        // Detectar mountpoint
        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            string mounts =
                ShellHelper.RunCleanAsync("mount").GetAwaiter().GetResult();

            var line = mounts.Split('\n')
                .FirstOrDefault(l => l.Contains(d.PartitionPath));

            if (line != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    d.MountPoint = parts[2];
            }
        }

        // Detectar filesystem
        if (!string.IsNullOrWhiteSpace(d.PartitionPath))
        {
            try
            {
                var (code, blkidOut, err) =
                    ShellHelper.EjecutarComoRoot($"blkid -p {d.PartitionPath}");

                d.TieneFilesystem =
                    !string.IsNullOrWhiteSpace(blkidOut) &&
                    blkidOut.Contains("TYPE=");
            }
            catch
            {
                d.TieneFilesystem = false;
            }
        }
    }

    // ============================================================
    // OBTENER DESTINOS YA CONECTADOS
    // ============================================================

    public static List<IscsiDestino> ObtenerDestinosConectados()
    {
        var destinos = new List<IscsiDestino>();

        try
        {
            var (code, sesionesOut, err) =
                ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            if (string.IsNullOrWhiteSpace(sesionesOut))
                return destinos;

            var sesiones = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var s in sesiones)
            {
                var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                string iqn = tokens.LastOrDefault(t => t.StartsWith("iqn."));
                if (string.IsNullOrEmpty(iqn)) continue;

                string portal = tokens.FirstOrDefault(t => t.Contains(":"));
                if (portal == null) continue;

                var destino = new IscsiDestino
                {
                    Ip = portal.Split(':')[0],
                    Iqn = iqn,
                    Conectado = true
                };

                CompletarInformacionDestino(destino);
                destinos.Add(destino);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al obtener destinos conectados: {ex.Message}");
        }

        return destinos;
    }
}
