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
            NotificadorLinux.Enviar($"[ERROR] Failed to get local IP: {ex.Message}", 6000, "critical", "dialog-error");
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
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] MismaSubred: {ex.Message}");
            return false;
        }
    }

    // ============================================================
    //   1) SESIONES ACTIVAS
    // ============================================================

    private static List<IscsiDestino> ObtenerSesionesActivas()
    {
        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → SesionesActivas()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            var list = new List<IscsiDestino>();

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
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerSesionesActivas: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to read active sessions: {ex.Message}", 6000, "critical", "dialog-error");
            return new List<IscsiDestino>();
        }
    }

    // ============================================================
    //   2) NODOS CONOCIDOS
    // ============================================================

    private static List<IscsiDestino> ObtenerNodos()
    {
        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → Nodos()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m node");

            var list = new List<IscsiDestino>();

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
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerNodos: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to read nodes: {ex.Message}", 6000, "critical", "dialog-error");
            return new List<IscsiDestino>();
        }
    }

    // ============================================================
    //   3) DISCOVERYDB
    // ============================================================

    private static List<IscsiDestino> ObtenerDiscoveryDb()
    {
        try
        {
            long id = NextId();
            var sw = Stopwatch.StartNew();

            Console.WriteLine($"[SESSIONS_HELPER] #{id} → DiscoveryDB()");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m discoverydb -t sendtargets -o show");

            var list = new List<IscsiDestino>();

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
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] ObtenerDiscoveryDb: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to read discoverydb: {ex.Message}", 6000, "critical", "dialog-error");
            return new List<IscsiDestino>();
        }
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

            var grupos = todos.GroupBy(x => x.Iqn);

            string ipLocal = GetLocalIPv4();

            var final = new List<IscsiDestino>();

            foreach (var g in grupos)
            {
                string iqn = g.Key;
                var lista = g.ToList();

                var sesionActiva = lista.FirstOrDefault(x => x.Conectado);
                if (sesionActiva != null)
                {
                    final.Add(new IscsiDestino
                    {
                        Ip = sesionActiva.Ip,
                        Iqn = iqn,
                        Conectado = true
                    });
                    continue;
                }

                var mismaSubred = lista.FirstOrDefault(x => MismaSubred(ipLocal, x.Ip));
                if (mismaSubred != null)
                {
                    final.Add(new IscsiDestino
                    {
                        Ip = mismaSubred.Ip,
                        Iqn = iqn,
                        Conectado = false
                    });
                    continue;
                }

                var primero = lista.First();
                final.Add(new IscsiDestino
                {
                    Ip = primero.Ip,
                    Iqn = iqn,
                    Conectado = false
                });
            }

            return final
                .OrderByDescending(x => x.Conectado)
                .ThenBy(x => x.Iqn)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] Fusionar: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to merge session data: {ex.Message}", 6000, "critical", "dialog-error");
            return new List<IscsiDestino>();
        }
    }

    // ============================================================
    //   5) COMPLETAR INFORMACIÓN REAL
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
                    NotificadorLinux.Enviar($"[ERROR] Failed to complete info for {d.Iqn}: {ex.Message}", 6000, "critical", "dialog-error");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS_HELPER][ERROR] CompletarInfo: {ex.Message}");
            NotificadorLinux.Enviar($"[ERROR] Failed to complete session info: {ex.Message}", 6000, "critical", "dialog-error");
        }

        sw.Stop();
        Console.WriteLine($"[SESSIONS_HELPER] #{id} ← CompletarInfo en {sw.ElapsedMilliseconds} ms");
    }

    // ============================================================
    //   6) VISTA GLOBAL
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
            NotificadorLinux.Enviar($"[ERROR] Failed to load session view: {ex.Message}", 6000, "critical", "dialog-error");
            return new List<IscsiDestino>();
        }
    }
}
