using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISCSI_Util.Models;

namespace ISCSI_Util.Helpers
{
    public static class CliApp
    {
        public static async Task Run()
        {
            AskPasswordOnce();

            while (true)
            {
                Console.Clear();
                Console.WriteLine("===== iSCSI-Util CLI =====\n");
                Console.WriteLine("1) Discover targets");
                Console.WriteLine("2) Connect target");
                Console.WriteLine("3) Disconnect target");
                Console.WriteLine("4) Disconnect + delete node");
                Console.WriteLine("5) Show target details");
                Console.WriteLine("6) Initialize target (mkfs + mount)");
                Console.WriteLine("7) Apply persistence (fstab + systemd)");
                Console.WriteLine("8) Remove persistence");
                Console.WriteLine("9) Detect persistence");
                Console.WriteLine("10) List active iSCSI sessions");
                Console.WriteLine("11) Show all targets");
                Console.WriteLine("12) Disconnect ALL sessions");
                Console.WriteLine("0) Exit");
                Console.WriteLine("==========================\n");
                Console.Write("Option: ");

                var opt = Console.ReadLine();

                switch (opt)
                {
                    case "1": await Discover(); break;
                    case "2": await Connect(); break;
                    case "3": await Disconnect(); break;
                    case "4": await DisconnectDelete(); break;
                    case "5": await Details(); break;
                    case "6": await Initialize(); break;
                    case "7": await ApplyPersistence(); break;
                    case "8": await RemovePersistence(); break;
                    case "9": await DetectPersistence(); break;
                    case "10": await ListSessions(); break;
                    case "11": await ShowAllTargets(); break;
                    case "12": await DisconnectAllSessions(); break;
                    case "0": return;
                    default:
                        Console.WriteLine("Invalid option.");
                        break;
                }

                Console.WriteLine("Press ENTER to continue...");
                Console.ReadLine();
            }
        }

        // ==========================================================
        // PASSWORD
        // ==========================================================
        private static void AskPasswordOnce()
        {
            if (!string.IsNullOrEmpty(Credenciales.AdminPassword))
                return;

            Console.Write("Enter sudo password: ");
            Credenciales.AdminPassword = ReadPassword();
        }

        private static string ReadPassword()
        {
            string pass = "";
            ConsoleKeyInfo key;

            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
                {
                    pass = pass[..^1];
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                    pass += key.KeyChar;
            }

            Console.WriteLine();
            return pass;
        }

        // ==========================================================
        // DISCOVER
        // ==========================================================
        private static async Task Discover()
        {
            Console.Clear();
            Console.WriteLine("=== Discover targets ===");
            Console.Write("Portal/IP: ");
            var ip = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(ip))
                return;

            var destinos = await IscsiCore.Discover(ip);

            if (destinos.Count == 0)
            {
                Console.WriteLine("No targets found.");
                return;
            }

            int i = 1;
            foreach (var d in destinos)
            {
                Console.WriteLine($"{i++}) {d.Iqn}");
                Console.WriteLine($"    IP: {d.Ip}");
                Console.WriteLine($"    Connected: {d.Conectado}");
                Console.WriteLine($"    CHAP: {d.UsaChap}, Mutual: {d.UsaMutualChap}");
                Console.WriteLine();
            }
        }

