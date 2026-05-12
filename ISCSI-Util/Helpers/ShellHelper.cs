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

        try
        {
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

            process.Start();

            if (!string.IsNullOrEmpty(Credenciales.AdminPassword))
            {
                var pass = Credenciales.AdminPassword.TrimEnd('\r', '\n');
                process.StandardInput.WriteLine(pass);
                process.StandardInput.Flush();
            }

            process.StandardInput.Close();

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            const int timeoutMs = 15000;

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }

                LogService.Error($"[SHELL] #{callId} TIMEOUT ejecutando '{command}'");
                return (1, "", "Timeout");
            }

            sw.Stop();
            LogService.Debug($"[SHELL] #{callId} ← exit={process.ExitCode} en {sw.ElapsedMilliseconds} ms");

            if (stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Error($"[SHELL] #{callId} PASSWORD_INCORRECT");
                return (1001, stdout, "PASSWORD_INCORRECT");
            }

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            LogService.Error($"[SHELL] #{callId} EXCEPTION: {ex.Message}");
            return (1, "", ex.Message);
        }
    }

    // ---------------------------------------------------------
    // EJECUCIÓN NORMAL (SIN ROOT)
    // ---------------------------------------------------------
    public static async Task<string> RunCleanAsync(string command)
    {
        LogService.Debug($"[SHELL] RunCleanAsync('{command}')");

        try
        {
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
                LogService.Error($"[SHELL] RunCleanAsync stderr: {stderr}");
                return string.Empty;
            }

            LogService.Debug($"[SHELL] RunCleanAsync OK");
            return stdout.Trim();
        }
        catch (Exception ex)
        {
            LogService.Error($"[SHELL] RunCleanAsync exception: {ex.Message}");
            return string.Empty;
        }
    }
}
