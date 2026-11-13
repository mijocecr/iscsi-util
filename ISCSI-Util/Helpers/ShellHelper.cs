using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Ejecución de comandos del sistema con timeout real.
///
/// Puntos importantes:
/// - stdout y stderr se leen en paralelo desde el primer momento. Leerlos de
///   forma secuencial puede bloquear el proceso si uno de los buffers se llena.
/// - El timeout se aplica con WaitForExitAsync(CancellationToken), no después
///   de leer las salidas. Si se cumple, se mata todo el árbol de procesos.
/// - Se usa ArgumentList en lugar de construir una cadena con comillas, lo que
///   evita problemas de escapado con rutas e IQNs.
/// </summary>
public static class ShellHelper
{
    private static long _callCount;

    /// <summary>Timeout por defecto para comandos que requieren root.</summary>
    public static TimeSpan DefaultRootTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Timeout por defecto para comandos de solo lectura.</summary>
    public static TimeSpan DefaultCleanTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private const int PasswordErrorExitCode = 1001;
    private const int TimeoutExitCode = 124;

    // ============================================================
    // ROOT (sudo)
    // ============================================================

    /// <summary>
    /// Ejecuta un comando con sudo de forma síncrona. Se mantiene por
    /// compatibilidad; en código nuevo se recomienda <see cref="EjecutarComoRootAsync"/>.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) EjecutarComoRoot(string command)
        => EjecutarComoRootAsync(command).GetAwaiter().GetResult();

    /// <summary>
    /// Ejecuta un comando con sudo, con timeout real y cancelación opcional.
    /// </summary>
    public static Task<(int ExitCode, string Stdout, string Stderr)> EjecutarComoRootAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return RunRootCoreAsync(
            command,
            timeout ?? DefaultRootTimeout,
            cancellationToken);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRootCoreAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var callId = Interlocked.Increment(ref _callCount);
        var sw = Stopwatch.StartNew();

        LogService.Debug(
            $"[SHELL] #{callId} → EjecutarComoRoot('{command}') timeout={timeout.TotalSeconds:0}s");

        if (string.IsNullOrWhiteSpace(command))
            return (1, "", "Empty command");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // sudo -S lee la contraseña de stdin. -p "" evita que escriba el
            // prompt en stderr. El comando va como argumento único de bash -c.
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Arrancar ambas lecturas antes de esperar evita el deadlock
            // clásico de leer stdout y stderr en secuencia.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await WritePasswordAndCloseAsync(process).ConfigureAwait(false);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillTreeAndWaitAsync(process).ConfigureAwait(false);

                var partialOut = await ReadWithTimeoutAsync(
                    stdoutTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var partialErr = await ReadWithTimeoutAsync(
                    stderrTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                sw.Stop();
                LogService.Error(
                    $"[SHELL] #{callId} TIMEOUT ({timeout.TotalSeconds:0}s) ejecutando '{command}'");

                var detail = string.IsNullOrWhiteSpace(partialErr)
                    ? $"Timeout after {timeout.TotalSeconds:0}s"
                    : $"{partialErr.Trim()}\nTimeout after {timeout.TotalSeconds:0}s";

                return (TimeoutExitCode, partialOut, detail);
            }

            var stdout = await ReadWithTimeoutAsync(
                stdoutTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var stderr = await ReadWithTimeoutAsync(
                stderrTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            sw.Stop();
            LogService.Debug(
                $"[SHELL] #{callId} ← exit={process.ExitCode} en {sw.ElapsedMilliseconds} ms");

            if (LooksLikePasswordError(stderr))
            {
                LogService.Error($"[SHELL] #{callId} PASSWORD_INCORRECT");
                return (PasswordErrorExitCode, stdout, "PASSWORD_INCORRECT");
            }

            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogService.Error($"[SHELL] #{callId} CANCELADO: '{command}'");
            return (1, "", "Cancelled");
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogService.Error(
                $"[SHELL] #{callId} EXCEPTION tras {sw.ElapsedMilliseconds} ms: {ex.Message}");
            return (1, "", ex.Message);
        }
    }

    private static async Task WritePasswordAndCloseAsync(Process process)
    {
        try
        {
            var password = Credenciales.AdminPassword;
            if (!string.IsNullOrEmpty(password))
            {
                await process.StandardInput
                    .WriteLineAsync(password.TrimEnd('\r', '\n'))
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // El proceso pudo terminar antes de escribir la contraseña.
        }
        catch (InvalidOperationException)
        {
            // stdin ya cerrado.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
        }
    }

    private static async Task<string> ReadWithTimeoutAsync(Task<string> readTask, TimeSpan wait)
    {
        try
        {
            return await readTask.WaitAsync(wait).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task KillTreeAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }

        // Esperar a que el proceso muera cierra los pipes y permite que las
        // lecturas pendientes terminen sin dejar tareas huérfanas.
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
        }
        catch
        {
            // Si no muere a tiempo, las lecturas tienen su propio timeout.
        }
    }

    private static bool LooksLikePasswordError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;

        return stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is not in the sudoers", StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // SIN ROOT
    // ============================================================

    /// <summary>
    /// Ejecuta un comando de solo lectura sin sudo, con timeout real.
    /// Devuelve la salida estándar o vacío si hubo error/timeout.
    /// </summary>
    public static async Task<string> RunCleanAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultCleanTimeout;

        LogService.Debug(
            $"[SHELL] RunCleanAsync('{command}') timeout={effectiveTimeout.TotalSeconds:0}s");

        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);

            using var process = new Process { StartInfo = psi };
            process.Start();

            try { process.StandardInput.Close(); } catch { }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillTreeAndWaitAsync(process).ConfigureAwait(false);

                await ReadWithTimeoutAsync(
                    stdoutTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await ReadWithTimeoutAsync(
                    stderrTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                LogService.Error(
                    $"[SHELL] RunCleanAsync TIMEOUT ({effectiveTimeout.TotalSeconds:0}s): {command}");
                return string.Empty;
            }

            var stdout = await ReadWithTimeoutAsync(
                stdoutTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var stderr = await ReadWithTimeoutAsync(
                stderrTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                LogService.Error($"[SHELL] RunCleanAsync stderr: {stderr}");
                return string.Empty;
            }

            if (process.ExitCode != 0)
            {
                LogService.Error(
                    $"[SHELL] RunCleanAsync exit={process.ExitCode}: {command}");
                return string.Empty;
            }

            LogService.Debug("[SHELL] RunCleanAsync OK");
            return stdout.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            LogService.Error($"[SHELL] RunCleanAsync exception: {ex.Message}");
            return string.Empty;
        }
    }
}
