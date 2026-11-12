using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Parser de los enlaces de <c>/dev/disk/by-path</c> de iSCSI.
/// Formato esperado (udev):
/// <c>ip-&lt;portal&gt;-iscsi-&lt;iqn&gt;-lun-&lt;lun&gt;[-partN]</c>
/// </summary>
public static class IscsiByPath
{
    // El IQN es codicioso para que, si contuviera "-lun-", el LUN real sea
    // siempre el último grupo numérico del enlace.
    private static readonly Regex Pattern = new(
        @"^ip-(?<portal>.+?)-iscsi-(?<iqn>.+)-lun-(?<lun>\d+)(?:-part\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public sealed record Entry(string Link, string Portal, string Iqn, int LunId);

    public static bool IsIscsiLink(string? link)
        => !string.IsNullOrWhiteSpace(link) &&
           link.Contains("iscsi", StringComparison.OrdinalIgnoreCase);

    public static bool IsPartitionLink(string? link)
        => !string.IsNullOrWhiteSpace(link) &&
           link.Contains("-part", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? link, out string portal, out string iqn, out int lunId)
    {
        portal = string.Empty;
        iqn = string.Empty;
        lunId = 0;

        if (string.IsNullOrWhiteSpace(link))
            return false;

        var match = Pattern.Match(link.Trim());
        if (!match.Success)
            return false;

        portal = match.Groups["portal"].Value;
        iqn = match.Groups["iqn"].Value;

        if (!int.TryParse(match.Groups["lun"].Value, out lunId))
            return false;

        return !string.IsNullOrWhiteSpace(portal) && !string.IsNullOrWhiteSpace(iqn);
    }

    public static IEnumerable<Entry> ParseAll(IEnumerable<string> links)
    {
        foreach (var link in links)
        {
            if (IsPartitionLink(link))
                continue;

            if (TryParse(link, out var portal, out var iqn, out var lun))
                yield return new Entry(link.Trim(), portal, iqn, lun);
        }
    }

    /// <summary>
    /// Devuelve el enlace del LUN solicitado. Si <paramref name="fallbackToFirstLun"/>
    /// es true y no hay coincidencia exacta, usa el LUN más bajo del target
    /// (comportamiento histórico para destinos de un solo LUN).
    /// </summary>
    public static Entry? Find(
        IEnumerable<string> links,
        string iqn,
        int lunId,
        bool fallbackToFirstLun = false)
    {
        var candidates = ParseAll(links)
            .Where(e => e.Iqn.Equals(iqn, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.LunId)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var exact = candidates.FirstOrDefault(e => e.LunId == lunId);
        if (exact != null)
            return exact;

        return fallbackToFirstLun ? candidates[0] : null;
    }

    /// <summary>
    /// Resuelve el LUN efectivo de un target cuando el modelo aún no lo conoce
    /// (por ejemplo, targets descubiertos desde la pestaña Targets).
    /// </summary>
    public static int ResolveLunOrDefault(IEnumerable<string> links, string iqn, int preferredLun)
    {
        var entry = Find(links, iqn, preferredLun, fallbackToFirstLun: preferredLun == 0);
        return entry?.LunId ?? preferredLun;
    }
}
