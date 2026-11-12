using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers;

public static class CliApp
{
    private static readonly string[] SupportedFilesystems =
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "ntfs", "exfat"
    };

    public static async Task Run()
    {
        ConfigManager.Load();
        RequestAndValidatePassword();

        if (!await StartIscsiServices())
            Console.WriteLine("Warning: required iSCSI services are not available.");

        while (true)
        {
            Console.Clear();
            Console.WriteLine("===== iSCSI-Util =====");
            Console.WriteLine("1) Targets");
            Console.WriteLine("2) Sessions");
            Console.WriteLine("3) System status");
            Console.WriteLine("4) View logs");
            Console.WriteLine("5) Settings");
            Console.WriteLine("0) Exit");

            try
            {
                switch (ReadChoice("Option: ", 0, 5))
                {
                    case 1:
                        await DiscoverTargets();
                        break;
                    case 2:
                        await ManageSessions();
                        break;
                    case 3:
                        await ShowSystemStatus();
                        break;
                    case 4:
                        ShowLogs();
                        break;
                    case 5:
                        ConfigureSettings();
                        break;
                    case 0:
                        return;
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[CLI] Operation failed: {ex.Message}");
                Console.WriteLine($"Operation failed: {ex.Message}");
            }

            Pause();
        }
    }

    private static void RequestAndValidatePassword()
    {
        while (true)
        {
            Console.Write("Enter sudo password: ");
            Credenciales.AdminPassword = ReadSecret();

            if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
            {
                Console.WriteLine("The password cannot be empty.");
                continue;
            }

            var result = ShellHelper.EjecutarComoRoot("true");
            if (result.ExitCode == 0)
                return;

            Console.WriteLine("The password could not be validated. Try again.");
        }
    }

    private static string ReadSecret()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return value.ToString();
    }

    private static async Task<bool> StartIscsiServices()
    {
        var unitsResult = await Task.Run(() =>
            ShellHelper.EjecutarComoRoot("systemctl list-unit-files --type=service --no-legend --no-pager"));

        if (unitsResult.ExitCode != 0)
        {
            Console.WriteLine($"Unable to list systemd services: {unitsResult.Stderr}");
            return false;
        }

        var installedUnits = new HashSet<string>(
            unitsResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 0)
                .Select(parts => parts[0]),
            StringComparer.OrdinalIgnoreCase);

        if (!installedUnits.Contains("iscsid.service"))
        {
            Console.WriteLine("Required service iscsid.service is not installed.");
            return false;
        }

        if (!await EnsureServiceStarted("iscsid.service"))
            return false;

        var sessionUnit = new[] { "iscsi.service", "open-iscsi.service" }
            .FirstOrDefault(installedUnits.Contains);

        return sessionUnit == null || await EnsureServiceStarted(sessionUnit);
    }

    private static async Task<bool> EnsureServiceStarted(string unit)
    {
        var status = await Task.Run(() =>
            ShellHelper.EjecutarComoRoot($"systemctl is-active --quiet {unit}"));
        if (status.ExitCode == 0)
            return true;

        var start = await Task.Run(() =>
            ShellHelper.EjecutarComoRoot($"systemctl start {unit}"));
        if (start.ExitCode == 0)
            return true;

        Console.WriteLine($"Failed to start {unit}: {start.Stderr}");
        return false;
    }

    private static async Task DiscoverTargets()
    {
        Console.Clear();
        Console.WriteLine("=== Discover targets ===");
        Console.Write("Portal IPv4 address: ");
        var portal = Console.ReadLine()?.Trim() ?? "";

        if (!IPAddress.TryParse(portal, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            Console.WriteLine("Enter a valid IPv4 address.");
            return;
        }

        var targets = await IscsiHelper.Descubrir(portal);
        foreach (var target in targets)
            await RefreshTarget(target);

        if (targets.Count == 0)
        {
            Console.WriteLine("No targets found.");
            return;
        }

        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== Discovered targets ===");
            for (int i = 0; i < targets.Count; i++)
                Console.WriteLine($"{i + 1}) {GetDisplayName(targets[i])}  [{ConnectionState(targets[i])}]");
            Console.WriteLine("0) Back");

            int choice = ReadChoice("Select a target: ", 0, targets.Count);
            if (choice == 0)
                return;

            var selected = targets[choice - 1];
            await ManageTarget(selected);
            await RefreshTarget(selected);
        }
    }

    private static async Task ManageTarget(IscsiDestino target)
    {
        while (true)
        {
            Console.Clear();
            PrintTargetDetails(target);

            Console.WriteLine("\nActions:");
            Console.WriteLine($"1) {(target.Conectado ? "Disconnect / unmount" : "Connect")}");
            Console.WriteLine("2) Initialize disk");
            Console.WriteLine("3) Configure CHAP");
            Console.WriteLine("4) Configure Mutual CHAP");
            Console.WriteLine("5) Toggle autostart persistence");
            Console.WriteLine("6) Open mount point");
            Console.WriteLine("7) Delete node configuration");
            Console.WriteLine("0) Back to targets");

            try
            {
                switch (ReadChoice("Option: ", 0, 7))
                {
                    case 1:
                        await ToggleTargetConnection(target);
                        break;
                    case 2:
                        await InitializeTarget(target);
                        break;
                    case 3:
                        await ConfigureChap(target, mutual: false);
                        break;
                    case 4:
                        await ConfigureChap(target, mutual: true);
                        break;
                    case 5:
                        await TogglePersistence(target);
                        break;
                    case 6:
                        OpenMountPoint(target.MountPoint);
                        break;
                    case 7:
                        await DeleteNode(target);
                        return;
                    case 0:
                        return;
                }

                await RefreshTarget(target);
            }
            catch (Exception ex)
            {
                LogService.Error($"[CLI] Target action failed: {ex.Message}");
                Console.WriteLine($"Action failed: {ex.Message}");
            }

            Pause();
        }
    }

    private static async Task ToggleTargetConnection(IscsiDestino target)
    {
        if (target.Conectado)
        {
            Console.WriteLine("This disconnect action also unmounts and removes the local node configuration.");
            if (!Confirm("Continue?"))
                return;

            await IscsiHelper.Desconectar_Borrar(target);
            return;
        }

        if (target.RequiresMutualChap && !target.HasLocalMutualConfigured)
        {
            Console.WriteLine("Configure Mutual CHAP before connecting.");
            return;
        }

        if (target.RequiresChap && !target.HasLocalChapConfigured)
        {
            Console.WriteLine("Configure CHAP before connecting.");
            return;
        }

        await IscsiHelper.Conectar(target);
    }

    private static async Task InitializeTarget(IscsiDestino target)
    {
        if (!target.Conectado ||
            target.TieneFilesystem ||
            string.IsNullOrWhiteSpace(target.DevicePath))
        {
            Console.WriteLine("Initialization is available only for a connected target without a filesystem.");
            return;
        }

        var filesystems = SupportedFilesystems
            .Where(IscsiHelper.SoportaFs)
            .ToArray();

        if (filesystems.Length == 0)
        {
            Console.WriteLine("No supported filesystem tools are installed.");
            return;
        }

        Console.WriteLine("Select filesystem:");
        for (int i = 0; i < filesystems.Length; i++)
            Console.WriteLine($"{i + 1}) {filesystems[i]}");
        Console.WriteLine("0) Cancel");

        int choice = ReadChoice("Option: ", 0, filesystems.Length);
        if (choice == 0)
            return;

        Console.Write("Disk label: ");
        var label = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(label))
        {
            Console.WriteLine("The label cannot be empty.");
            return;
        }
        if (label.Any(character =>
                !char.IsLetterOrDigit(character) && !" _.-".Contains(character)))
        {
            Console.WriteLine("Use only letters, numbers, spaces, dots, underscores, and hyphens in the label.");
            return;
        }

        Console.WriteLine("WARNING: Initializing the disk will erase its existing data.");
        if (!Confirm("Format and mount this target?"))
            return;

        await IscsiHelper.InicializarDestino(target, label, filesystems[choice - 1]);
        await RefreshTarget(target);
        Console.WriteLine($"Initialized. Mount point: {target.MountPoint ?? "-"}");
    }

    private static async Task ConfigureChap(IscsiDestino target, bool mutual)
    {
        Console.WriteLine(mutual ? "=== Configure Mutual CHAP ===" : "=== Configure CHAP ===");
        Console.Write("Outgoing CHAP username: ");
        string user = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Outgoing CHAP password: ");
        string password = ReadSecret();

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("Username and password are required.");
            return;
        }

        string portal = string.IsNullOrWhiteSpace(target.PortalReal) ? target.Ip : target.PortalReal;
        var commands = new List<string[]>
        {
            CreateChapCommand(target, portal, "node.session.auth.authmethod", "CHAP"),
            CreateChapCommand(target, portal, "node.session.auth.username", user),
            CreateChapCommand(target, portal, "node.session.auth.password", password)
        };
        string userIn = "";
        string passwordIn = "";

        if (mutual)
        {
            Console.Write("Incoming CHAP username: ");
            userIn = Console.ReadLine()?.Trim() ?? "";
            Console.Write("Incoming CHAP password: ");
            passwordIn = ReadSecret();

            if (string.IsNullOrWhiteSpace(userIn) || string.IsNullOrWhiteSpace(passwordIn))
            {
                Console.WriteLine("Incoming username and password are required.");
                return;
            }

            commands.Add(CreateChapCommand(target, portal, "node.session.auth.username_in", userIn));
            commands.Add(CreateChapCommand(target, portal, "node.session.auth.password_in", passwordIn));
        }

        await RunSensitiveIscsiCommands(commands);

        target.UsaChap = true;
        target.UsuarioChap = user;
        target.PasswordChap = password;
        target.LocalUser = user;
        target.LocalPass = password;
        target.HasLocalChapConfigured = true;
        if (mutual)
        {
            target.UsaMutualChap = true;
            target.UsuarioMutualChap = userIn;
            target.PasswordMutualChap = passwordIn;
            target.LocalUserIn = userIn;
            target.LocalPassIn = passwordIn;
            target.HasLocalMutualConfigured = true;
        }
        Console.WriteLine(mutual ? "Mutual CHAP credentials configured." : "CHAP credentials configured.");
    }

    private static string[] CreateChapCommand(
        IscsiDestino target,
        string portal,
        string name,
        string value)
    {
        return new[]
        {
            "iscsiadm",
            "-m", "node",
            "-T", target.Iqn,
            "-p", portal,
            "--op=update",
            "--name", name,
            "--value", value
        };
    }

    private static async Task RunSensitiveIscsiCommands(IEnumerable<string[]> commands)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-k");
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-s");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Unable to start sudo for CHAP configuration.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        string script = "set -e\n" + string.Join("\n", commands.Select(command =>
            string.Join(" ", command.Select(QuoteForShell))));
        await process.StandardInput.WriteLineAsync(Credenciales.AdminPassword);
        await process.StandardInput.WriteLineAsync(script);
        process.StandardInput.Close();

        var exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15))) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out while applying CHAP configuration.");
        }

        string stderr = await stderrTask;
        await stdoutTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to apply CHAP configuration: {stderr.Trim()}");
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task TogglePersistence(IscsiDestino target)
    {
        if (!target.Conectado)
        {
            Console.WriteLine("Persistence can be changed only while the target is connected.");
            return;
        }

        await RefreshTarget(target);
        if (!target.TieneFilesystem)
        {
            Console.WriteLine("Persistence requires a filesystem on the target.");
            return;
        }

        bool persistent = await Task.Run(() => IscsiPersistenceManager.Detect(target));
        if (persistent)
            await IscsiPersistenceManager.RemoveAsync(target);
        else
            await IscsiPersistenceManager.ApplyAsync(target);

        bool updated = await Task.Run(() => IscsiPersistenceManager.Detect(target));
        Console.WriteLine(updated ? "Autostart persistence is enabled." : "Autostart persistence is disabled.");
    }

    private static async Task DeleteNode(IscsiDestino target)
    {
        Console.WriteLine("This removes the local iSCSI node configuration and related persistence.");
        if (!Confirm("Delete this node configuration?"))
            return;

        await IscsiHelper.Desconectar_Borrar(target);
        Console.WriteLine("Node configuration removed.");
    }

    private static void OpenMountPoint(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || !Directory.Exists(mountPoint))
        {
            Console.WriteLine("The target has no available mount point.");
            return;
        }

        var startInfo = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(mountPoint);
        Process.Start(startInfo);
    }

    private static async Task RefreshTarget(IscsiDestino target)
    {
        await IscsiHelper.CompletarInformacionDestino(target, 0);
        target.Persistir = await Task.Run(() => IscsiPersistenceManager.Detect(target));
        target.UsaChap = target.RequiresChap || target.HasLocalChapConfigured;
        target.UsaMutualChap = target.RequiresMutualChap || target.HasLocalMutualConfigured;
    }

    private static void PrintTargetDetails(IscsiDestino target)
    {
        Console.WriteLine($"=== {GetDisplayName(target)} ===");
        Console.WriteLine($"IQN:           {target.Iqn}");
        Console.WriteLine($"Portal:        {target.Ip}");
        Console.WriteLine($"Status:        {ConnectionState(target)}");
        Console.WriteLine($"Device:        {target.DevicePath ?? "-"}");
        Console.WriteLine($"Filesystem:    {(target.TieneFilesystem ? target.FsType : "No")}");
        Console.WriteLine($"Capacity:      {(target.SizeGb > 0 ? $"{target.SizeGb} GB" : "-")}");
        Console.WriteLine($"Mount point:   {target.MountPoint ?? "-"}");
        Console.WriteLine($"Persistence:   {(target.Persistir ? "Enabled" : "Disabled")}");
        Console.WriteLine($"Authentication:{(target.UsaMutualChap ? " Mutual CHAP" : target.UsaChap ? " CHAP" : " None")}");
    }

    private static async Task ManageSessions()
    {
        var sessions = await IscsiSessions.ObtenerVistaGlobal();
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active sessions.");
            return;
        }

        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== Active sessions ===");
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                Console.WriteLine($"{i + 1}) {session.Iqn} ({session.Portal}) LUN {session.LunId}");
            }
            Console.WriteLine($"{sessions.Count + 1}) Rescan all targets");
            Console.WriteLine($"{sessions.Count + 2}) Disconnect all sessions");
            Console.WriteLine("0) Back");

            int choice = ReadChoice("Select an option: ", 0, sessions.Count + 2);
            if (choice == 0)
                return;

            if (choice == sessions.Count + 1)
            {
                var results = await IscsiRescan.RescanAllAsync(sessions);
                if (results.Count == 0)
                    Console.WriteLine("No connected targets to rescan.");
                else
                    foreach (var result in results)
                        PrintRescanResult(result);

                sessions = await IscsiSessions.ObtenerVistaGlobal();
                continue;
            }

            if (choice == sessions.Count + 2)
            {
                if (Confirm($"Disconnect all {sessions.Count} active sessions?"))
                {
                    foreach (var session in sessions)
                        await IscsiHelper.DesconectarSesion(session);
                    Console.WriteLine("All active sessions disconnected.");
                    return;
                }
                continue;
            }

            var selected = sessions[choice - 1];
            await ManageSession(selected);
            sessions = await IscsiSessions.ObtenerVistaGlobal();
            if (sessions.Count == 0)
            {
                Console.WriteLine("No active sessions remain.");
                return;
            }
        }
    }

    private static async Task ManageSession(SessionInfo session)
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine($"=== {session.Iqn} ===");
            Console.WriteLine($"Portal:       {session.Portal}");
            Console.WriteLine($"Status:       {(session.Connected ? "Connected" : "Disconnected")}");
            Console.WriteLine($"Device:       {session.Device}");
            Console.WriteLine($"Filesystem:   {session.Filesystem}");
            Console.WriteLine($"Capacity:     {(session.SizeBytes > 0 ? IscsiRescan.FormatBytes(session.SizeBytes) : session.SizeGb > 0 ? $"{session.SizeGb} GB" : "-")}");
            Console.WriteLine($"Mount point:  {session.MountPoint}");
            Console.WriteLine($"LUN:          {session.LunId}");
            Console.WriteLine($"Authentication: {session.Auth}");
            Console.WriteLine("\nActions:");
            Console.WriteLine($"1) {(session.Connected ? "Disconnect" : "Connect")}");
            Console.WriteLine("2) Open mount point");
            Console.WriteLine("3) Rescan target LUNs");
            Console.WriteLine("0) Back to sessions");

            switch (ReadChoice("Option: ", 0, 3))
            {
                case 1:
                    if (session.Connected)
                        await IscsiHelper.DesconectarSesion(session);
                    else
                        await IscsiHelper.ConectarSesion(session);
                    return;
                case 2:
                    OpenMountPoint(session.MountPoint);
                    break;
                case 3:
                    if (!session.Connected)
                    {
                        Console.WriteLine("The target is not connected.");
                        break;
                    }

                    var rescan = await IscsiRescan.RescanTargetAsync(session.Iqn, session.Portal);
                    PrintRescanResult(rescan);

                    var refreshed = (await IscsiSessions.ObtenerVistaGlobal())
                        .FirstOrDefault(x => x.Connected &&
                            x.Iqn.Equals(session.Iqn, StringComparison.OrdinalIgnoreCase) &&
                            x.Portal.Equals(session.Portal, StringComparison.OrdinalIgnoreCase) &&
                            x.LunId == session.LunId);

                    if (refreshed != null)
                        session = refreshed;
                    break;
                case 0:
                    return;
            }

            Pause();
        }
    }

    private static void PrintRescanResult(TargetRescanResult result)
    {
        Console.WriteLine();

        if (!result.Success)
        {
            Console.WriteLine($"Rescan failed: {result.Message}");
            return;
        }

        if (!result.HasChanges)
        {
            Console.WriteLine($"{result.DisplayTarget}: {result.Summary}");
            return;
        }

        Console.WriteLine($"{result.DisplayTarget}: {result.ShortSummary}");

        foreach (var change in result.ChangedLuns)
            Console.WriteLine($"  - {change.Describe()}");
    }

    private static async Task ShowSystemStatus()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== System status ===");

            foreach (var unit in new[] { "iscsid.service", "iscsi.service", "open-iscsi.service" })
            {
                var result = ShellHelper.EjecutarComoRoot($"systemctl is-active {unit}");
                if (result.ExitCode == 0 || !string.IsNullOrWhiteSpace(result.Stdout))
                    Console.WriteLine($"{unit}: {result.Stdout.Trim()}");
            }

            var address = ShellHelper.EjecutarComoRoot("hostname -I");
            Console.WriteLine($"Network addresses: {address.Stdout.Trim()}");

            var sessions = await IscsiSessions.ObtenerVistaGlobal();
            Console.WriteLine($"Active iSCSI sessions: {sessions.Count}");
            Console.WriteLine("\n1) Restart iSCSI daemon");
            Console.WriteLine("0) Back");

            if (ReadChoice("Option: ", 0, 1) == 0)
                return;

            var restart = ShellHelper.EjecutarComoRoot("systemctl restart iscsid.service");
            Console.WriteLine(restart.ExitCode == 0
                ? "iSCSI daemon restarted."
                : $"Restart failed: {restart.Stderr}");
            Pause();
        }
    }

    private static void ShowLogs()
    {
        string appLog = Path.Combine(ConfigManager.LogPath, "iscsi-util.log");
        Console.Clear();
        Console.WriteLine("=== Logs ===");
        Console.WriteLine("1) Show latest log entries");
        Console.WriteLine("2) Open application log file");
        Console.WriteLine("0) Back");

        switch (ReadChoice("Option: ", 0, 2))
        {
            case 0:
                return;
            case 2:
                if (File.Exists(appLog))
                {
                    Process.Start(new ProcessStartInfo(appLog) { UseShellExecute = true });
                    return;
                }
                Console.WriteLine("The application log file does not exist yet.");
                return;
        }

        if (File.Exists(appLog))
        {
            foreach (var line in File.ReadLines(appLog).TakeLast(200))
                Console.WriteLine(line);
            return;
        }

        var journal = ShellHelper.EjecutarComoRoot("journalctl -u iscsid --no-pager -n 100");
        Console.WriteLine(string.IsNullOrWhiteSpace(journal.Stdout)
            ? "No iSCSI daemon log entries found."
            : journal.Stdout);
    }

    private static void ConfigureSettings()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== Settings ===");
            Console.WriteLine($"Current mount directory: {ConfigManager.MountBasePath}");
            Console.WriteLine($"Current permissions:     {ConfigManager.DefaultPermissions}");
            Console.WriteLine($"Current log directory:   {ConfigManager.LogPath}");
            Console.WriteLine($"Verbose logging:         {(ConfigManager.Verbose ? "On" : "Off")}");
            Console.WriteLine("\n1) Change mount directory");
            Console.WriteLine("2) Change default permissions");
            Console.WriteLine("3) Change log directory");
            Console.WriteLine("4) Toggle verbose logging");
            Console.WriteLine("5) Save settings");
            Console.WriteLine("0) Cancel");

            switch (ReadChoice("Option: ", 0, 5))
            {
                case 1:
                    SetDirectory("Mount directory", value => ConfigManager.MountBasePath = value);
                    break;
                case 2:
                    SetPermissions();
                    break;
                case 3:
                    SetDirectory("Log directory", value => ConfigManager.LogPath = value);
                    break;
                case 4:
                    ConfigManager.Verbose = !ConfigManager.Verbose;
                    break;
                case 5:
                    ConfigManager.Save();
                    Console.WriteLine("Settings saved.");
                    return;
                case 0:
                    return;
            }
        }
    }

    private static void SetDirectory(string label, Action<string> assign)
    {
        Console.Write($"{label} (absolute path): ");
        var path = Console.ReadLine()?.Trim() ?? "";
        if (!Path.IsPathRooted(path))
        {
            Console.WriteLine("Please enter an absolute path.");
            return;
        }

        assign(path);
    }

    private static void SetPermissions()
    {
        Console.WriteLine("1) 755 (owner can write; others can read)");
        Console.WriteLine("2) 775 (owner/group can write)");
        Console.WriteLine("3) 770 (owner/group can write; others have no access)");
        Console.WriteLine("4) 777 (everyone can write)");
        Console.WriteLine("0) Keep current value");

        switch (ReadChoice("Option: ", 0, 4))
        {
            case 1:
                ConfigManager.DefaultPermissions = 755;
                break;
            case 2:
                ConfigManager.DefaultPermissions = 775;
                break;
            case 3:
                ConfigManager.DefaultPermissions = 770;
                break;
            case 4:
                ConfigManager.DefaultPermissions = 777;
                break;
        }
    }

    private static string GetDisplayName(IscsiDestino target) =>
        target.Iqn.Contains(':')
            ? target.Iqn[(target.Iqn.LastIndexOf(':') + 1)..]
            : target.Iqn;

    private static string ConnectionState(IscsiDestino target) =>
        target.Conectado ? "Connected" : "Disconnected";

    private static int ReadChoice(string prompt, int minimum, int maximum)
    {
        while (true)
        {
            Console.Write(prompt);
            if (int.TryParse(Console.ReadLine(), out int choice) &&
                choice >= minimum &&
                choice <= maximum)
                return choice;

            Console.WriteLine($"Enter a number from {minimum} to {maximum}.");
        }
    }

    private static bool Confirm(string prompt)
    {
        Console.WriteLine($"1) Yes    0) No");
        return ReadChoice($"{prompt} ", 0, 1) == 1;
    }

    private static void Pause()
    {
        Console.WriteLine("\nPress ENTER to continue...");
        Console.ReadLine();
    }
}
