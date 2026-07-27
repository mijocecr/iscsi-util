using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiSessions
{
    // ============================================================
    // OBTENER SESIONES REALES
    // ============================================================

    
public static async Task<List<SessionInfo>> ObtenerVistaGlobal()
{
    var sesiones = new List<SessionInfo>();

    var byPath = ShellHelper.EjecutarComoRoot("ls -1 /dev/disk/by-path/")
                            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ============================================================
    // FILTRO CORREGIDO: solo discos, NO particiones
    // ============================================================
    var iscsiLinks = byPath
        .Where(p => p.Contains("iscsi") && !p.Contains("part"))
        .ToList();

    if (iscsiLinks.Count == 0)
        return sesiones;

    foreach (var link in iscsiLinks)
    {
        string portal = ExtraerPortal(link);
        string iqn = ExtraerIqn(link);
        int lun = ExtraerLunId(link);

        var info = new SessionInfo
        {
            Iqn = iqn,
            Portal = portal,
            LunId = lun,
            Connected = true,
            Device = "/dev/disk/by-path/" + link.Trim(),
            ConnectedSince = DateTime.Now
        };

        // ============================================================
        // 1) Detect real partition (if any)
        // ============================================================

        var lsblkRaw = ShellHelper.EjecutarComoRoot($"lsblk -rno NAME,TYPE {info.Device}").Stdout;
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

        var blkidRaw = ShellHelper.EjecutarComoRoot($"blkid -p {info.Device}").Stdout;
        info.Filesystem = ExtraerFsType(blkidRaw);

        // ============================================================
        // 3) Mount runtime
        // ============================================================

        var mountsRaw = ShellHelper.EjecutarComoRoot("mount").Stdout;
        var mounts = mountsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var m in mounts)
        {
            if (m.StartsWith(info.Device + " "))
            {
                info.MountPoint = m.Split(' ')[2];
                break;
            }
        }

        // ============================================================
        // 4) Vendor / Model / Size (iSCSI has no vendor/model)
        // ============================================================

        var blkRaw = ShellHelper.EjecutarComoRoot($"lsblk -o SIZE {info.Device}").Stdout;
        var blkLines = blkRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string size = "-";

        if (blkLines.Length >= 2)
            size = blkLines[1].Trim();

        info.Vendor = "-";
        info.Model  = "-";
        info.SizeGb = ExtraerSizeGb(size);

        // ============================================================
        // 5) Auth
        // ============================================================

        info.Auth = ExtraerAuthDesdeNode(iqn, portal);

        sesiones.Add(info);
    }

    return sesiones;
}





    // ============================================================
    // HELPERS
    
    private static string ExtraerNombreDestino(string link)
    {
        // Ejemplo:
        // ip-192.168.1.50:3260-iscsi-iqn.2013-03.com.wdc.mycloudex2ultra.mjcc-lun-0

        int start = link.IndexOf("iscsi-iqn.");
        if (start < 0) return "";

        string sub = link.Substring(start + "iscsi-".Length);

        int lunIndex = sub.IndexOf("-lun");
        if (lunIndex > 0)
            sub = sub.Substring(0, lunIndex);

        return sub.Trim();
    }

    
    
    private static string ExtraerPortal(string link)
    {
        var parts = link.Split('-');
        foreach (var p in parts)
        {
            if (p.Contains(":3260"))
                return p.Replace("ip", "").Replace("-", "").Trim();
        }
        return "";
    }


    private static string ExtraerIqn(string link)
    {
        // Buscar "iscsi-" y tomar lo que viene después hasta "-lun"
        int start = link.IndexOf("iscsi-");
        if (start < 0) return "";

        string sub = link.Substring(start + "iscsi-".Length);

        int lunIndex = sub.IndexOf("-lun");
        if (lunIndex > 0)
            sub = sub.Substring(0, lunIndex);

        return sub.Trim();
    }


    private static int ExtraerLunId(string link)
    {
        // ...-lun-0
        try
        {
            var parts = link.Split('-');
            foreach (var p in parts)
            {
                if (p.StartsWith("lun"))
                {
                    var num = p.Replace("lun", "").Replace("-", "");
                    if (int.TryParse(num, out int lun))
                        return lun;
                }
            }
        }
        catch { }
        return 0;
    }

    
    
    // ============================================================
    
    public static string ExtraerAuthDesdeNode(string iqn, string portal)
    {
        // Obtener la configuración del nodo
        var raw = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {iqn} -p {portal} --op show"
        ).Stdout;

        if (string.IsNullOrWhiteSpace(raw))
            return "None";

        // Detectar método de autenticación
        if (raw.Contains("node.session.auth.authmethod = CHAP"))
        {
            // Mutual CHAP si reverse credentials existen
            bool hasReverse = raw.Contains("node.session.auth.username_in") ||
                              raw.Contains("node.session.auth.password_in");

            return hasReverse ? "Mutual CHAP" : "CHAP";
        }

        return "None";
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

        bool chap = false;
        bool mutual = false;

        foreach (var line in nodeShowOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = line.Trim();

            if (l.Contains("authmethod") && l.Contains("CHAP"))
                chap = true;

            if (l.Contains("username_in") || l.Contains("password_in"))
                mutual = true;
        }

        if (mutual) return "Mutual CHAP";
        if (chap) return "CHAP";
        return "None";
    }
}
