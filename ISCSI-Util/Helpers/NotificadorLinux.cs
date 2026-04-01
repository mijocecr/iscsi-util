using System;
using System.Diagnostics;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Sends desktop notifications to Linux using notify-send.
/// Falls back to console output if notify-send is not available.
/// </summary>
public static class NotificadorLinux
{
    /// <summary>
    /// Sends a desktop notification with the given message.
    /// </summary>
    public static void Enviar(string mensaje)
    {
        try
        {
            Process.Start("notify-send", $"\"ISCSI Util\" \"{mensaje}\"");
        }
        catch
        {
            Console.WriteLine($"[NOTIFICACIÓN] {mensaje}");
        }
    }
}
