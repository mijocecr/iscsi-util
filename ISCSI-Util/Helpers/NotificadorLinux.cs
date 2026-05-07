using System;
using System.Diagnostics;
using System.IO;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Sends desktop notifications to Linux using notify-send.
/// Supports duration, urgency, and icons.
/// Falls back to console output if notify-send is not available.
/// </summary>
public static class NotificadorLinux
{
    /// <summary>
    /// Sends a desktop notification.
    /// </summary>
    /// <param name="mensaje">Message to display.</param>
    /// <param name="duracionMs">Duration in milliseconds (default: 5000).</param>
    /// <param name="urgencia">Urgency level: low, normal, critical.</param>
    /// <param name="icono">Optional icon name or path.</param>
    public static void Enviar(
        string mensaje,
        int duracionMs = 5000,
        string urgencia = "normal",
        string? icono = "iscsi-util")
    {
        try
        {
            if (!NotifySendDisponible())
            {
                Console.WriteLine($"[NOTIFICACIÓN] {mensaje}");
                return;
            }

            string iconArg = icono != null ? $"-i \"{icono}\"" : "";
            string args = $"-t {duracionMs} -u {urgencia} {iconArg} \"iSCSI Management\" \"{mensaje}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                Arguments = args,
                UseShellExecute = false
            });
        }
        catch
        {
            Console.WriteLine($"[NOTIFICACIÓN] {mensaje}");
        }
    }

    /// <summary>
    /// Checks if notify-send is available in the system.
    /// </summary>
    private static bool NotifySendDisponible()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "notify-send",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
