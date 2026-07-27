using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class IscsiSessions
{
    private static long _opId = 0;
    private static long NextId() => ++_opId;

    // ============================================================
    // PARSE PORTAL DE FORMA ROBUSTA
    // ============================================================

    private static string ParsePortal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        // Eliminar coma final si existe
        var p = raw.Split(',')[0];

        // Asegurar puerto
        if (!p.Contains(":"))
            p += ":3260";

        return p.Trim();
    }

    // ============================================================
    // SESIONES ACTIVAS
    // ============================================================

    private static List<IscsiDestino> ObtenerSesionesActivas()
    {
        var list = new List<IscsiDestino>();

        var result = ShellHelper.EjecutarComoRoot("iscsiadm -m session");

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return list;

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("iqn.")) continue;

            // Ejemplos válidos:
            // tcp: [1] 192.168.1.50:3260,1 iqn.2013-03.com.wdc:disk
            // tcp: [1] 192.168.1.50:3260 iqn.2013-03.com.wdc:disk

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string portal = parts.FirstOrDefault(p => p.Contains(":"));
            string iqn = parts.LastOrDefault(p => p.StartsWith("iqn."));

            if (portal == null || iqn == null)
                continue;

            portal = portal.Split(',')[0]; // limpia coma si existe

            list.Add(new IscsiDestino
            {
                Ip = portal,
                Iqn = iqn,
                Conectado = true
            });
        }

        return list;
    }


    // ============================================================
    // NODOS CONFIGURADOS
    // ============================================================

    private static List<IscsiDestino> ObtenerNodos()
    {
        var list = new List<IscsiDestino>();

        try
        {
            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m node");

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                return list;

            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                string portal = ParsePortal(parts[0]);
                string iqn = parts[1];

                list.Add(new IscsiDestino
                {
                    Ip = portal,
                    Iqn = iqn,
                    Conectado = false
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"ObtenerNodos: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    // DISCOVERYDB
    // ============================================================

    private static List<IscsiDestino> ObtenerDiscoveryDb()
    {
        var list = new List<IscsiDestino>();

        try
        {
            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m discoverydb -t sendtargets -o show");

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                return list;

            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                string portal = ParsePortal(parts[0]);
                string iqn = parts[1];

                list.Add(new IscsiDestino
                {
                    Ip = portal,
                    Iqn = iqn,
                    Conectado = false
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"ObtenerDiscoveryDb: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    // FUSIÓN MODERNA
    // ============================================================

    private static List<IscsiDestino> Fusionar(
        List<IscsiDestino> sesiones,
        List<IscsiDestino> nodos,
        List<IscsiDestino> discoverydb)
    {
        var todos = sesiones.Concat(nodos).Concat(discoverydb).ToList();

        var grupos = todos.GroupBy(x => $"{x.Iqn}|{ParsePortal(x.Ip)}");

        var final = new List<IscsiDestino>();

        foreach (var g in grupos)
        {
            var lista = g.ToList();

            var activo = lista.FirstOrDefault(x => x.Conectado);
            if (activo != null)
            {
                final.Add(activo);
                continue;
            }

            final.Add(lista.First());
        }

        return final
            .OrderByDescending(x => x.Conectado)
            .ThenBy(x => x.Iqn)
            .ToList();
    }

    // ============================================================
    // COMPLETAR INFO (solo conectados)
    // ============================================================

    private static async Task CompletarInfo(List<IscsiDestino> destinos)
    {
        var conectados = destinos.Where(x => x.Conectado).ToList();

        foreach (var d in conectados)
        {
            try
            {
                await IscsiHelper.CompletarInformacionDestino(d, 0);
            }
            catch (Exception ex)
            {
                LogService.Error($"CompletarInfo error en {d.Iqn}: {ex.Message}");
            }
        }
    }

    // ============================================================
    // VISTA GLOBAL FINAL
    // ============================================================

    public static async Task<List<IscsiDestino>> ObtenerVistaGlobal()
    {
        try
        {
            var sesiones = ObtenerSesionesActivas();
            var nodos = ObtenerNodos();
            var discoverydb = ObtenerDiscoveryDb();

            var fusion = Fusionar(sesiones, nodos, discoverydb);

            await CompletarInfo(fusion);

            return fusion;
        }
        catch (Exception ex)
        {
            LogService.Error($"ObtenerVistaGlobal error: {ex.Message}");
            return new List<IscsiDestino>();
        }
    }
}