        private static async Task<IscsiDestino?> SelectDestinoFromDiscover()
        {
            Console.Write("Portal/IP to discover: ");
            var ip = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            var destinos = await IscsiCore.Discover(ip);
            if (destinos.Count == 0)
                return null;

            for (int i = 0; i < destinos.Count; i++)
                Console.WriteLine($"{i + 1}) {destinos[i].Iqn} ({destinos[i].Ip})");

            Console.Write("Select index: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return null;

            idx--;
            if (idx < 0 || idx >= destinos.Count)
                return null;

            return destinos[idx];
        }

        // ==========================================================
        // CONNECT
        // ==========================================================
        private static async Task Connect()
        {
            Console.Clear();
            Console.WriteLine("=== Connect target ===");

            var destino = await SelectDestinoFromDiscover();
            if (destino == null)
                return;

            await IscsiCore.CompleteInfo(destino);
            await IscsiCore.Connect(destino);

            Console.WriteLine($"Connected. MountPoint: {destino.MountPoint}");
        }

        // ==========================================================
        // DISCONNECT
        // ==========================================================
        private static async Task Disconnect()
        {
            Console.Clear();
            Console.WriteLine("=== Disconnect target ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            Console.WriteLine($"\nDisconnecting {destino.Iqn}...\n");
            await IscsiCore.Disconnect(destino);

            Console.WriteLine("Disconnected.");
        }

        // ==========================================================
        // DISCONNECT + DELETE NODE
        // ==========================================================
        private static async Task DisconnectDelete()
        {
            Console.Clear();
            Console.WriteLine("=== Disconnect + delete node ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            Console.WriteLine($"\nDisconnecting and deleting node {destino.Iqn}...\n");
            await IscsiCore.DisconnectDelete(destino);

            Console.WriteLine("Disconnected and node removed.");
        }

        // ==========================================================
        // DETAILS
        // ==========================================================
        private static async Task Details()
        {
            Console.Clear();
            Console.WriteLine("=== Target details ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            await IscsiCore.CompleteInfo(destino);

            Console.WriteLine("\n=== Details ===\n");
            Console.WriteLine($"IQN:           {destino.Iqn}");
            Console.WriteLine($"Portal/IP:     {destino.Ip}");
            Console.WriteLine($"Connected:     {destino.Conectado}");
            Console.WriteLine($"DevicePath:    {destino.DevicePath}");
            Console.WriteLine($"PartitionPath: {destino.PartitionPath}");
            Console.WriteLine($"Filesystem:    {destino.FsType}");
            Console.WriteLine($"MountPoint:    {destino.MountPoint}");
            Console.WriteLine($"Persistent:    {IscsiPersistenceManager_CLI.Detect(destino)}");
        }

        // ==========================================================
        // INITIALIZE
        // ==========================================================
        private static async Task Initialize()
        {
            Console.Clear();
            Console.WriteLine("=== Initialize target ===");

            var destino = await SelectDestinoFromDiscover();
            if (destino == null)
                return;

            Console.Write("Label: ");
            var label = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(label))
                label = "iscsi-disk";

            Console.Write("Filesystem: ");
            var fs = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(fs))
                fs = "ext4";

            if (!IscsiHelper.SoportaFs(fs))
            {
                Console.WriteLine("Filesystem not supported.");
                return;
            }

            await IscsiCore.Initialize(destino, label, fs);

            Console.WriteLine($"Initialized and mounted: {destino.MountPoint}");
        }

        // ==========================================================
        // SHOW ALL TARGETS
        // ==========================================================
        private static async Task ShowAllTargets()
        {
            Console.Clear();
            Console.WriteLine("=== All active iSCSI targets ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            int index = 1;

            foreach (var destino in lista)
            {
                Console.WriteLine($"[{index}] {destino.Iqn} ({destino.Ip})");

                await IscsiCore.CompleteInfo(destino);

                Console.WriteLine($"    DevicePath:    {destino.DevicePath}");
                Console.WriteLine($"    PartitionPath: {destino.PartitionPath}");
                Console.WriteLine($"    Filesystem:    {destino.FsType}");
                Console.WriteLine($"    MountPoint:    {destino.MountPoint}");
                Console.WriteLine($"    Persistent:    {IscsiPersistenceManager_CLI.Detect(destino)}");
                Console.WriteLine();

                index++;
            }
        }

        // ==========================================================
        // DISCONNECT ALL SESSIONS
        // ==========================================================
        private static async Task DisconnectAllSessions()
        {
            Console.Clear();
            Console.WriteLine("=== Disconnect ALL sessions ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("The following sessions will be disconnected:\n");
            foreach (var d in lista)
                Console.WriteLine($" - {d.Iqn} ({d.Ip})");

            Console.WriteLine($"\nTotal sessions: {lista.Count}");
            Console.WriteLine("WARNING: This will disconnect ALL active iSCSI sessions.");
            Console.WriteLine("Type EXACTLY 'YES' to confirm.");
            Console.Write("\nConfirm: ");

            var confirm = Console.ReadLine()?.Trim();
            if (confirm != "YES")
            {
                Console.WriteLine("Cancelled.");
                return;
            }

            Console.WriteLine();

            foreach (var destino in lista)
            {
                Console.WriteLine($"Disconnecting {destino.Iqn}...");
                await IscsiCore.Disconnect(destino);
            }

            Console.WriteLine("\nAll sessions disconnected.");
        }

        // ==========================================================
        // APPLY PERSISTENCE (CLI)
        // ==========================================================
        private static async Task ApplyPersistence()
        {
            Console.Clear();
            Console.WriteLine("=== Apply persistence ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            var portalReal = IscsiCore.ObtenerPortalReal(destino);
            if (!string.IsNullOrWhiteSpace(portalReal))
                destino.Ip = portalReal;

            await IscsiCore.CompleteInfo(destino);

            if (!destino.TieneFilesystem)
            {
                Console.WriteLine("Cannot apply persistence: no filesystem detected.");
                return;
            }

            if (string.IsNullOrWhiteSpace(destino.MountPoint))
            {
                Console.WriteLine("Target is not mounted. Mounting...");
                await IscsiCore.Mount(destino);
                await IscsiCore.CompleteInfo(destino);
            }

            Console.WriteLine("\n=== DEBUG APPLY ===");
            Console.WriteLine($"IQN:           {destino.Iqn}");
            Console.WriteLine($"IP:            {destino.Ip}");
            Console.WriteLine($"DevicePath:    {destino.DevicePath}");
            Console.WriteLine($"PartitionPath: {destino.PartitionPath}");
            Console.WriteLine($"MountPoint:    {destino.MountPoint}");
            Console.WriteLine($"FsType:        {destino.FsType}");
            Console.WriteLine($"TieneFS:       {destino.TieneFilesystem}");
            Console.WriteLine("=== FIN DEBUG APPLY ===\n");

            await IscsiPersistenceManager_CLI.ApplyAsync(destino);

            Console.WriteLine("\nPersistence applied.");
        }

        // ==========================================================
        // REMOVE PERSISTENCE (CLI)
        // ==========================================================
        private static async Task RemovePersistence()
        {
            Console.Clear();
            Console.WriteLine("=== Remove persistence ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            var portalReal = IscsiCore.ObtenerPortalReal(destino);
            if (!string.IsNullOrWhiteSpace(portalReal))
                destino.Ip = portalReal;

            await IscsiCore.CompleteInfo(destino);

            await IscsiPersistenceManager_CLI.RemoveAsync(destino);

            Console.WriteLine("\nPersistence removed.");
        }

        // ==========================================================
        // DETECT PERSISTENCE (CLI)
        // ==========================================================
        private static async Task DetectPersistence()
        {
            Console.Clear();
            Console.WriteLine("=== Detect persistence ===\n");

            var sesiones = ShellHelper.EjecutarComoRoot("iscsiadm -m session").Stdout;
            var lista = ParseActiveSessions(sesiones);

            if (lista.Count == 0)
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine("Active sessions:\n");
            for (int i = 0; i < lista.Count; i++)
                Console.WriteLine($"{i + 1}) {lista[i].Iqn} ({lista[i].Ip})");

            Console.Write("\nSelect target: ");
            if (!int.TryParse(Console.ReadLine(), out int idx))
                return;

            idx--;
            if (idx < 0 || idx >= lista.Count)
                return;

            var destino = lista[idx];

            var portalReal = IscsiCore.ObtenerPortalReal(destino);
            if (!string.IsNullOrWhiteSpace(portalReal))
                destino.Ip = portalReal;

            await IscsiCore.CompleteInfo(destino);

            bool persist = IscsiPersistenceManager_CLI.Detect(destino);

            Console.WriteLine($"\nPersistence: {(persist ? "YES" : "NO")}");
        }

        // ---------------------------------------------------------
        // LIST SESSIONS
        // ---------------------------------------------------------
        private static async Task ListSessions()
        {
            Console.Clear();
            Console.WriteLine("=== Active iSCSI sessions ===\n");

            var result = ShellHelper.EjecutarComoRoot("iscsiadm -m session");

            if (string.IsNullOrWhiteSpace(result.Stdout))
            {
                Console.WriteLine("No active sessions.");
                return;
            }

            Console.WriteLine(result.Stdout);
        }

        // ---------------------------------------------------------
        // HELPER: PARSE ACTIVE SESSIONS
        // ---------------------------------------------------------
        private static List<IscsiDestino> ParseActiveSessions(string sesiones)
        {
            var lista = new List<IscsiDestino>();

            foreach (var line in sesiones.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("iqn.")) continue;

                var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string iqn = partes.LastOrDefault(p => p.StartsWith("iqn."));
                if (iqn == null) continue;

                // Ejemplo línea:
                // tcp: [6] 192.168.10.20:3260,1 iqn.2013-03.com.wdc:mycloudex2ultra:mjcc (non-flash)
                //
                // partes[2] = "192.168.10.20:3260,1"
                // portal = "192.168.10.20:3260"

                string portal = partes[2].Trim().Split(',')[0];

                lista.Add(new IscsiDestino
                {
                    Iqn = iqn,
                    Ip = portal,
                    Conectado = true
                });
            }

            return lista;
        }
    }
}
