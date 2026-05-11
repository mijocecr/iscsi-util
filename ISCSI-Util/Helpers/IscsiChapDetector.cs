using System;
using System.Linq;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers;

public static class IscsiChapDetector
{
    public class ChapResult
    {
        public bool RequiresChap { get; set; }
        public bool RequiresMutualChap { get; set; }
        public bool HasLocalChapConfigured { get; set; }
        public bool HasLocalMutualConfigured { get; set; }
        public string LocalUser { get; set; } = "";
        public string LocalPass { get; set; } = "";
        public string LocalUserIn { get; set; } = "";
        public string LocalPassIn { get; set; } = "";
    }

    // ============================================================
    // 1) Leer configuración local del nodo (si existe)
    // ============================================================
    private static void ReadLocalNodeConfig(IscsiDestino d, ChapResult r)
    {
        var show = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 -o show"
        );

        if (string.IsNullOrWhiteSpace(show.Stdout))
            return;

        string config = show.Stdout;

        string authMethod = Extract(config, "node.session.auth.authmethod");
        string user       = Extract(config, "node.session.auth.username");
        string pass       = Extract(config, "node.session.auth.password");
        string userIn     = Extract(config, "node.session.auth.username_in");
        string passIn     = Extract(config, "node.session.auth.password_in");

        bool chapEnabled = authMethod.Equals("CHAP", StringComparison.OrdinalIgnoreCase);

        bool userEmpty   = string.IsNullOrWhiteSpace(user)   || user   == "<empty>";
        bool passEmpty   = string.IsNullOrWhiteSpace(pass)   || pass   == "<empty>";
        bool userInEmpty = string.IsNullOrWhiteSpace(userIn) || userIn == "<empty>";
        bool passInEmpty = string.IsNullOrWhiteSpace(passIn) || passIn == "<empty>";

        r.HasLocalChapConfigured   = chapEnabled && !userEmpty && !passEmpty;
        r.HasLocalMutualConfigured = chapEnabled && !userInEmpty && !passInEmpty;

        r.LocalUser   = userEmpty   ? "" : user;
        r.LocalPass   = passEmpty   ? "" : pass;
        r.LocalUserIn = userInEmpty ? "" : userIn;
        r.LocalPassIn = passInEmpty ? "" : passIn;
    }

    private static string Extract(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = t.Split('=', 2);
            if (parts.Length == 2)
                return parts[1].Trim();
        }
        return "";
    }

    // ============================================================
    // 2) Intentar login de prueba (sin montar, sin symlink)
    // ============================================================
    private static void ProbeLogin(IscsiDestino d, ChapResult r)
    {
        // Creamos nodo temporal si no existe
        var check = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260"
        );

        bool exists = !check.Stderr.Contains("No records found", StringComparison.OrdinalIgnoreCase);

        if (!exists)
        {
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=new"
            );
        }

        // Intento de login
        var login = ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --login"
        );

        // Logout inmediato si entró
        if (login.ExitCode == 0)
        {
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --logout"
            );
            return;
        }

        string err = login.Stderr.ToLowerInvariant();

        if (err.Contains("authorization failure"))
            r.RequiresChap = true;

        if (err.Contains("incoming authentication") ||
            err.Contains("mutual") ||
            err.Contains("reverse"))
            r.RequiresMutualChap = true;

        // Borrar nodo temporal si lo creamos
        if (!exists)
        {
            ShellHelper.EjecutarComoRoot(
                $"iscsiadm -m node -T {d.Iqn} -p {d.Ip}:3260 --op=delete"
            );
        }
    }

    // ============================================================
    // 3) API principal
    // ============================================================
    public static ChapResult Detect(IscsiDestino d)
    {
        var r = new ChapResult();

        // 1) Leer configuración local
        ReadLocalNodeConfig(d, r);

        // 2) Intentar login de prueba para saber si el servidor exige CHAP
        ProbeLogin(d, r);

        return r;
    }
}
