using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public enum LunRescanKind
{
    Unchanged,
    Added,
    Resized,
    Removed
}

/// <summary>
/// Resultado del reescaneo de un LUN concreto dentro de un target.
/// </summary>
public sealed class LunRescanChange
{
    public int LunId { get; init; }
    public LunRescanKind Kind { get; init; }
    public string Device { get; init; } = string.Empty;
    public string PreviousDevice { get; init; } = string.Empty;
    public string MountPoint { get; init; } = string.Empty;
    public string Filesystem { get; init; } = string.Empty;
    public long PreviousSizeBytes { get; init; }
    public long SizeBytes { get; init; }
    public bool IsMounted { get; init; }
    public bool FilesystemExpanded { get; set; }
    public string FilesystemMessage { get; set; } = string.Empty;

    public bool DeviceChanged =>
        !string.IsNullOrWhiteSpace(PreviousDevice) &&
        !string.IsNullOrWhiteSpace(Device) &&
        !PreviousDevice.Equals(Device, StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var currentSize = IscsiRescan.FormatBytes(SizeBytes);
        var previousSize = IscsiRescan.FormatBytes(PreviousSizeBytes);

        var description = Kind switch
        {
            LunRescanKind.Added =>
                $"LUN {LunId}: new device {Device} ({currentSize})",

            LunRescanKind.Removed =>
                $"LUN {LunId}: no longer present (was {PreviousDevice}, {previousSize})" +
                (IsMounted ? " [still mounted]" : string.Empty),

            LunRescanKind.Resized =>
                $"LUN {LunId}: {previousSize} -> {currentSize}" + GrowthSuffix(),

            _ => $"LUN {LunId}: unchanged"
        };

        if (DeviceChanged && Kind != LunRescanKind.Removed)
            description += $" [device {PreviousDevice} -> {Device}]";

        return description;
    }

    private string GrowthSuffix()
    {
        if (FilesystemExpanded)
            return " (filesystem expanded online)";

        if (!string.IsNullOrWhiteSpace(FilesystemMessage))
            return $" ({FilesystemMessage})";

        return IsMounted ? string.Empty : " (volume not mounted)";
    }
}

/// <summary>
/// Resultado del reescaneo de un target completo (todos sus LUNs).
/// </summary>
public sealed class TargetRescanResult
{
    public string Iqn { get; init; } = string.Empty;
    public string Portal { get; init; } = string.Empty;
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public List<LunRescanChange> Changes { get; } = new();

    public IEnumerable<LunRescanChange> ChangedLuns =>
        Changes.Where(c => c.Kind != LunRescanKind.Unchanged);

    public bool HasChanges => ChangedLuns.Any();

    public string DisplayTarget =>
        Iqn.LastIndexOf(':') is var index && index >= 0 && index < Iqn.Length - 1
            ? Iqn[(index + 1)..]
            : Iqn;

    public string ShortSummary
    {
        get
        {
            if (!Success)
                return "rescan failed";

            var added = Changes.Count(c => c.Kind == LunRescanKind.Added);
            var resized = Changes.Count(c => c.Kind == LunRescanKind.Resized);
            var removed = Changes.Count(c => c.Kind == LunRescanKind.Removed);

            var parts = new List<string>();
            if (added > 0)
                parts.Add($"{added} new LUN{(added == 1 ? string.Empty : "s")}");
            if (resized > 0)
                parts.Add($"{resized} resized");
            if (removed > 0)
                parts.Add($"{removed} removed");

            return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
        }
    }

    public string Summary
    {
        get
        {
            if (!Success)
                return $"Rescan failed: {Message}";

            var changed = ChangedLuns.ToList();
            return changed.Count == 0
                ? "No LUN or size changes detected."
                : string.Join("; ", changed.Select(c => c.Describe()));
        }
    }
}

