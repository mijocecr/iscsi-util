using System;
using System.Diagnostics;
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

            var result = ShellHelper.EjecutarComoRoot("bash -c \"echo OK\"");

            if (result.ExitCode == 0)
            {
                LogService.Write("[MAIN] Password validated successfully.");
                break;
            }

            LogService.Error("[MAIN] Incorrect administrator password.");
            await MostrarPasswordIncorrecta();
        }

        // ------------------------------------------------------------
        // 2) ARRANCAR ISCSID SOLO SI ES NECESARIO
        // ------------------------------------------------------------
        StatusBarText.Text = "Checking iSCSI service...";
        LogService.Debug("[MAIN] Checking iscsid service status...");

        var statusCheck = ShellHelper.EjecutarComoRoot("systemctl is-active iscsid");

        if (!statusCheck.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            StatusBarText.Text = "Starting iSCSI service...";
            LogService.Write("[MAIN] iscsid not active. Starting service...");
            ShellHelper.EjecutarComoRoot("systemctl start iscsid");
        }

        await WaitForDaemonReady();

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

        StatusBarText.Text = "Ready.";
        LogService.Write("[MAIN] Initialization completed. System ready.");
    }

    // ============================================================
    // ESPERAR A QUE ISCSID ESTÉ LISTO
    // ============================================================
    private async Task WaitForDaemonReady()
    {
        LogService.Debug("[MAIN] Waiting for iscsid to become active...");

        for (int i = 0; i < 40; i++)
        {
            var result = ShellHelper.EjecutarComoRoot(
                "systemctl show -p ActiveState iscsid"
            );

            if (result.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Debug("[MAIN] iscsid is active.");
                return;
            }

            await Task.Delay(120);
        }

        LogService.Error("[MAIN] iscsid did not reach active state within timeout.");
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
            await IscsiHelper.Descubrir(ip);
        }
    }

    public async Task ConnectTarget(IscsiDestino d)
    {
        LogService.Write($"[MAIN] Connecting to target {d.Iqn}...");

        using (LoadingService.Show("Connecting to target..."))
        {
            await IscsiHelper.Conectar(d);
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
            await IscsiHelper.Desconectar(d);
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
            await IscsiHelper.InicializarDestino(d, label, fsType);
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
