using System;
using System.IO;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Operaciones sobre /etc/fstab que respetan el campo exacto del punto de
/// montaje. Evita que un LUN como <c>..._lun1</c> coincida por prefijo con
/// <c>..._lun12</c>.
/// </summary>
public static class FstabHelper
{
    /// <summary>
    /// Opciones obligatorias para un montaje iSCSI de red: no bloquear el
    /// arranque si el target no está disponible y esperar a iscsid.
    /// </summary>
    public const string NetworkOptions =
        "defaults,_netdev,nofail,x-systemd.requires=iscsid.service,x-systemd.device-timeout=30";

    public static bool HasMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || !File.Exists("/etc/fstab"))
            return false;

        try
        {
            foreach (var line in File.ReadAllLines("/etc/fstab"))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var fields = trimmed.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries
                );

                if (fields.Length >= 2 &&
                    fields[1].Equals(mountPoint, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[FSTAB] No se pudo leer /etc/fstab: {ex.Message}");
        }

        return false;
    }

    public static void RemoveMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            return;

        var mpEscaped = mountPoint.Replace("'", "'\\''");

        // awk compara el segundo campo completo, no una subcadena.
        var script =
            "tmp=$(mktemp /etc/.fstab.XXXXXX) && " +
            $"awk -v mp='{mpEscaped}' '$2 != mp' /etc/fstab > \"$tmp\" && " +
            "chmod --reference=/etc/fstab \"$tmp\" && " +
            "mv \"$tmp\" /etc/fstab || rm -f \"$tmp\"";

        ShellHelper.EjecutarComoRoot(script);
    }

    public static void AddEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;

        var entryEscaped = entry.Replace("'", "'\\''");
        ShellHelper.EjecutarComoRoot($"printf '%s\\n' '{entryEscaped}' >> /etc/fstab");
    }
}
