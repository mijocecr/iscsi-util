using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiSessions
{
    // Ruta del archivo JSON para persistir la fecha de primera conexión
    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "ISCSI_Util", 
        "session_history.json"
    );

    private class SessionHistoryRecord
    {
        public string Iqn { get; set; } = string.Empty;
        public string Portal { get; set; } = string.Empty;
        public int LunId { get; set; }
        public DateTime FirstConnected { get; set; }
    }

    private static Dictionary<string, DateTime> CargarHistorialConexiones()
    {
        var diccionario = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(HistoryFilePath))
            {
                var json = File.ReadAllText(HistoryFilePath);
                var registros = JsonSerializer.Deserialize<List<SessionHistoryRecord>>(json);
                if (registros != null)
                {
                    foreach (var reg in registros)
                    {
                        var clave = $"{reg.Iqn}|{reg.Portal}|{reg.LunId}";
                        diccionario[clave] = reg.FirstConnected;
                    }
                }
            }
        }
        catch { }
        return diccionario;
    }

    private static void GuardarHistorialConexiones(Dictionary<string, DateTime> historial)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var lista = historial.Select(kvp =>
            {
                var partes = kvp.Key.Split('|');
                return new SessionHistoryRecord
                {
                    Iqn = partes[0],
                    Portal = partes.Length > 1 ? partes[1] : string.Empty,
                    LunId = partes.Length > 2 && int.TryParse(partes[2], out int lun) ? lun : 0,
                    FirstConnected = kvp.Value
                };
            }).ToList();

            var json = JsonSerializer.Serialize(lista, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryFilePath, json);
        }
        catch { }
    }

    // ============================================================
    // OBTENER SESIONES REALES
    // ============================================================

    public static async Task<List<SessionInfo>> ObtenerVistaGlobal()
    {
        var sesiones = new List<SessionInfo>();
        var historial = CargarHistorialConexiones();
        bool historialModificado = false;

        var byPath = (ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/").Stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var iscsiLinks = IscsiByPath.ParseAll(byPath).ToList();

        foreach (var link in iscsiLinks)
        {
            string portal = link.Portal;
            string iqn = link.Iqn;
            int lun = link.LunId;

            var mountPointCalculado = IscsiNaming.MountPoint(ConfigManager.MountBasePath, iqn, lun);

            // Gestionar la fecha de primera conexión de forma persistente
            var claveHistorial = $"{iqn}|{portal}|{lun}";
            DateTime connectedSince;

            if (historial.TryGetValue(claveHistorial, out var fechaExistente) && fechaExistente != default)
            {
                connectedSince = fechaExistente;
            }
            else
            {
                connectedSince = DateTime.Now;
                historial[claveHistorial] = connectedSince;
                historialModificado = true;
            }

            var info = new SessionInfo
            {
                Iqn = iqn,
                Portal = portal,
                LunId = lun,
                Connected = true,
                Device = "/dev/disk/by-path/" + link.Link,
                ConnectedSince = connectedSince,
                MountPoint = mountPointCalculado
            };

            // ============================================================
            // 1) Detect real partition (if any)
            // ============================================================

            var lsblkRaw = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {info.Device}").Stdout ?? "";
            var lsblkLines = lsblkRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var l in lsblkLines)
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && p[1] == "part")
                {
                    info.Device = "/dev/" + p[0];
                    break;
                }
            }

            // ============================================================
            // 2) Filesystem
            // ============================================================

            var blkidRaw = ShellHelper.EjecutarComoRoot($"blkid -p {info.Device}").Stdout ?? "";
            info.Filesystem = ExtraerFsType(blkidRaw);

            // ============================================================
            // 3) Mount runtime (Verificación real contra el OS)
            // ============================================================
            var mountPointReal = ObtenerPuntoMontajeReal(info.Device);
            if (!string.IsNullOrWhiteSpace(mountPointReal))
                info.MountPoint = mountPointReal;

            // ============================================================
            // 4) Vendor / Model / Size
            // ============================================================

            var sizeBytesResult = ShellHelper.EjecutarComoRoot(
                $"blockdev --getsize64 \"{info.Device}\" 2>/dev/null"
            );

            if (sizeBytesResult.ExitCode == 0 &&
                long.TryParse(sizeBytesResult.Stdout.Trim(), out var sizeBytes) &&
                sizeBytes > 0)
            {
                info.SizeBytes = sizeBytes;
                info.SizeGb = (int)Math.Round(sizeBytes / 1024.0 / 1024.0 / 1024.0);
            }
            else
            {
                var blkRaw = ShellHelper.EjecutarComoRoot($"lsblk -o SIZE {info.Device}").Stdout ?? "";
                var blkLines = blkRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                string size = "-";

                if (blkLines.Length >= 2)
                    size = blkLines[1].Trim();

                info.SizeGb = ExtraerSizeGb(size);
                info.SizeBytes = info.SizeGb * 1024L * 1024L * 1024L;
            }

            info.Vendor = "-";
            info.Model  = "-";

            // ============================================================
            // 5) Auth
            // ============================================================

            info.Auth = ExtraerAuthDesdeNode(iqn, portal);

            sesiones.Add(info);
        }

        if (historialModificado)
        {
            GuardarHistorialConexiones(historial);
        }

        var configuredNodes = ShellHelper.EjecutarComoRoot("iscsiadm -m node").Stdout ?? "";
        foreach (var line in configuredNodes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0 || separator == line.Length - 1)
                continue;

            var portal = line[..separator].Trim();
            var iqn = line[(separator + 1)..].Trim();
            var comma = portal.LastIndexOf(',');
            if (comma >= 0)
                portal = portal[..comma];

            if (string.IsNullOrWhiteSpace(iqn) || string.IsNullOrWhiteSpace(portal) ||
                sesiones.Any(x => x.Iqn.Equals(iqn, StringComparison.OrdinalIgnoreCase) &&
                                  x.Portal.Equals(portal, StringComparison.OrdinalIgnoreCase)))
                continue;

            sesiones.Add(new SessionInfo
            {
                Iqn = iqn,
                Portal = portal,
                Connected = false,
                Auth = ExtraerAuthDesdeNode(iqn, portal),
                // Un nodo configurado pero desconectado se muestra a nivel de
                // target (LUN 0) hasta que vuelva a exponer sus LUNs.
                MountPoint = IscsiNaming.MountPoint(ConfigManager.MountBasePath, iqn, 0)
            });
        }

        return sesiones;
    }

    // ============================================================
    // JERARQUÍA Target -> LUN -> DISPOSITIVO
    // ============================================================

    public static List<IscsiTargetNode> AgruparPorTarget(IEnumerable<SessionInfo> sesiones)
    {
        var targets = new List<IscsiTargetNode>();
        var index = new Dictionary<string, IscsiTargetNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sesiones)
        {
            var key = $"{session.Iqn}|{session.Portal}";

            if (!index.TryGetValue(key, out var target))
            {
                target = new IscsiTargetNode
                {
                    Iqn = session.Iqn,
                    Portal = session.Portal
                };

                index[key] = target;
                targets.Add(target);
            }

            target.Luns.Add(new IscsiLunNode
            {
                LunId = session.LunId,
                Session = session
            });
        }

        foreach (var target in targets)
            target.Luns.Sort((a, b) => a.LunId.CompareTo(b.LunId));

        return targets;
    }

    // ============================================================
    // MOUNTPOINT REAL EN RUNTIME
    // ============================================================

    private static string ObtenerPuntoMontajeReal(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return string.Empty;

        var result = ShellHelper.EjecutarComoRoot(
            $"findmnt -rn -S \"{device}\" -o TARGET"
        );

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return string.Empty;

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim() ?? string.Empty;
    }

    public static string ExtraerAuthDesdeNode(string iqn, string portal)
    {
        var raw = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {iqn} -p {portal} --op show"
        ).Stdout;

        if (string.IsNullOrWhiteSpace(raw))
            return "None";

        return ExtraerAuth(raw);
    }

    private static string ExtraerFsType(string blkidOut)
    {
        if (string.IsNullOrWhiteSpace(blkidOut))
            return "";

        foreach (var part in blkidOut.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase))
            {
                return part.Replace("TYPE=", "")
                           .Trim()
                           .Trim('"');
            }
        }

        return "";
    }

    private static int ExtraerSizeGb(string sizeRaw)
    {
        if (string.IsNullOrWhiteSpace(sizeRaw))
            return 0;

        sizeRaw = sizeRaw.Trim().ToUpper();

        try
        {
            if (sizeRaw.EndsWith("G"))
            {
                var num = sizeRaw.Replace("G", "");
                if (double.TryParse(num, out double g))
                    return (int)Math.Round(g);
            }

            if (sizeRaw.EndsWith("M"))
            {
                var num = sizeRaw.Replace("M", "");
                if (double.TryParse(num, out double m))
                    return (int)Math.Round(m / 1024.0);
            }

            if (sizeRaw.EndsWith("T"))
            {
                var num = sizeRaw.Replace("T", "");
                if (double.TryParse(num, out double t))
                    return (int)Math.Round(t * 1024.0);
            }
        }
        catch { }

        return 0;
    }

    private static string ExtraerAuth(string nodeShowOut)
    {
        if (string.IsNullOrWhiteSpace(nodeShowOut))
            return "None";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in nodeShowOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }

        if (!values.TryGetValue("node.session.auth.authmethod", out var method) ||
            !method.Equals("CHAP", StringComparison.OrdinalIgnoreCase))
            return "None";

        static bool Configured(Dictionary<string, string> config, string key) =>
            config.TryGetValue(key, out var value) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("<empty>", StringComparison.OrdinalIgnoreCase);

        return Configured(values, "node.session.auth.username_in") &&
               Configured(values, "node.session.auth.password_in")
            ? "Mutual CHAP"
            : "CHAP";
    }
}