using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;

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
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] GetLocalIPv4: {ex.Message}");
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

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → SesionesActivas()");

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
            Console.WriteLine($"[SESSIONS_HELPER] #{id} ← SesionesActivas={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerSesionesActivas: {ex.Message}");
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

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → Nodos()");

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
            Console.WriteLine($"[SESSIONS_HELPER] #{id} ← Nodos={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerNodos: {ex.Message}");
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

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → DiscoveryDB()");

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
            Console.WriteLine($"[SESSIONS_HELPER] #{id} ← DiscoveryDB={list.Count} en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerDiscoveryDb: {ex.Message}");
        }

        return list;
    }

    // ============================================================
    //   4) FUSIÓN INTELIGENTE (IQN + IP)
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

            // Agrupar por IQN + IP
            var grupos = todos.GroupBy(x => $"{x.Iqn}|{x.Ip}");

            var final = new List<IscsiDestino>();

            foreach (var g in grupos)
            {
                var lista = g.ToList();

                // Prioridad 1: sesión activa
                var activo = lista.FirstOrDefault(x => x.Conectado);
                if (activo != null)
                {
                    final.Add(activo);
                    continue;
                }

                // Prioridad 2: nodo configurado
                var nodo = lista.FirstOrDefault(x => !x.Conectado);
                if (nodo != null)
                {
                    final.Add(nodo);
                    continue;
                }

                // Prioridad 3: discoverydb
                final.Add(lista.First());
            }

            return final
                .OrderByDescending(x => x.Conectado)
                .ThenBy(x => x.Iqn)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] Fusionar: {ex.Message}");
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

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → CompletarInfo() conectados={conectados.Count}");

            foreach (var d in conectados)
            {
                try
                {
                    await IscsiHelper.CompletarInformacionDestino(d, id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SESSIONS_HELPER] #{id} ERROR completando {d.Iqn}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] CompletarInfo: {ex.Message}");
        }

        sw.Stop();
        Console.WriteLine($"[SESSIONS_HELPER] #{id} ← CompletarInfo en {sw.ElapsedMilliseconds} ms");
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
            Console.WriteLine($"[SESSIONS_HELPER] #{id} → VistaGlobal()");

            var sesiones = ObtenerSesionesActivas();
            var nodos = ObtenerNodos();
            var discoverydb = ObtenerDiscoveryDb();

            var fusion = Fusionar(sesiones, nodos, discoverydb);

            await CompletarInfo(fusion);

            sw.Stop();
            Console.WriteLine($"[SESSIONS_HELPER] #{id} ← VistaGlobal={fusion.Count} en {sw.ElapsedMilliseconds} ms");

            return fusion;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] VistaGlobal: {ex.Message}");
            return new List<IscsiDestino>();
        }
    }
}
