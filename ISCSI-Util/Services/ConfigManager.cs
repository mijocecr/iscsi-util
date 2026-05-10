using System;
using System.IO;
using System.Text.Json;

namespace ISCSI_Util.Services;

public static class ConfigManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "iscsi-util");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "config.json");

    public static int DefaultPermissions { get; set; } = 755;
    public static string MountBasePath { get; set; } = "/mnt/iscsi";

    // Ruta por defecto: ~/.local/share/iscsi-util/logs
    public static string LogPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/iscsi-util/logs");

    public static bool Verbose { get; set; } = false;

    public static void Load()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            if (!File.Exists(ConfigPath))
            {
                Save();
                return;
            }

            var json = File.ReadAllText(ConfigPath);

            // JSON vacío → regenerar
            if (string.IsNullOrWhiteSpace(json))
            {
                Save();
                return;
            }

            var cfg = JsonSerializer.Deserialize<ConfigData>(json);

            // JSON corrupto → regenerar
            if (cfg == null)
            {
                Save();
                return;
            }

            // Asignar valores (con fallback si faltan)
            DefaultPermissions = cfg.DefaultPermissions;
            MountBasePath = cfg.MountBasePath ?? "/mnt/iscsi";
            LogPath = cfg.LogPath ?? LogPath;
            Verbose = cfg.Verbose;

            // Asegurar carpeta de logs
            if (!Directory.Exists(LogPath))
                Directory.CreateDirectory(LogPath);
        }
        catch
        {
            // fallback seguro
            Save();
        }
    }

    public static void Save()
    {
        var cfg = new ConfigData
        {
            DefaultPermissions = DefaultPermissions,
            MountBasePath = MountBasePath,
            LogPath = LogPath,
            Verbose = Verbose
        };

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        if (!Directory.Exists(ConfigDir))
            Directory.CreateDirectory(ConfigDir);

        File.WriteAllText(ConfigPath, json);

        // Crear carpeta de logs si no existe
        if (!Directory.Exists(LogPath))
            Directory.CreateDirectory(LogPath);
    }

    private class ConfigData
    {
        public int DefaultPermissions { get; set; }
        public string MountBasePath { get; set; }
        public string LogPath { get; set; }
        public bool Verbose { get; set; }
    }
}
