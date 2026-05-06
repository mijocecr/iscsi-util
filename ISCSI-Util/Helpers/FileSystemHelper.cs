using System;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class FilesystemHelper
{
    // ============================================================
    // Detectar tipo de filesystem
    // ============================================================

    public static string DetectarFsType(string blkidOut)
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

        // Valor por defecto seguro
        return "ext4";
    }

    // ============================================================
    // Inicializar destino (mkfs.ext4)
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
            // mkfs requiere root
            var (code, stdout, stderr) =
                ShellHelper.EjecutarComoRoot($"mkfs.ext4 -F {destino.PartitionPath}");

            if (code != 0)
            {
                Console.WriteLine($"Error al inicializar {destino.Iqn}: {stderr}");
                destino.TieneFilesystem = false;
                return;
            }

            destino.TieneFilesystem = true;
            NotificadorLinux.Enviar($"Destino {destino.Iqn} inicializado con éxito");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al inicializar {destino.Iqn}: {ex.Message}");
            destino.TieneFilesystem = false;
        }
    }
}
