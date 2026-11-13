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
/// - Solo se ejecuta un comando sudo a la vez. Así se evita que varias
///   operaciones envíen la contraseña simultáneamente y que una operación
///   colgada bloquee a todas las demás.
/// - Si sudo rechaza la contraseña, no se vuelve a intentar con la misma
///   contraseña. Eso reduce el riesgo de disparar el bloqueo de la cuenta
///   (faillock/PAM) por reintentos automáticos.
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

    // Un único sudo en vuelo. El timeout de la espera usa el mismo límite que
    // el comando, de modo que una operación no puede quedarse esperando
    // indefinidamente detrás de otra.
    private static readonly SemaphoreSlim SudoGate = new(1, 1);

    // Contraseña que sudo ya rechazó. Mientras no cambie, no se vuelve a
    // enviar para evitar intentos repetidos que podrían bloquear la cuenta.
    private static readonly object PasswordStateSync = new();
    private static string _rejectedPassword = "";

    // Re-prompt: cuando sudo rechaza la contraseña, la aplicación puede
    // registrar aquí un diálogo para pedir una nueva. Solo se muestra uno a la
    // vez aunque varias operaciones fallen al mismo tiempo.
    private static readonly object PromptSync = new();
    private static Task<string?>? _activePrompt;

    /// <summary>
    /// Delegado opcional que pide una contraseña nueva al usuario. Si es
    /// <c>null</c> (por ejemplo en la CLI), un rechazo de sudo no reintenta.
    /// </summary>
    public static Func<Task<string?>>? PasswordPrompt { get; set; }

    /// <summary>Indica si la contraseña actual fue rechazada por sudo.</summary>
    public static bool HasRejectedPassword
    {
        get
        {
            lock (PasswordStateSync)
                return _rejectedPassword.Length > 0;
        }
    }

    /// <summary>
    /// Olvida el rechazo anterior. Se llama cuando el usuario introduce una
    /// contraseña nueva.
    /// </summary>
    public static void ResetPasswordRejection()
    {
        lock (PasswordStateSync)
            _rejectedPassword = "";
    }

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
        var attemptPassword = Credenciales.AdminPassword ?? "";

        var result = await RunRootOnceAsync(command, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!IsPasswordFailure(result) || PasswordPrompt == null ||
            cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        // Si otra operación ya consiguió una contraseña nueva, no se vuelve a
        // preguntar: basta con reintentar con la actual.
        var currentPassword = Credenciales.AdminPassword ?? "";

        if (string.Equals(currentPassword, attemptPassword, StringComparison.Ordinal))
        {
            LogService.Write("[SHELL] Sudo rechazó la contraseña; pidiendo una nueva.");

            var newPassword = await RequestNewPasswordAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                LogService.Error(
                    "[SHELL] El usuario no introdujo una contraseña nueva; no se reintenta.");
                return result;
            }

            // Reenviar exactamente la misma contraseña que sudo acaba de
            // rechazar solo gastaría otro intento de la cuenta.
            if (string.Equals(newPassword, attemptPassword, StringComparison.Ordinal))
            {
                LogService.Error(
                    "[SHELL] La contraseña nueva coincide con la rechazada; no se reintenta.");
                return result;
            }

            Credenciales.AdminPassword = newPassword;
            ResetPasswordRejection();
        }

        // Un único reintento, siempre con una contraseña distinta.
        return await RunRootOnceAsync(command, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsPasswordFailure((int ExitCode, string Stdout, string Stderr) result)
        => result.ExitCode == PasswordErrorExitCode &&
           (result.Stderr == "PASSWORD_INCORRECT" ||
            result.Stderr == "PASSWORD_REJECTED");

    private static async Task<string?> RequestNewPasswordAsync(CancellationToken cancellationToken)
    {
        var prompt = PasswordPrompt;
        if (prompt == null)
            return null;

        Task<string?> task;

        lock (PromptSync)
        {
            if (_activePrompt == null || _activePrompt.IsCompleted)
                _activePrompt = Task.Run(prompt);

            task = _activePrompt;
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Error($"[SHELL] Error pidiendo la contraseña: {ex.Message}");
            return null;
        }
        finally
        {
            lock (PromptSync)
            {
                if (ReferenceEquals(_activePrompt, task) && task.IsCompleted)
                    _activePrompt = null;
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRootOnceAsync(
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

        var password = Credenciales.AdminPassword ?? "";

        if (string.IsNullOrWhiteSpace(password))
        {
            LogService.Error(
                $"[SHELL] #{callId} SIN CONTRASEÑA: no se ejecuta '{command}'");
            return (PasswordErrorExitCode, "", "PASSWORD_REQUIRED");
        }

        if (IsPasswordRejected(password))
        {
            LogService.Error(
                $"[SHELL] #{callId} CONTRASEÑA RECHAZADA previamente: no se ejecuta '{command}'");
            return (PasswordErrorExitCode, "", "PASSWORD_REJECTED");
        }

        var gateTaken = false;

        try
        {
            if (!await SudoGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                sw.Stop();
                LogService.Error(
                    $"[SHELL] #{callId} TIMEOUT esperando turno de sudo para '{command}'");
                return (TimeoutExitCode, "", $"Timeout after {timeout.TotalSeconds:0}s waiting for sudo lock");
            }

            gateTaken = true;

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

            await WritePasswordAndCloseAsync(process, password).ConfigureAwait(false);

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                remaining = TimeSpan.FromMilliseconds(1);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(remaining);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillTreeAndWaitAsync(process).ConfigureAwait(false);

                var partialOut = await ReadWithTimeoutAsync(
                    stdoutTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var partialErr = await ReadWithTimeoutAsync(
                    stderrTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                sw.Stop();

                if (cancellationToken.IsCancellationRequested)
                {
                    LogService.Error($"[SHELL] #{callId} CANCELADO: '{command}'");
                    return (1, partialOut, "Cancelled");
                }

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

            if (LooksLikeIncorrectPassword(stderr))
            {
                MarkPasswordRejected(password);
                LogService.Error($"[SHELL] #{callId} PASSWORD_INCORRECT");
                return (PasswordErrorExitCode, stdout, "PASSWORD_INCORRECT");
            }

            if (LooksLikePasswordError(stderr))
            {
                LogService.Error($"[SHELL] #{callId} PASSWORD_ERROR: {stderr.Trim()}");
                return (PasswordErrorExitCode, stdout, stderr.Trim());
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
        finally
        {
            if (gateTaken)
                SudoGate.Release();
        }
    }

    private static async Task WritePasswordAndCloseAsync(Process process, string password)
    {
        try
        {
            await process.StandardInput
                .WriteLineAsync(password.TrimEnd('\r', '\n'))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
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

    private static bool LooksLikeIncorrectPassword(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;

        return stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePasswordError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;

        return LooksLikeIncorrectPassword(stderr)
            || stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is not in the sudoers", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPasswordRejected(string password)
    {
        lock (PasswordStateSync)
        {
            if (_rejectedPassword.Length == 0)
                return false;

            // Si la contraseña cambió, el rechazo anterior ya no aplica.
            if (!string.Equals(_rejectedPassword, password, StringComparison.Ordinal))
            {
                _rejectedPassword = "";
                return false;
            }

            return true;
        }
    }

    private static void MarkPasswordRejected(string password)
    {
        lock (PasswordStateSync)
            _rejectedPassword = password;
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
        var result = await RunCleanResultAsync(command, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            LogService.Error(
                $"[SHELL] RunCleanAsync exit={result.ExitCode}: {command}");
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            LogService.Error($"[SHELL] RunCleanAsync stderr: {result.Stderr}");
            return string.Empty;
        }

        return result.Stdout.Trim();
    }

    /// <summary>
    /// Igual que <see cref="RunCleanAsync"/> pero devuelve también el código de
    /// salida. Necesario para comandos como `systemctl is-active`, donde un
    /// código distinto de cero es una respuesta válida (inactivo).
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCleanResultAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultCleanTimeout;

        LogService.Debug(
            $"[SHELL] RunCleanAsync('{command}') timeout={effectiveTimeout.TotalSeconds:0}s");

        if (string.IsNullOrWhiteSpace(command))
            return (1, "", "Empty command");

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
            catch (OperationCanceledException)
            {
                await KillTreeAndWaitAsync(process).ConfigureAwait(false);

                var partialOut = await ReadWithTimeoutAsync(
                    stdoutTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var partialErr = await ReadWithTimeoutAsync(
                    stderrTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    LogService.Error($"[SHELL] RunCleanAsync CANCELADO: {command}");
                    return (1, partialOut, "Cancelled");
                }

                LogService.Error(
                    $"[SHELL] RunCleanAsync TIMEOUT ({effectiveTimeout.TotalSeconds:0}s): {command}");
                return (TimeoutExitCode, partialOut,
                    $"Timeout after {effectiveTimeout.TotalSeconds:0}s");
            }

            var stdout = await ReadWithTimeoutAsync(
                stdoutTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var stderr = await ReadWithTimeoutAsync(
                stderrTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (1, "", "Cancelled");
        }
        catch (Exception ex)
        {
            LogService.Error($"[SHELL] RunCleanAsync exception: {ex.Message}");
            return (1, "", ex.Message);
        }
    }

    /// <summary>
    /// Variante síncrona de <see cref="RunCleanAsync"/> para código que ya se
    /// ejecuta en el thread pool. No usar desde el hilo de UI.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) RunClean(
        string command,
        TimeSpan? timeout = null)
        => RunCleanResultAsync(command, timeout).GetAwaiter().GetResult();
}
