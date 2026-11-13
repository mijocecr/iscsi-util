using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;
using ISCSI_Util.Views;

namespace ISCSI_Util.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Width = 500;
        Height = 580;
        MinWidth = 500;
        MinHeight = 580;
        MaxWidth = 500;
        MaxHeight = 580;
        Title = "iscsi-util";

        LogService.Debug("[MAIN] Main window initialized.");
    }

    // ============================================================
    // STATUS BAR
    // ============================================================
    public void UpdateMainStatus(string state)
    {
        LogService.Debug($"[MAIN] UpdateMainStatus → {state}");

        StatusBarText.Text = state switch
        {
            "SYSTEM STATUS: OK"       => "Ready.",
            "SYSTEM STATUS: Warning"  => "Warning: degraded state.",
            "SYSTEM STATUS: Critical" => "Critical: subsystem offline.",
            _                         => "Status unknown."
        };
    }

    // ============================================================
    // FLUJO PRINCIPAL OnOpened
    // ============================================================
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        try
        {
            await InicializarAsync();
        }
        catch (Exception ex)
        {
            LogService.Error($"[MAIN] Initialization failed: {ex.Message}");
            StatusBarText.Text = "Initialization failed.";
            NotificadorLinux.Enviar($"Initialization failed: {ex.Message}", 8000, "critical");
        }
    }

    private async Task InicializarAsync()
    {
        LogService.Write("[MAIN] Application startup sequence initiated.");

        // ------------------------------------------------------------
        // 0) CARGAR CONFIGURACIÓN GLOBAL
        // ------------------------------------------------------------
        ConfigManager.Load();
        LogService.Debug("[MAIN] Configuration loaded.");

        StatusBarText.Text = "Initializing...";
        await Task.Delay(120);

        // ------------------------------------------------------------
        // 1) VALIDACIÓN DE CONTRASEÑA
        // ------------------------------------------------------------
        while (true)
        {
            LogService.Debug("[MAIN] Requesting admin password...");
            await SolicitarPassword();

            if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
            {
                StatusBarText.Text = "Initialization aborted.";
                LogService.Error("[MAIN] Initialization aborted: no password provided.");
                return;
            }

            StatusBarText.Text = "Validating password...";
            LogService.Debug("[MAIN] Validating admin password...");

            var result = await ShellHelper.EjecutarComoRootAsync("bash -c \"echo OK\"");

            if (result.ExitCode == 0)
            {
                LogService.Write("[MAIN] Password validated successfully.");
                break;
            }

            LogService.Error("[MAIN] Incorrect administrator password.");
            await MostrarPasswordIncorrecta();
        }

        // ------------------------------------------------------------
        // 2) ARRANCAR LOS SERVICIOS iSCSI DISPONIBLES
        // ------------------------------------------------------------
        StatusBarText.Text = "Starting iSCSI services...";
        bool iscsiReady = await StartIscsiServicesAsync();

        // ------------------------------------------------------------
        // 3) CARGAR SESSIONS
        // ------------------------------------------------------------
        StatusBarText.Text = "Loading iSCSI information...";
        LogService.Debug("[MAIN] Loading global iSCSI session overview...");
        await LoadSessionsAsync();

        // ------------------------------------------------------------
        // 4) REFRESCAR STATUSVIEW
        // ------------------------------------------------------------
        if (StatusPanel is StatusView status)
        {
            LogService.Debug("[MAIN] Refreshing StatusView...");
            await status.RefreshStatus();
        }

        StatusBarText.Text = iscsiReady ? "Ready." : "Ready (iSCSI service unavailable).";
        LogService.Write(iscsiReady
            ? "[MAIN] Initialization completed. System ready."
            : "[MAIN] Initialization completed with iSCSI services unavailable.");
    }

    // ============================================================
    // INICIAR SERVICIOS iSCSI
    // ============================================================
    private async Task<bool> StartIscsiServicesAsync()
    {
        const string daemonUnit = "iscsid.service";
        LogService.Debug("[MAIN] Discovering installed iSCSI systemd units...");

        var unitsResult = await ShellHelper.RunCleanResultAsync(
            "systemctl list-unit-files --type=service --no-legend --no-pager");

        if (unitsResult.ExitCode != 0)
        {
            LogService.Error($"[MAIN] Unable to list systemd service units: {unitsResult.Stderr}");
            return false;
        }

        var installedUnits = new HashSet<string>(
            unitsResult.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 0)
                .Select(parts => parts[0]),
            StringComparer.OrdinalIgnoreCase);

        if (!installedUnits.Contains(daemonUnit))
        {
            LogService.Error($"[MAIN] Required systemd unit '{daemonUnit}' is not installed.");
            return false;
        }

        var daemonStatus = await ShellHelper.RunCleanResultAsync(
            $"systemctl is-active --quiet {daemonUnit}");

        if (daemonStatus.ExitCode != 0)
        {
            StatusBarText.Text = "Starting iSCSI daemon...";
            LogService.Write($"[MAIN] Starting required service '{daemonUnit}'...");

            var startResult = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"systemctl start {daemonUnit}"));

            if (startResult.ExitCode != 0)
            {
                LogService.Error($"[MAIN] Failed to start '{daemonUnit}': {startResult.Stderr}");
                return false;
            }
        }

        var sessionUnit = new[] { "iscsi.service", "open-iscsi.service" }
            .FirstOrDefault(installedUnits.Contains);

        if (sessionUnit != null)
        {
            StatusBarText.Text = "Starting iSCSI session service...";
            LogService.Write($"[MAIN] Starting available session service '{sessionUnit}'...");

            var sessionResult = await Task.Run(() =>
                ShellHelper.EjecutarComoRoot($"systemctl start {sessionUnit}"));

            if (sessionResult.ExitCode != 0)
                LogService.Error($"[MAIN] Could not start optional service '{sessionUnit}': {sessionResult.Stderr}");
        }

        var verifyResult = await ShellHelper.RunCleanResultAsync(
            $"systemctl is-active --quiet {daemonUnit}");

        if (verifyResult.ExitCode != 0)
        {
            LogService.Error($"[MAIN] Service '{daemonUnit}' did not become active: {verifyResult.Stderr}");
            return false;
        }

        LogService.Write("[MAIN] Required iSCSI daemon is active.");
        return true;
    }

    // ============================================================
    // DIÁLOGO DE CONTRASEÑA
    // ============================================================
    private async Task SolicitarPassword()
    {
        LogService.Debug("[MAIN] Opening password dialog...");

        var dialog = new PasswordDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        Credenciales.AdminPassword = await dialog.ShowDialog<string?>(this) ?? string.Empty;
        ShellHelper.ResetPasswordRejection();

        LogService.Debug("[MAIN] Password dialog closed.");
    }

    // ============================================================
    // DIÁLOGO DE CONTRASEÑA INCORRECTA
    // ============================================================
    private async Task MostrarPasswordIncorrecta()
    {
        LogService.Debug("[MAIN] Showing incorrect password dialog...");

        var dialog = new Window
        {
            Width = 380,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.FindResource("SteamPanel")!,
            Padding = new Thickness(0),
            Title = "Authentication error"
        };

        var border = new Border
        {
            Background = (IBrush)Application.Current!.FindResource("SteamCard")!,
            BorderBrush = (IBrush)Application.Current!.FindResource("SteamAccent")!,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Margin = new Thickness(6)
        };

        var stack = new StackPanel { Spacing = 14 };

        var text = new TextBlock
        {
            Text = "The administrator password is incorrect.",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Foreground = Brushes.Red,
            FontSize = 14
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            HorizontalContentAlignment= Avalonia.Layout.HorizontalAlignment.Center,
            Classes = { "SteamButton" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        okButton.Click += (_, _) => dialog.Close();

        stack.Children.Add(text);
        stack.Children.Add(okButton);

        border.Child = stack;
        dialog.Content = border;

        await dialog.ShowDialog(this);

        LogService.Debug("[MAIN] Incorrect password dialog closed.");
    }

    // ============================================================
    // CARGAR SESSIONS
    // ============================================================
    private async Task LoadSessionsAsync()
    {
        LogService.Debug("[MAIN] Loading sessions...");

        using (LoadingService.Show("Loading sessions..."))
        {
            if (SessionsPanel is SessionsView sessions)
                await sessions.CargarSesiones();

            LogService.Debug("[MAIN] Sessions loaded.");
        }
    }

    // ============================================================
    // MÉTODOS ISCSI
    // ============================================================
    public async Task DiscoverTargets(string ip)
    {
        LogService.Write($"[MAIN] Discovering targets on portal {ip}...");

        using (LoadingService.Show("Discovering targets..."))
        {
            await SystemOperationLock.RunAsync(() => IscsiHelper.Descubrir(ip));
        }
    }

    public async Task ConnectTarget(IscsiDestino d)
    {
        LogService.Write($"[MAIN] Connecting to target {d.Iqn}...");

        using (LoadingService.Show("Connecting to target..."))
        {
            await SystemOperationLock.RunAsync(() => IscsiHelper.Conectar(d));
            await Task.Delay(1500);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task DisconnectTarget(IscsiDestino d)
    {
        LogService.Write($"[MAIN] Disconnecting target {d.Iqn}...");

        using (LoadingService.Show("Disconnecting target..."))
        {
            await SystemOperationLock.RunAsync(() => IscsiHelper.Desconectar_Borrar(d));
            await Task.Delay(1500);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task InitializeDisk(IscsiDestino d, string label, string fsType)
    {
        LogService.Write($"[MAIN] Initializing disk for {d.Iqn} with FS={fsType}, Label={label}");

        using (LoadingService.Show("Initializing disk..."))
        {
            await SystemOperationLock.RunAsync(() => IscsiHelper.InicializarDestino(d, label, fsType));
            await Task.Delay(1500);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    // ============================================================
    // DOBLE CLICK → Abrir mountpoint
    // ============================================================
    private void Destino_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.Tag is not IscsiDestino destino) return;
        if (!destino.Conectado || string.IsNullOrWhiteSpace(destino.MountPoint)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{destino.MountPoint}\"",
                UseShellExecute = false
            });

            LogService.Write($"[MAIN] Opened mountpoint: {destino.MountPoint}");
        }
        catch (Exception ex)
        {
            LogService.Error($"[MAIN] Failed to open folder: {ex.Message}");
        }
    }

    // ============================================================
    // CONFIG WINDOW
    // ============================================================
    private async void OnOpenConfig(object? sender, RoutedEventArgs e)
    {
        LogService.Debug("[MAIN] Opening configuration window...");

        var win = new ConfigWindow();
        await win.ShowDialog(this);

        LogService.Write("[MAIN] Configuration updated.");
    }

    // ============================================================
    // LOGGING (usa LogService)
    // ============================================================
    private void Log(string message)
    {
        LogService.Write(message);
    }

    // ============================================================
    // EVENTOS DE PESTAÑAS
    // ============================================================
    private void OnTabStatusClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "System summary";
    }

    private void OnTabTargetClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "All discoverable targets";
    }

    private async void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Durante InitializeComponent el campo MainTabs aun no esta asignado,
        // pero el TabControl ya dispara SelectionChanged. Usamos el sender.
        if (sender is not TabControl tabs || tabs.SelectedIndex != 1)
            return;

        if (TargetsView is ISCSI_Util.Views.TargetsView targets)
        {
            try
            {
                LogService.Debug("[MAIN] Targets tab selected -> refreshing target states...");
                await targets.ActualizarEstadoAsync();
            }
            catch (Exception ex)
            {
                LogService.Error($"[MAIN] Error refreshing targets: {ex.Message}");
            }
        }
    }

    private void OnTabStatusBarClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "Cerratonix  |  https://github.com/mijocecr";
    }

    private async void OnTabSessionsClick(object? sender, PointerPressedEventArgs e)
    {
        LogService.Debug("[MAIN] Sessions tab clicked → refreshing sessions...");
        await LoadSessionsAsync();
        StatusBarText.Text = "Sessions overview";
    }

    private void onTabLogsClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "Latest iSCSI logs";
    }
}
