using System;
using System.IO;
using System.Linq;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class DisconnectionHelper
{
    // ============================================================
    // Desconectar destino iSCSI
    // ============================================================

    public static void Desconectar(IscsiDestino destino, bool eliminarPersistencia = true)
    {
        try
        {
            // ---------------------------------------------------------
            // 1. Desmontar si está montado
            // ---------------------------------------------------------
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                string mp =
                    ShellHelper.RunCleanAsync($"mountpoint -q \"{destino.MountPoint}\"")
                    .GetAwaiter().GetResult();

                bool estaMontado = string.IsNullOrWhiteSpace(mp);

                if (estaMontado)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{destino.MountPoint}\"");
                }
            }

            // ---------------------------------------------------------
            // 2. Logout iSCSI si hay sesión activa
            // ---------------------------------------------------------
            var (_, sesionesOut, _) =
                ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            bool conectado = sesionesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Contains(destino.Iqn));

            if (conectado)
            {
                ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m node -T {destino.Iqn} -p {destino.Ip} --logout");
            }

            destino.Conectado = false;

            // ---------------------------------------------------------
            // 3. Eliminar persistencia si procede
            // ---------------------------------------------------------
            if (eliminarPersistencia)
                PersistenceHelper.EliminarServicioPersistencia(destino);

            // ---------------------------------------------------------
            // 4. Limpieza del mountpoint
            // ---------------------------------------------------------
            if (!string.IsNullOrEmpty(destino.MountPoint))
            {
                // Comprobar si sigue montado
                string mp2 =
                    ShellHelper.RunCleanAsync($"mountpoint -q \"{destino.MountPoint}\"")
                    .GetAwaiter().GetResult();

                bool sigueMontado = string.IsNullOrWhiteSpace(mp2);

                if (sigueMontado)
                {
                    ShellHelper.EjecutarComoRoot($"umount -l \"{destino.MountPoint}\"");
                }

                // Si el directorio existe pero está corrupto → borrarlo
                if (Directory.Exists(destino.MountPoint))
                {
                    try
                    {
                        Directory.GetFileSystemEntries(destino.MountPoint);
                    }
                    catch
                    {
                        ShellHelper.EjecutarComoRoot($"rm -rf \"{destino.MountPoint}\"");
                    }
                }

                // Recrear limpio
                if (!Directory.Exists(destino.MountPoint))
                {
                    Directory.CreateDirectory(destino.MountPoint);
                }
            }

            NotificadorLinux.Enviar($"Destino {destino.Iqn} desconectado.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al desconectar destino {destino.Iqn}: {ex.Message}");
        }
    }
}
