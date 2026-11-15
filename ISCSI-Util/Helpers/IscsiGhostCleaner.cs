using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

/// <summary>
/// Detección y limpieza de "targets fantasma": registros del nodo iSCSI que
/// siguen en la base de datos local (/var/lib/iscsi/nodes) pero que el portal
/// ya no exporta, además de sesiones activas que ya no tienen registro.
///
/// Reglas de seguridad:
/// - Solo se consideran fantasmas los targets de un portal que respondió al
///   descubrimiento y que ya no aparecen en su respuesta. Si el portal no
///   responde, sus nodos se ofrecen aparte y nunca se borran por defecto.
/// - Nunca se toca un target con algún LUN montado.
/// - Antes de borrar un nodo se cierra su sesión (logout).
/// </summary>
public static class IscsiGhostCleaner
{
    public sealed class GhostTarget
    {
        public string Iqn { get; init; } = "";
        public string Portal { get; init; } = "";
        public bool Connected { get; init; }
        public bool Mounted { get; init; }
        public bool PortalUnreachable { get; init; }
    }

    public sealed class ScanResult
    {
        /// <summary>Registros que el portal ya no exporta (borrado seguro).</summary>
        public List<GhostTarget> Confirmed { get; } = new();

        /// <summary>Registros de portales que no respondieron (borrado opcional).</summary>
        public List<GhostTarget> Unreachable { get; } = new();

        public List<string> UnreachablePortals { get; } = new();
        public int NodeCount { get; init; }
        public int SessionCount { get; init; }

        public bool HasAny => Confirmed.Count > 0 || Unreachable.Count > 0;
    }

    public sealed record Failure(GhostTarget Target, string Error);

    public sealed class CleanResult
    {
        public List<GhostTarget> Removed { get; } = new();
        public List<GhostTarget> SkippedMounted { get; } = new();
        public List<Failure> Failed { get; } = new();
    }

    public static Task<ScanResult> ScanAsync(IProgress<string>? progress = null)
        => Task.Run(() => Scan(progress));

    public static Task<CleanResult> CleanAsync(
        IReadOnlyCollection<GhostTarget> targets,
        IProgress<string>? progress = null)
        => Task.Run(() => Clean(targets, progress));

    // ============================================================
    // ESCANEO
    // ============================================================

