using System;
using System.IO;
using ISCSI_Util.Services;

namespace ISCSI_Util.Services;

public static class LogService
{
    private static readonly object _lock = new();

    private static string LogFilePath =>
        Path.Combine(ConfigManager.LogPath, "iscsi-util.log");

    // ============================================================
    //  MÉTODO PRINCIPAL (siempre escribe)
    // ============================================================
    public static void Write(string message)
    {
        try
        {
            EnsureDirectory();

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string line = $"[{timestamp}] {message}";

            lock (_lock)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Nunca lanzar excepciones desde el logger
        }
    }

    // ============================================================
    //  ERROR (siempre escribe)
    // ============================================================
    public static void Error(string message)
    {
        Write($"[ERROR] {message}");
    }

    // ============================================================
    //  DEBUG (solo si verbose está activado)
    // ============================================================
    public static void Debug(string message)
    {
        if (ConfigManager.Verbose)
            Write($"[DEBUG] {message}");
    }

    // ============================================================
    //  ASEGURAR DIRECTORIO
    // ============================================================
    private static void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(ConfigManager.LogPath))
                Directory.CreateDirectory(ConfigManager.LogPath);
        }
        catch
        {
            // Silencio total: el logger nunca debe romper la app
        }
    }
}