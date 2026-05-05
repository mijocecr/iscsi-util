using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace ISCSI_Util.Helpers;

public static class ShellHelper
{
    private static long _callCount = 0;

    public static (int ExitCode, string Stdout, string Stderr) EjecutarComoRoot(string command)
    {
        var callId = ++_callCount;
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"[SHELL] #{callId} → EjecutarComoRoot('{command}')");

        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"-S -p '' {command}",   // -p '' evita prompts interactivos
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        Console.WriteLine($"[SHELL] #{callId} Iniciando proceso…");
        process.Start();

        // Enviar contraseña
        if (!string.IsNullOrEmpty(Credenciales.AdminPassword))
        {
            Console.WriteLine($"[SHELL] #{callId} Enviando contraseña…");
            var pass = Credenciales.AdminPassword.TrimEnd('\r', '\n');
            process.StandardInput.WriteLine(pass);
            process.StandardInput.Flush();
        }
        else
        {
            Console.WriteLine($"[SHELL] #{callId} ADVERTENCIA: No hay contraseña configurada");
        }

        // Cerrar stdin después de enviar la contraseña
        process.StandardInput.Close();

        // 🔥 NUEVO: lectura síncrona y completa de stdout/stderr
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        const int timeoutMs = 15000;
        Console.WriteLine($"[SHELL] #{callId} Esperando hasta {timeoutMs} ms…");

        if (!process.WaitForExit(timeoutMs))
        {
            Console.WriteLine($"[SHELL] #{callId} TIMEOUT tras {timeoutMs} ms. Matando proceso…");
            try { process.Kill(); } catch { }
            return (1, "", "Timeout");
        }

        sw.Stop();

        Console.WriteLine($"[SHELL] #{callId} ← Finalizado en {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"[SHELL] #{callId} ExitCode={process.ExitCode}");
        Console.WriteLine($"[SHELL] #{callId} STDOUT='{stdout.Trim()}'");
        Console.WriteLine($"[SHELL] #{callId} STDERR='{stderr.Trim()}'");

        // 🔥 DETECCIÓN DE CONTRASEÑA INCORRECTA
        if (stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[SHELL] #{callId} → CONTRASEÑA INCORRECTA DETECTADA");
            return (1001, stdout, "PASSWORD_INCORRECT");
        }

        return (process.ExitCode, stdout, stderr);
    }


    // ---------------------------------------------------------
    // EJECUCIÓN NORMAL (SIN ROOT) PARA SMBCLIENT, ETC.
    // ---------------------------------------------------------
    public static async Task<string> RunCleanAsync(string command)
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

        // Leer STDOUT completamente
        string stdout = await process.StandardOutput.ReadToEndAsync();

        // Leer STDERR pero NO mezclarlo
        string stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        // Si hay error, devolvemos vacío (NO basura)
        if (!string.IsNullOrWhiteSpace(stderr))
            return string.Empty;

        return stdout.Trim();
    }

}
