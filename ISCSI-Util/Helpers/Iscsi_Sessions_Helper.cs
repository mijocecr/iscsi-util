using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class Iscsi_Sessions_Helper
{
    private static long _opId = 0;
    private static long NextId() => ++_opId;

    // ============================================================
    //   HELPERS DE RED
    // ============================================================

    private static string GetLocalIPv4()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault() ?? "0.0.0.0";
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] GetLocalIPv4: {ex.Message}");
            return "0.0.0.0";
        }
    }

    private static bool MismaSubred(string ip1, string ip2)
    {
        try
        {
            var a = ip1.Split('.');
            var b = ip2.Split('.');
            return a.Length == 4 && b.Length == 4 &&
                   a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    //   1) SESIONES ACTIVAS
    // ============================================================
    private static List<IscsiDestino> ObtenerSesionesActivas()
    {
        var list = new List<IscsiDestino>();

        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            LogService.Debug($"[SESSIONS_HELPER] #{id} → SesionesActivas()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("iqn.")) continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    string portal = parts[2].Split(',')[0];
                    string ip = portal.Split(':')[0];
                    string iqn = parts[3];

                    list.Add(new IscsiDestino
                    {
                        Ip = ip,
                        Iqn = iqn,
                        Conectado = true
                    });
                }
            }

            sw.Stop();
            LogService.Debug($"[SESSIONS_HELPER] #{id} ← SesionesActivas={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] ObtenerSesionesActivas: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    //   2) NODOS CONFIGURADOS
    // ============================================================
    private static List<IscsiDestino> ObtenerNodos()
    {
        var list = new List<IscsiDestino>();

        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            LogService.Debug($"[SESSIONS_HELPER] #{id} → Nodos()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m node");

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("iqn.")) continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string portal = parts[0].Split(',')[0];
                    string ip = portal.Split(':')[0];
                    string iqn = parts[1];

                    list.Add(new IscsiDestino
                    {
                        Ip = ip,
                        Iqn = iqn,
                        Conectado = false
                    });
                }
            }

            sw.Stop();
            LogService.Debug($"[SESSIONS_HELPER] #{id} ← Nodos={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] ObtenerNodos: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    //   3) DISCOVERYDB
    // ============================================================
    private static List<IscsiDestino> ObtenerDiscoveryDb()
    {
        var list = new List<IscsiDestino>();

        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            LogService.Debug($"[SESSIONS_HELPER] #{id} → DiscoveryDB()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m discoverydb -t sendtargets -o show");

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("iqn.")) continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string portal = parts[0].Split(',')[0];
                    string ip = portal.Split(':')[0];
                    string iqn = parts[1];

                    list.Add(new IscsiDestino
                    {
                        Ip = ip,
                        Iqn = iqn,
                        Conectado = false
                    });
                }
            }

            sw.Stop();
            LogService.Debug($"[SESSIONS_HELPER] #{id} ← DiscoveryDB={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] ObtenerDiscoveryDb: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    //   4) FUSIÓN INTELIGENTE
    // ============================================================
    private static List<IscsiDestino> Fusionar(
        List<IscsiDestino> sesiones,
        List<IscsiDestino> nodos,
        List<IscsiDestino> discoverydb)
    {
        try
        {
            var todos = new List<IscsiDestino>();
            todos.AddRange(sesiones);
            todos.AddRange(nodos);
            todos.AddRange(discoverydb);

            var grupos = todos.GroupBy(x => $"{x.Iqn}|{x.Ip}");

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

                var nodo = lista.FirstOrDefault(x => !x.Conectado);
                if (nodo != null)
                {
                    final.Add(nodo);
                    continue;
                }

                final.Add(lista.First());
            }

            return final
                .OrderByDescending(x => x.Conectado)
                .ThenBy(x => x.Iqn)
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] Fusionar: {ex.Message}");
            return new List<IscsiDestino>();
        }
    }

    // ============================================================
    //   5) COMPLETAR INFO SOLO PARA CONECTADOS
    // ============================================================
    private static async Task CompletarInfo(List<IscsiDestino> destinos)
    {
        long id = NextId();
        var sw = Stopwatch.StartNew();

        try
        {
            var conectados = destinos.Where(x => x.Conectado).ToList();

            LogService.Debug($"[SESSIONS_HELPER] #{id} → CompletarInfo() conectados={conectados.Count}");

            foreach (var d in conectados)
            {
                try
                {
                    await IscsiHelper.CompletarInformacionDestino(d, id);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[SESSIONS_HELPER] #{id} ERROR completando {d.Iqn}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] CompletarInfo: {ex.Message}");
        }

        sw.Stop();
        LogService.Debug($"[SESSIONS_HELPER] #{id} ← CompletarInfo en {sw.ElapsedMilliseconds} ms");
    }

    // ============================================================
    //   6) VISTA GLOBAL FINAL
    // ============================================================
    public static async Task<List<IscsiDestino>> ObtenerVistaGlobal()
    {
        long id = NextId();
        var sw = Stopwatch.StartNew();

        try
        {
            LogService.Debug($"[SESSIONS_HELPER] #{id} → VistaGlobal()");

            var sesiones = ObtenerSesionesActivas();
            var nodos = ObtenerNodos();
            var discoverydb = ObtenerDiscoveryDb();

            var fusion = Fusionar(sesiones, nodos, discoverydb);

            await CompletarInfo(fusion);

            sw.Stop();
            LogService.Debug($"[SESSIONS_HELPER] #{id} ← VistaGlobal={fusion.Count} en {sw.ElapsedMilliseconds} ms");

            return fusion;
        }
        catch (Exception ex)
        {
            LogService.Error($"[SESSIONS_HELPER] VistaGlobal: {ex.Message}");
            return new List<IscsiDestino>();
        }
    }
}