/// <summary>
/// Reescaneo en caliente de sesiones iSCSI. No desconecta la sesión ni
/// desmonta volúmenes: pide a open-iscsi que vuelva a consultar los LUNs,
/// refresca la tabla de particiones cuando es posible y, si un volumen
/// montado ha crecido, intenta ampliar su sistema de archivos en línea.
/// </summary>
public static class IscsiRescan
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    public static async Task<List<TargetRescanResult>> RescanAllAsync(
        IReadOnlyList<SessionInfo>? beforeSessions = null)
    {
        var sessions = beforeSessions ?? await IscsiSessions.ObtenerVistaGlobal();

        var targets = sessions
            .Where(s => s.Connected &&
                        !string.IsNullOrWhiteSpace(s.Iqn) &&
                        !string.IsNullOrWhiteSpace(s.Portal))
            .GroupBy(s => $"{s.Iqn}|{s.Portal}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var results = new List<TargetRescanResult>();
        foreach (var target in targets)
            results.Add(await RescanTargetAsync(target.Iqn, target.Portal, sessions));

        return results;
    }

    public static async Task<TargetRescanResult> RescanTargetAsync(
        string iqn,
        string portal,
        IReadOnlyList<SessionInfo>? beforeSessions = null,
        bool expandFilesystems = true)
    {
        var result = new TargetRescanResult { Iqn = iqn, Portal = portal };

        if (string.IsNullOrWhiteSpace(iqn) || string.IsNullOrWhiteSpace(portal))
        {
            result.Success = false;
            result.Message = "Target or portal is not available.";
            return result;
        }

        var before = (beforeSessions ?? await IscsiSessions.ObtenerVistaGlobal())
            .Where(s => s.Connected && SameTarget(s, iqn, portal))
            .GroupBy(s => s.LunId)
            .Select(g => g.First())
            .ToList();

        var rescan = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T \"{iqn}\" -p \"{portal}\" --rescan"
        );

        if (rescan.ExitCode != 0)
        {
            // Algunos portales con formato IPv6 o alias no coinciden con el
            // portal guardado en el nodo. En ese caso se reescanea la sesión
            // activa por su identificador de sysfs.
            var sessionId = FindSessionId(iqn);
            if (sessionId.HasValue)
            {
                rescan = ShellHelper.EjecutarComoRoot(
                    $"iscsiadm -m session -r {sessionId.Value} --rescan"
                );
            }
        }

        if (rescan.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(rescan.Stderr)
                ? rescan.Stdout.Trim()
                : rescan.Stderr.Trim();

            result.Success = false;
            result.Message = string.IsNullOrWhiteSpace(error)
                ? "open-iscsi rejected the rescan request."
                : error;

            LogService.Error($"[RESCAN] {iqn} ({portal}): {result.Message}");
            return result;
        }

        ShellHelper.EjecutarComoRoot("udevadm settle --timeout=10");
        await Task.Delay(800);

        // Refrescar la tabla de particiones de los dispositivos que ya
        // existían. En volúmenes montados puede fallar (EBUSY); no es un
        // error fatal porque el tamaño del bloque ya se actualizó.
        foreach (var lun in before)
            RefreshPartitionTable(lun);

        await Task.Delay(300);

        var after = (await IscsiSessions.ObtenerVistaGlobal())
            .Where(s => s.Connected && SameTarget(s, iqn, portal))
            .GroupBy(s => s.LunId)
            .Select(g => g.First())
            .ToDictionary(s => s.LunId, s => s);

        var beforeByLun = before.ToDictionary(s => s.LunId, s => s);

        foreach (var lun in after.Values.OrderBy(s => s.LunId))
        {
            if (!beforeByLun.TryGetValue(lun.LunId, out var previous))
            {
                result.Changes.Add(new LunRescanChange
                {
                    LunId = lun.LunId,
                    Kind = LunRescanKind.Added,
                    Device = lun.Device,
                    MountPoint = lun.MountPoint,
                    Filesystem = lun.Filesystem,
                    SizeBytes = EffectiveBytes(lun),
                    IsMounted = IsMounted(lun.MountPoint)
                });
                continue;
            }

            var previousBytes = EffectiveBytes(previous);
            var currentBytes = EffectiveBytes(lun);
            var kind = currentBytes > previousBytes
                ? LunRescanKind.Resized
                : LunRescanKind.Unchanged;

            var change = new LunRescanChange
            {
                LunId = lun.LunId,
                Kind = kind,
                Device = lun.Device,
                PreviousDevice = previous.Device,
                MountPoint = lun.MountPoint,
                Filesystem = lun.Filesystem,
                PreviousSizeBytes = previousBytes,
                SizeBytes = currentBytes,
                IsMounted = IsMounted(lun.MountPoint)
            };

            if (kind == LunRescanKind.Resized && expandFilesystems)
            {
                var (expanded, message) = TryExpandFilesystem(lun);
                change.FilesystemExpanded = expanded;
                change.FilesystemMessage = message;
            }

            result.Changes.Add(change);
        }

        foreach (var previous in beforeByLun.Values.OrderBy(s => s.LunId))
        {
            if (after.ContainsKey(previous.LunId))
                continue;

            result.Changes.Add(new LunRescanChange
            {
                LunId = previous.LunId,
                Kind = LunRescanKind.Removed,
                PreviousDevice = previous.Device,
                MountPoint = previous.MountPoint,
                Filesystem = previous.Filesystem,
                PreviousSizeBytes = EffectiveBytes(previous),
                IsMounted = IsMounted(previous.MountPoint)
            });
        }

        result.Changes.Sort((a, b) => a.LunId.CompareTo(b.LunId));

        LogService.Debug($"[RESCAN] {iqn} ({portal}): {result.Summary}");
        return result;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "-";

        if (bytes >= BytesPerGb * 1024)
            return $"{bytes / (BytesPerGb * 1024.0):0.##} TB";
        if (bytes >= BytesPerGb)
            return $"{bytes / (double)BytesPerGb:0.##} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.##} KB";

        return $"{bytes} B";
    }

    private static bool SameTarget(SessionInfo session, string iqn, string portal) =>
        session.Iqn.Equals(iqn, StringComparison.OrdinalIgnoreCase) &&
        session.Portal.Equals(portal, StringComparison.OrdinalIgnoreCase);

    private static long EffectiveBytes(SessionInfo session) =>
        session.SizeBytes > 0 ? session.SizeBytes : session.SizeGb * BytesPerGb;

    private static bool IsMounted(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            return false;

        var result = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{mountPoint}\"");
        return result.ExitCode == 0;
    }

    private static void RefreshPartitionTable(SessionInfo lun)
    {
        if (string.IsNullOrWhiteSpace(lun.Device))
            return;

        try
        {
            var typeResult = ShellHelper.EjecutarComoRoot(
                $"lsblk -no TYPE \"{lun.Device}\" 2>/dev/null"
            );

            if (typeResult.ExitCode != 0 ||
                !typeResult.Stdout.Trim().Equals("part", StringComparison.OrdinalIgnoreCase))
                return;

            var parentResult = ShellHelper.EjecutarComoRoot(
                $"lsblk -no PKNAME \"{lun.Device}\" 2>/dev/null"
            );

            var parent = parentResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();

            var target = string.IsNullOrWhiteSpace(parent)
                ? lun.Device
                : $"/dev/{parent}";

            ShellHelper.EjecutarComoRoot(
                $"partprobe \"{target}\" >/dev/null 2>&1 || blockdev --rereadpt \"{target}\" >/dev/null 2>&1"
            );
        }
        catch (Exception ex)
        {
            LogService.Debug($"[RESCAN] No se pudo refrescar la tabla de {lun.Device}: {ex.Message}");
        }
    }

    private static (bool Expanded, string Message) TryExpandFilesystem(SessionInfo lun)
    {
        if (string.IsNullOrWhiteSpace(lun.Device))
            return (false, "no block device");

        if (!IsMounted(lun.MountPoint))
            return (false, "volume is not mounted");

        var fs = (lun.Filesystem ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(fs))
            return (false, "filesystem type not detected");

        var command = fs switch
        {
            "ext2" or "ext3" or "ext4" => $"resize2fs \"{lun.Device}\"",
            "xfs" => $"xfs_growfs \"{lun.MountPoint}\"",
            "btrfs" => $"btrfs filesystem resize max \"{lun.MountPoint}\"",
            _ => null
        };

        if (command == null)
            return (false, $"filesystem {fs} must be expanded manually");

        var result = ShellHelper.EjecutarComoRoot(command);
        if (result.ExitCode == 0)
            return (true, "filesystem expanded online");

        var error = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();

        LogService.Error($"[RESCAN] No se pudo expandir {fs} en {lun.Device}: {error}");
        return (false, $"filesystem expansion failed: {error}");
    }

    private static int? FindSessionId(string iqn)
    {
        try
        {
            if (!Directory.Exists("/sys/class/iscsi_session"))
                return null;

            foreach (var directory in Directory.GetDirectories("/sys/class/iscsi_session"))
            {
                var targetNameFile = Path.Combine(directory, "targetname");
                if (!File.Exists(targetNameFile))
                    continue;

                if (!File.ReadAllText(targetNameFile).Trim()
                        .Equals(iqn, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sessionName = Path.GetFileName(directory);
                if (sessionName.StartsWith("session", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(sessionName["session".Length..], out var sessionId))
                    return sessionId;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"[RESCAN] No se pudo resolver la sesión de {iqn}: {ex.Message}");
        }

        return null;
    }
}
