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

    public static int DefaultPermissions { get; set; } = 777;
    
    public static string MountBasePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mnt", "iscsi");


    // Ruta por defecto: ~/.local/share/iscsi-util/logs
    public static string LogPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/iscsi-util/logs");

    public static bool Verbose { get; set; } = false;

    public static void Load()
    {
        LogService.Debug("[CONFIG] Load() iniciado.");

        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                LogService.Debug($"[CONFIG] Creando carpeta de configuración: {ConfigDir}");
                Directory.CreateDirectory(ConfigDir);
            }

            if (!File.Exists(ConfigPath))
            {
                LogService.Debug("[CONFIG] config.json no existe. Generando archivo nuevo...");
                Save();
                return;
            }

            var json = File.ReadAllText(ConfigPath);

            if (string.IsNullOrWhiteSpace(json))
            {
                LogService.Error("[CONFIG] config.json vacío. Regenerando...");
                Save();
                return;
            }

            var cfg = JsonSerializer.Deserialize<ConfigData>(json);

            if (cfg == null)
            {
                LogService.Error("[CONFIG] config.json corrupto. Regenerando...");
                Save();
                return;
            }

            DefaultPermissions = cfg.DefaultPermissions;
            MountBasePath = cfg.MountBasePath ?? "/mnt/iscsi";
            LogPath = cfg.LogPath ?? LogPath;
            Verbose = cfg.Verbose;

            LogService.Debug($"[CONFIG] Valores cargados: perms={DefaultPermissions}, mount={MountBasePath}, logs={LogPath}, verbose={Verbose}");

            if (!Directory.Exists(LogPath))
            {
                LogService.Debug($"[CONFIG] Creando carpeta de logs: {LogPath}");
                Directory.CreateDirectory(LogPath);
            }

            LogService.Debug("[CONFIG] Load() completado.");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CONFIG] ERROR Load(): {ex.Message}");
            Save();
        }
    }

    public static void Save()
    {
        LogService.Debug("[CONFIG] Save() iniciado.");

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
        {
            LogService.Debug($"[CONFIG] Creando carpeta de configuración: {ConfigDir}");
            Directory.CreateDirectory(ConfigDir);
        }

        // Escritura atómica: se escribe primero un temporal y luego se
        // reemplaza config.json con un rename. Un corte a mitad de guardado
        // deja intacto el archivo anterior en vez de truncarlo.
        var tempPath = ConfigPath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // La limpieza del temporal no debe ocultar el error original.
            }

            LogService.Error($"[CONFIG] ERROR Save(): {ex.Message}");
            throw;
        }

        LogService.Debug($"[CONFIG] Archivo guardado en {ConfigPath}");

        if (!Directory.Exists(LogPath))
        {
            LogService.Debug($"[CONFIG] Creando carpeta de logs: {LogPath}");
            Directory.CreateDirectory(LogPath);
        }

        LogService.Debug("[CONFIG] Save() completado.");
    }

    private class ConfigData
    {
        public int DefaultPermissions { get; set; }
        public string MountBasePath { get; set; }
        public string LogPath { get; set; }
        public bool Verbose { get; set; }
    }
}
