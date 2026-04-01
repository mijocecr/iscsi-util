using System.IO;

namespace ISCSI_Util.Utils;

public static class FileSystemUtils
{
    /// <summary>
    /// Creates a mount directory for an iSCSI target in /home/iscsi.
    /// Returns the full path of the created/existing directory.
    /// </summary>
    public static string CrearCarpetaMontaje(string iqn)
    {
        // Base path for mounting iSCSI targets
        string basePath = "/home/iscsi";
        string folderName = SanitizarNombre(iqn);
        string fullPath = Path.Combine(basePath, folderName);

        if (!Directory.Exists(fullPath))
            Directory.CreateDirectory(fullPath);

        return fullPath;
    }

    /// <summary>
    /// Sanitizes a string by replacing invalid filesystem characters with underscores.
    /// Returns "iscsi" if input is empty. Handles special characters like colons.
    /// </summary>
    public static string SanitizarNombre(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "iscsi";

        foreach (var c in Path.GetInvalidFileNameChars())
            input = input.Replace(c, '_');

        // Replace ':' explicitly (invalid in folder names)
        input = input.Replace(':', '_');

        return input;
    }
}

/// <summary>
/// Static class for storing admin credentials used for sudo commands.
/// Stores the sudo password in memory during the session.
/// </summary>
public static class Credenciales
{
    public static string AdminPassword { get; set; }
}
