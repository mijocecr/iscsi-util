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

        Log("Main window initialized.");
    }

    // ============================================================
    // STATUS BAR
    // ============================================================
    public void UpdateMainStatus(string state)
    {
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

        // ------------------------------------------------------------
        // 0) CARGAR CONFIGURACIÓN GLOBAL
        // ------------------------------------------------------------
        ConfigManager.Load();
        Log("Configuration loaded.");

        StatusBarText.Text = "Initializing...";
        await Task.Delay(120);

        // ------------------------------------------------------------
        // 1) VALIDACIÓN DE CONTRASEÑA
        // ------------------------------------------------------------
        while (true)
        {
            await SolicitarPassword();

            if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
            {
                StatusBarText.Text = "Initialization aborted.";
                Log("Initialization aborted: no password provided.");
                return;
            }

            StatusBarText.Text = "Validating password...";
            Log("Validating admin password...");

            var result = ShellHelper.EjecutarComoRoot("bash -c \"echo OK\"");

            if (result.ExitCode == 0)
            {
                Log("Password validated successfully.");
                break;
            }

            LogService.Error("Incorrect administrator password.");
            await MostrarPasswordIncorrecta();
        }

        // ------------------------------------------------------------
        // 2) ARRANCAR ISCSID SOLO SI ES NECESARIO
        // ------------------------------------------------------------
        StatusBarText.Text = "Checking iSCSI service...";
        Log("Checking iscsid service status...");

        var statusCheck = ShellHelper.EjecutarComoRoot("systemctl is-active iscsid");

        if (!statusCheck.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            StatusBarText.Text = "Starting iSCSI service...";
            Log("iscsid not active. Starting service...");
            ShellHelper.EjecutarComoRoot("systemctl start iscsid");
        }

        await WaitForDaemonReady();

        // ------------------------------------------------------------
        // 3) CARGAR SESSIONS
        // ------------------------------------------------------------
        StatusBarText.Text = "Loading iSCSI information...";
        Log("Loading global iSCSI session overview...");
        await LoadSessionsAsync();

        // ------------------------------------------------------------
        // 4) REFRESCAR STATUSVIEW
        // ------------------------------------------------------------
        if (StatusPanel is StatusView status)
        {
            Log("Refreshing StatusView...");
            await status.RefreshStatus();
        }

        StatusBarText.Text = "Ready.";
        Log("Initialization completed. System ready.");
    }

    // ============================================================
    // ESPERAR A QUE ISCSID ESTÉ LISTO
    // ============================================================
    private async Task WaitForDaemonReady()
    {
        for (int i = 0; i < 40; i++)
        {
            var result = ShellHelper.EjecutarComoRoot(
                "systemctl show -p ActiveState iscsid"
            );

            if (result.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase))
            {
                Log("iscsid is active.");
                return;
            }

            await Task.Delay(120);
        }

        LogService.Error("iscsid did not reach active state within timeout.");
    }

    // ============================================================
    // DIÁLOGO DE CONTRASEÑA
    // ============================================================
    private async Task SolicitarPassword()
    {
        var dialog = new PasswordDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        Credenciales.AdminPassword = await dialog.ShowDialog<string?>(this) ?? string.Empty;
    }

    // ============================================================
    // DIÁLOGO DE CONTRASEÑA INCORRECTA
    // ============================================================
    private async Task MostrarPasswordIncorrecta()
    {
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
    }

    // ============================================================
    // CARGAR SESSIONS
    // ============================================================
    private async Task LoadSessionsAsync()
    {
        using (LoadingService.Show("Loading sessions..."))
        {
            if (SessionsPanel is SessionsView sessions)
                await sessions.CargarSesiones();

            Log("Sessions loaded.");
        }
    }

    // ============================================================
    // MÉTODOS ISCSI
    // ============================================================
    public async Task DiscoverTargets(string ip)
    {
        using (LoadingService.Show("Discovering targets..."))
        {
            Log($"Discovering targets on portal {ip}...");
            await IscsiHelper.Descubrir(ip);
        }
    }

    public async Task ConnectTarget(IscsiDestino d)
    {
        using (LoadingService.Show("Connecting to target..."))
        {
            Log($"Connecting to target {d.Iqn}...");
            await IscsiHelper.Conectar(d);
            await Task.Delay(1500);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task DisconnectTarget(IscsiDestino d)
    {
        using (LoadingService.Show("Disconnecting target..."))
        {
            Log($"Disconnecting target {d.Iqn}...");
            await IscsiHelper.Desconectar(d);
            await Task.Delay(1500);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task InitializeDisk(IscsiDestino d, string label, string fsType)
    {
        using (LoadingService.Show("Initializing disk..."))
        {
            Log($"Initializing disk for {d.Iqn} with FS {fsType} and label {label}...");
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

            Log($"Opened mountpoint: {destino.MountPoint}");
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to open folder: {ex.Message}");
        }
    }

    // ============================================================
    // CONFIG WINDOW
    // ============================================================
    private async void OnOpenConfig(object? sender, RoutedEventArgs e)
    {
        var win = new ConfigWindow();
        await win.ShowDialog(this);

        Log("Configuration updated.");
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
        await LoadSessionsAsync();
        StatusBarText.Text = "Sessions overview";
    }

    private void onTabLogsClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "Latest iSCSI logs";
    }
}
