using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Fuente única de verdad para los identificadores estables de un destino iSCSI.
/// El LUN forma parte de la identidad: dos LUNs del mismo target nunca deben
/// compartir punto de montaje ni unidad systemd.
/// </summary>
public static class IscsiNaming
{
    public const string DefaultMountBasePath = "/mnt/iscsi";

    /// <summary>
    /// Nombre seguro usado en el punto de montaje. Se mantiene idéntico al
    /// esquema histórico para el LUN 0, de forma que las instalaciones
    /// existentes no cambian de ruta.
    /// </summary>
    public static string MountSafeIqn(string? iqn) => SafeName(iqn);

    /// <summary>
    /// Nombre seguro para unidades systemd y scripts auxiliares.
    /// </summary>
    public static string SystemdSafe(string? value) => SafeName(value);

    /// <summary>
    /// Reduce cualquier identificador a caracteres seguros para rutas y
    /// unidades systemd. Un IQN válido solo usa letras, dígitos, '.', '-' y
    /// ':', por lo que su nombre histórico no cambia.
    /// </summary>
    private static string SafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "iscsi_target";

        var chars = value.Select(c =>
            c is >= 'a' and <= 'z' or
                 >= 'A' and <= 'Z' or
                 >= '0' and <= '9'
                ? c
                : '_'
        ).ToArray();

        return new string(chars);
    }

    public static string Hash(string? value, int length = 8)
    {
        var hex = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))
        );

        return hex[..Math.Min(length, hex.Length)];
    }

    /// <summary>
    /// Punto de montaje estable. Para LUN 0 conserva el formato anterior
    /// (<c>{iqn}_{hash}</c>); para LUN &gt; 0 añade el sufijo <c>_lunN</c>.
    /// </summary>
    public static string MountPoint(string? basePath, string? iqn, int lunId)
    {
        var root = string.IsNullOrWhiteSpace(basePath)
            ? DefaultMountBasePath
            : basePath.Trim();

        if (!root.StartsWith('/'))
            root = "/" + root;

        var name = $"{MountSafeIqn(iqn)}_{Hash(iqn)}";

        if (lunId > 0)
            name += $"_lun{lunId}";

        return Path.Combine(root, name);
    }

    public static string ServiceName(string iqn, int lunId)
    {
        var safe = SystemdSafe(iqn);
        return lunId > 0 ? $"iscsi-{safe}-lun{lunId}" : $"iscsi-{safe}";
    }

    public static string ServicePath(string iqn, int lunId)
        => $"/etc/systemd/system/{ServiceName(iqn, lunId)}.service";

    public static string ScriptPath(string iqn, int lunId)
        => $"/usr/local/bin/mount-{ServiceName(iqn, lunId)}.sh";
}