    private static ScanResult Scan(IProgress<string>? progress)
    {
        EnsureDaemonRunning();

        var nodes = ReadNodes();
        var sessions = ReadSessions();
        var links = ReadByPathLinks();

        var result = new ScanResult
        {
            NodeCount = nodes.Count,
            SessionCount = sessions.Count
        };

        bool IsConnected(string iqn, string portal) =>
            sessions.Any(s => SameIqn(s.Iqn, iqn) && SamePortal(s.Portal, portal));

        var portals = nodes
            .Select(n => n.Portal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int portalIndex = 0;

        foreach (var portal in portals)
        {
            portalIndex++;
            progress?.Report($"Checking portal {portal} ({portalIndex}/{portals.Count})…");

            var group = nodes
                .Where(n => SamePortal(n.Portal, portal))
                .ToList();

            // "-o new": solo crea registros que falten. Un discovery normal
            // reescribiría los nodos existentes y borraría su CHAP.
            var discovery = RunRoot(
                $"iscsiadm -m discovery -t sendtargets -p {ShellHelper.QuoteArg(portal)} -o new",
                TimeSpan.FromSeconds(15));

            bool reachable = discovery.ExitCode == 0;

            if (!reachable)
            {
                if (!result.UnreachablePortals.Contains(portal, StringComparer.OrdinalIgnoreCase))
                    result.UnreachablePortals.Add(portal);

                foreach (var node in group)
                {
                    result.Unreachable.Add(new GhostTarget
                    {
                        Iqn = node.Iqn,
                        Portal = node.Portal,
                        Connected = IsConnected(node.Iqn, node.Portal),
                        Mounted = IsMounted(node.Iqn, node.Portal, links),
                        PortalUnreachable = true
                    });
                }

                continue;
            }

            var exported = ParseDiscoveryIqns(discovery.Stdout);

            foreach (var node in group)
            {
                if (exported.Contains(node.Iqn))
                    continue;

                result.Confirmed.Add(new GhostTarget
                {
                    Iqn = node.Iqn,
                    Portal = node.Portal,
                    Connected = IsConnected(node.Iqn, node.Portal),
                    Mounted = IsMounted(node.Iqn, node.Portal, links)
                });
            }
        }

        progress?.Report("Checking sessions without a node record…");

        // Sesiones activas que ya no tienen registro de nodo asociado.
        foreach (var session in sessions)
        {
            bool hasNode = nodes.Any(n =>
                SameIqn(n.Iqn, session.Iqn) && SamePortal(n.Portal, session.Portal));

            if (hasNode)
                continue;

            if (result.Confirmed.Any(g =>
                    SameIqn(g.Iqn, session.Iqn) && SamePortal(g.Portal, session.Portal)))
                continue;

            result.Confirmed.Add(new GhostTarget
            {
                Iqn = session.Iqn,
                Portal = session.Portal,
                Connected = true,
                Mounted = IsMounted(session.Iqn, session.Portal, links)
            });
        }

        Sort(result.Confirmed);
        Sort(result.Unreachable);

        progress?.Report(
            $"Scan finished: {result.Confirmed.Count} confirmed ghost(s), " +
            $"{result.Unreachable.Count} record(s) on unreachable portals.");

        return result;
    }

    // ============================================================
    // LIMPIEZA
    // ============================================================

    private static CleanResult Clean(
        IReadOnlyCollection<GhostTarget> targets,
        IProgress<string>? progress)
    {
        EnsureDaemonRunning();

        var result = new CleanResult();
        var sessions = ReadSessions();
        var links = ReadByPathLinks();
        int index = 0;

        foreach (var target in targets)
        {
            index++;
            progress?.Report(
                $"Removing {target.Iqn} from {target.Portal} ({index}/{targets.Count})…");

            // Revalidar el montaje en el momento de borrar: entre el escaneo y
            // la confirmación el usuario pudo montar el volumen.
            if (IsMounted(target.Iqn, target.Portal, links))
            {
                result.SkippedMounted.Add(target);
                LogService.Write(
                    $"[GHOST] Skip {target.Iqn} at {target.Portal}: still mounted.");
                continue;
            }

            var session = sessions.FirstOrDefault(s =>
                SameIqn(s.Iqn, target.Iqn) && SamePortal(s.Portal, target.Portal));

            string logoutError = "";

            if (session != null)
            {
                var logout = RunRoot(
                    $"iscsiadm -m session -r {session.Sid} --logout",
                    TimeSpan.FromSeconds(10));

                if (logout.ExitCode != 0)
                {
                    var nodeLogout = RunRoot(
                        $"iscsiadm -m node -T {ShellHelper.QuoteArg(target.Iqn)} " +
                        $"-p {ShellHelper.QuoteArg(target.Portal)} --logout",
                        TimeSpan.FromSeconds(10));

                    if (nodeLogout.ExitCode != 0)
                        logoutError = FirstLine(nodeLogout.Stderr);
                }
            }

            var delete = RunRoot(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(target.Iqn)} " +
                $"-p {ShellHelper.QuoteArg(target.Portal)} --op=delete",
                TimeSpan.FromSeconds(15));

            bool nodeGone = NodeRecordMissing(target.Iqn, target.Portal);

            if (nodeGone && string.IsNullOrWhiteSpace(logoutError))
            {
                result.Removed.Add(target);
                IscsiSessions.ForgetHistory(target.Iqn, target.Portal);

                LogService.Write(
                    $"[GHOST] Removed {target.Iqn} at {target.Portal}" +
                    (session != null ? $" (session {session.Sid} closed)" : ""));
                continue;
            }

            if (nodeGone)
            {
                // El registro desapareció aunque el logout falló: se considera
                // limpio, pero se deja constancia del error de sesión.
                result.Removed.Add(target);
                IscsiSessions.ForgetHistory(target.Iqn, target.Portal);
                LogService.Write(
                    $"[GHOST] Removed node {target.Iqn} at {target.Portal}; logout reported: {logoutError}");
                continue;
            }

            var error = FirstLine(delete.Stderr);
            if (string.IsNullOrWhiteSpace(error))
                error = "The node record is still present after deletion.";

            result.Failed.Add(new Failure(target, error));
            LogService.Error(
                $"[GHOST] Failed to remove {target.Iqn} at {target.Portal}: {error}");
        }

        progress?.Report(
            $"Cleanup finished: {result.Removed.Count} removed, " +
            $"{result.SkippedMounted.Count} skipped, {result.Failed.Count} failed.");

        return result;
    }

