using System;
using System.Linq;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Utilidades para tratar credenciales CHAP procedentes de open-iscsi.
///
/// open-iscsi enmascara los secretos al leer la base de datos de nodos
/// (por ejemplo "********"). Ese valor indica que el secreto está
/// configurado, pero nunca debe reutilizarse como contraseña real ni
/// escribirse de vuelta en la configuración del nodo.
/// </summary>
public static class IscsiSecrets
{
    private const int MinMaskLength = 6;

    private static string Clean(string? value) =>
        (value ?? "").Trim().Trim('"');

    /// <summary>Valor ausente o marcado por open-iscsi como &lt;empty&gt;.</summary>
    public static bool IsEmpty(string? value)
    {
        var clean = Clean(value);
        return clean.Length == 0 ||
               clean.Equals("<empty>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Valor enmascarado por open-iscsi (nunca es el secreto real).</summary>
    public static bool IsMasked(string? value)
    {
        var clean = Clean(value);
        return clean.Length >= MinMaskLength && clean.All(c => c == '*');
    }

    /// <summary>Hay un valor configurado (aunque esté enmascarado).</summary>
    public static bool IsConfigured(string? value) => !IsEmpty(value);

    /// <summary>El valor se puede reutilizar como secreto real.</summary>
    public static bool IsUsable(string? value) => !IsEmpty(value) && !IsMasked(value);

    /// <summary>Devuelve el secreto solo si es real; si no, cadena vacía.</summary>
    public static string Usable(string? value) => IsUsable(value) ? Clean(value) : "";

    /// <summary>Normaliza un portal a IP y puerto, admitiendo IPv4, IPv6 y puerto opcional.</summary>
    public static (string Ip, int Port) NormalizePortal(string? portal)
    {
        if (string.IsNullOrWhiteSpace(portal))
            return ("", 3260);

        var value = portal.Trim();

        // IPv6 entre corchetes: [2001:db8::1]:3260
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                var host = value[1..close];
                var rest = value[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketPort))
                    return (host, bracketPort);
                return (host, 3260);
            }
        }

        // IPv6 sin corchetes y sin puerto.
        if (value.Count(c => c == ':') > 1)
            return (value, 3260);

        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port))
            return (value[..separator], port);

        return (value, 3260);
    }

    /// <summary>Portal listo para pasar a iscsiadm; omite el puerto por defecto.</summary>
    public static string PortalArg(string? portal)
    {
        var (ip, port) = NormalizePortal(portal);
        if (string.IsNullOrWhiteSpace(ip))
            return "";

        return port == 3260 ? ip : $"{ip}:{port}";
    }

    /// <summary>Portal con puerto explícito, útil para logs y comparaciones.</summary>
    public static string PortalWithPort(string? portal)
    {
        var (ip, port) = NormalizePortal(portal);
        if (string.IsNullOrWhiteSpace(ip))
            return "";

        return $"{ip}:{port}";
    }
}
