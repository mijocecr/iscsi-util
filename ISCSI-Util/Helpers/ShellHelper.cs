using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class ShellHelper
{
    private static long _callCount = 0;

    public static (int ExitCode, string Stdout, string Stderr) EjecutarComoRoot(string command)
    {
        var callId = ++_callCount;
        var sw = Stopwatch.StartNew();

        LogService.Debug($"[SHELL] #{callId} → EjecutarComoRoot('{command}')");

        // 🔥 SIEMPRE usar bash -c para soportar redirecciones, sed, systemctl, etc.
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"-S bash -c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        LogService.Debug($"[SHELL] #{callId} Iniciando proceso…");
        process.Start();

        // Enviar contraseña
        if (!string.IsNullOrEmpty(Credenciales.AdminPassword))
        {
            LogService.Debug($"[SHELL] #{callId} Enviando contraseña…");
            var pass = Credenciales.AdminPassword.TrimEnd('\r', '\n');
            process.StandardInput.WriteLine(pass);
            process.StandardInput.Flush();
        }
        else
        {
            LogService.Error($"[SHELL] #{callId} ADVERTENCIA: No hay contraseña configurada");
        }

        process.StandardInput.Close();

        // Leer stdout/stderr
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        const int timeoutMs = 15000;
        LogService.Debug($"[SHELL] #{callId} Esperando hasta {timeoutMs} ms…");

        if (!process.WaitForExit(timeoutMs))
        {
            LogService.Error($"[SHELL] #{callId} TIMEOUT tras {timeoutMs} ms. Matando proceso…");
            try { process.Kill(); } catch { }
            return (1, "", "Timeout");
        }

        sw.Stop();

        LogService.Debug($"[SHELL] #{callId} ← Finalizado en {sw.ElapsedMilliseconds} ms");
        LogService.Debug($"[SHELL] #{callId} ExitCode={process.ExitCode}");
        LogService.Debug($"[SHELL] #{callId} STDOUT='{stdout.Trim()}'");
        LogService.Debug($"[SHELL] #{callId} STDERR='{stderr.Trim()}'");

        // Detección de contraseña incorrecta
        if (stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Error($"[SHELL] #{callId} → CONTRASEÑA INCORRECTA DETECTADA");
            return (1001, stdout, "PASSWORD_INCORRECT");
        }

        return (process.ExitCode, stdout, stderr);
    }

    // ---------------------------------------------------------
    // EJECUCIÓN NORMAL (SIN ROOT)
    // ---------------------------------------------------------
    public static async Task<string> RunCleanAsync(string command)
    {
        LogService.Debug($"[SHELL] RunCleanAsync('{command}')");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.Start();

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            LogService.Error($"[SHELL] RunCleanAsync STDERR='{stderr.Trim()}'");
            return string.Empty;
        }

        LogService.Debug($"[SHELL] RunCleanAsync STDOUT='{stdout.Trim()}'");
        return stdout.Trim();
    }
}
