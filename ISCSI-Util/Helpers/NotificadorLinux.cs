using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Envía notificaciones de escritorio con notify-send.
///
/// Todo el trabajo externo (comprobar notify-send y lanzarlo) se hace en
/// segundo plano. Antes se ejecutaba `which notify-send` de forma bloqueante
/// en el hilo que llamaba, que podía ser el hilo de UI.
/// </summary>
public static class NotificadorLinux
{
    private static readonly object Sync = new();

    private static readonly Lazy<bool> NotifySendDisponible = new(
        DetectarNotifySend,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string _lastMessage = "";
    private static DateTime _lastTime = DateTime.MinValue;

    /// <summary>
    /// Envía una notificación de escritorio sin bloquear al llamador.
    /// </summary>
    /// <param name="mensaje">Mensaje a mostrar.</param>
    /// <param name="duracionMs">Duración en milisegundos (por defecto 5000).</param>
    /// <param name="urgencia">Urgencia: low, normal o critical.</param>
    /// <param name="icono">Icono opcional.</param>
    public static void Enviar(
        string mensaje,
        int duracionMs = 5000,
        string urgencia = "normal",
        string? icono = "iscsi-util")
    {
        try
        {
            // Anti-spam: evitar notificaciones idénticas en menos de 1 segundo.
            lock (Sync)
            {
                if (mensaje == _lastMessage &&
                    (DateTime.UtcNow - _lastTime).TotalMilliseconds < 1000)
                {
                    return;
                }

                _lastMessage = mensaje;
                _lastTime = DateTime.UtcNow;
            }

            // La comprobación de notify-send y el envío se hacen en segundo
            // plano. Así esta llamada nunca bloquea la UI ni el thread pool,
            // aunque `which` o `notify-send` tarden en responder.
            _ = Task.Run(() =>
            {
                if (!NotifySendDisponible.Value)
                {
                    Console.WriteLine($"[NOTIFICACIÓN] {mensaje}");
                    return;
                }

                EnviarCore(mensaje, duracionMs, urgencia, icono);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NOTIFICACIÓN] {mensaje} ({ex.Message})");
        }
    }

    private static void EnviarCore(
        string mensaje,
        int duracionMs,
        string urgencia,
        string? icono)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "notify-send",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(duracionMs.ToString());

            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(urgencia) ? "normal" : urgencia);

            if (!string.IsNullOrWhiteSpace(icono))
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(icono);
            }

            psi.ArgumentList.Add("iSCSI Management");
            psi.ArgumentList.Add(mensaje);

            using var process = Process.Start(psi);
            if (process == null)
                return;

            // No esperamos indefinidamente: si notify-send se queda colgado,
            // el proceso sigue su curso pero no bloquea a nadie.
            process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NOTIFICACIÓN] {mensaje} ({ex.Message})");
        }
    }

    private static bool DetectarNotifySend()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("notify-send");

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
