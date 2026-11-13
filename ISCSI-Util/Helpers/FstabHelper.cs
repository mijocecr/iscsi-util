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

    /// <summary>
    /// Elimina la entrada del punto de montaje de /etc/fstab.
    /// Devuelve <c>false</c> si no se pudo reescribir el archivo.
    /// </summary>
    public static bool RemoveMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            return true;

        var mpEscaped = mountPoint.Replace("'", "'\\''");

        // awk compara el segundo campo completo, no una subcadena.
        // El script devuelve un código distinto de cero si falla cualquier paso,
        // aunque la limpieza del temporal sí se haya podido hacer.
        var script =
            "tmp=$(mktemp /etc/.fstab.XXXXXX) || exit 1; " +
            $"if awk -v mp='{mpEscaped}' '$2 != mp' /etc/fstab > \"$tmp\" && " +
            "chmod --reference=/etc/fstab \"$tmp\" && " +
            "mv \"$tmp\" /etc/fstab; then exit 0; fi; " +
            "rm -f \"$tmp\"; exit 1";

        var result = ShellHelper.EjecutarComoRoot(script);

        if (result.ExitCode != 0)
        {
            LogService.Error(
                $"[FSTAB] No se pudo quitar '{mountPoint}' de /etc/fstab: {result.Stderr.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Añade una entrada a /etc/fstab.
    /// Devuelve <c>false</c> si no se pudo escribir el archivo.
    /// </summary>
    public static bool AddEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return true;

        var entryEscaped = entry.Replace("'", "'\\''");
        var result = ShellHelper.EjecutarComoRoot(
            $"printf '%s\\n' '{entryEscaped}' >> /etc/fstab");

        if (result.ExitCode != 0)
        {
            LogService.Error(
                $"[FSTAB] No se pudo escribir en /etc/fstab: {result.Stderr.Trim()}");
            return false;
        }

        return true;
    }
}