    // ============================================================
    // LECTURA DEL ESTADO
    // ============================================================

    private sealed record NodeRecord(string Iqn, string Portal);
    private sealed record SessionRecord(int Sid, string Iqn, string Portal);

    private static readonly Regex SessionPattern = new(
        @"^tcp:\s*\[(?<sid>\d+)\]\s+(?<portal>\S+)\s+(?<iqn>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<NodeRecord> ReadNodes()
    {
        var raw = ShellHelper.EjecutarComoRoot("iscsiadm -m node").Stdout ?? "";
        var list = new List<NodeRecord>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("iscsiadm:", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = trimmed.IndexOf(' ');
            if (separator <= 0 || separator == trimmed.Length - 1)
                continue;

            var portal = NormalizePortal(trimmed[..separator]);
            var iqn = trimmed[(separator + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";

            if (portal.Length > 0 && iqn.StartsWith("iqn.", StringComparison.OrdinalIgnoreCase))
                list.Add(new NodeRecord(iqn, portal));
        }

        return list;
    }

    private static List<SessionRecord> ReadSessions()
    {
        var raw = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout ?? "";
        var list = new List<SessionRecord>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = SessionPattern.Match(line.Trim());
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["sid"].Value, out int sid))
                continue;

            list.Add(new SessionRecord(
                sid,
                match.Groups["iqn"].Value,
                NormalizePortal(match.Groups["portal"].Value)));
        }

        return list;
    }

    private static HashSet<string> ParseDiscoveryIqns(string stdout)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var iqn = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(token => token.StartsWith("iqn.", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(iqn))
                set.Add(iqn);
        }

        return set;
    }

    private static List<IscsiByPath.Entry> ReadByPathLinks()
    {
        var raw = ShellHelper.RunClean("ls -1 /dev/disk/by-path/").Stdout ?? "";
        return IscsiByPath.ParseAll(
            raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();
    }

    // ============================================================
    // UTILIDADES
    // ============================================================

    private static (int ExitCode, string Stdout, string Stderr) RunRoot(
        string command,
        TimeSpan timeout)
        => ShellHelper.EjecutarComoRootAsync(command, timeout).GetAwaiter().GetResult();

    private static void EnsureDaemonRunning()
    {
        var status = ShellHelper.RunClean("systemctl is-active --quiet iscsid");
        if (status.ExitCode != 0)
            throw new InvalidOperationException(
                "The iSCSI daemon (iscsid) is not running. Start it before scanning or removing ghost targets.");
    }

    private static bool NodeRecordMissing(string iqn, string portal)
    {
        var check = RunRoot(
            $"iscsiadm -m node -T {ShellHelper.QuoteArg(iqn)} -p {ShellHelper.QuoteArg(portal)}",
            TimeSpan.FromSeconds(10));

        return check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(check.Stdout);
    }

    private static bool IsMounted(
        string iqn,
        string portal,
        IReadOnlyList<IscsiByPath.Entry> links)
    {
        foreach (var link in links)
        {
            if (!SameIqn(link.Iqn, iqn) || !SamePortal(link.Portal, portal))
                continue;

            var device = "/dev/disk/by-path/" + link.Link;
            var mount = ShellHelper.RunClean(
                $"findmnt -rn -S {ShellHelper.QuoteArg(device)} -o TARGET");

            if (mount.ExitCode == 0 && !string.IsNullOrWhiteSpace(mount.Stdout))
                return true;
        }

        return false;
    }

    private static bool SameIqn(string a, string b)
        => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePortal(string a, string b)
        => NormalizePortal(a).Equals(NormalizePortal(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Quita el portal group tag: "ip:3260,1" → "ip:3260".</summary>
    private static string NormalizePortal(string portal)
    {
        portal = portal.Trim();
        int comma = portal.LastIndexOf(',');
        return comma > 0 ? portal[..comma] : portal;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
    }

    private static void Sort(List<GhostTarget> targets)
    {
        targets.Sort((a, b) =>
        {
            int portal = string.Compare(a.Portal, b.Portal, StringComparison.OrdinalIgnoreCase);
            return portal != 0
                ? portal
                : string.Compare(a.Iqn, b.Iqn, StringComparison.OrdinalIgnoreCase);
        });
    }
}
