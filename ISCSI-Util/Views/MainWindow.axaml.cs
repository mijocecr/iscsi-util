using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

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

    //---------------------------------------------------------
    // MAIN STATUS BAR
    //---------------------------------------------------------
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

    //---------------------------------------------------------
    // OnOpened → flujo robusto
    //---------------------------------------------------------
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        StatusBarText.Text = "Initializing...";
        await Task.Delay(300);

        // ============================================================
        // 1) BUCLE DE CONTRASEÑA
        // ============================================================
        while (true)
        {
            await SolicitarPassword();

            if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
            {
                StatusBarText.Text = "Initialization aborted.";
                return;
            }

            StatusBarText.Text = "Validating password...";
            var result = ShellHelper.EjecutarComoRoot("echo OK");

            if (!string.IsNullOrWhiteSpace(result.Stderr) &&
                (result.Stderr.Contains("incorrect", StringComparison.OrdinalIgnoreCase) ||
                 result.Stderr.Contains("failure", StringComparison.OrdinalIgnoreCase)))
            {
                await MostrarPasswordIncorrecta();
                continue;
            }

            if (result.ExitCode != 0)
            {
                StatusBarText.Text = "Admin password validation failed.";
                return;
            }

            break;
        }

        // ============================================================
        // 2) Asegurar servicio iscsid
        // ============================================================
        StatusBarText.Text = "Ensuring iSCSI service...";
        ShellHelper.EjecutarComoRoot("systemctl start iscsid");

        await Task.Delay(200);
        await WaitForDaemonReady();

        // ============================================================
        // 3) Cargar sesiones
        // ============================================================
        StatusBarText.Text = "Loading iSCSI information...";
        await LoadSessionsAsync();

        await Task.Delay(200);

        // ============================================================
        // 4) REFRESCAR STATUSVIEW
        // ============================================================
        if (StatusPanel is StatusView status)
            await status.RefreshStatus();

        StatusBarText.Text = "Ready.";
    }

    //---------------------------------------------------------
    // ESPERAR A QUE ISCSID ESTÉ LISTO
    //---------------------------------------------------------
    private async Task WaitForDaemonReady()
    {
        for (int i = 0; i < 30; i++)
        {
            var result = ShellHelper.EjecutarComoRoot(
                "systemctl show -p StatusText iscsid"
            );

            if (result.Stdout.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(100);
        }

        Log("[WARN] iscsid did not report Ready within timeout.");
    }

    //---------------------------------------------------------
    // PASSWORD DIALOG
    //---------------------------------------------------------
    private async Task SolicitarPassword()
    {
        var dialog = new PasswordDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        Credenciales.AdminPassword = await dialog.ShowDialog<string?>(this) ?? string.Empty;
    }

    private async Task MostrarPasswordIncorrecta()
    {
        var dialog = new Window
        {
            Width = 320,
            Height = 140,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Authentication error",
            Content = new StackPanel
            {
                Margin = new Thickness(15),
                Children =
                {
                    new TextBlock
                    {
                        Text = "The administrator password is incorrect.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Button
                    {
                        Content = "OK",
                        Width = 80,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Margin = new Thickness(0,10,0,0)
                    }
                }
            }
        };

        if (dialog.Content is StackPanel sp &&
            sp.Children[1] is Button btn)
        {
            btn.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(this);
    }

    //---------------------------------------------------------
    // LOAD SESSIONS (con LoadingDialog)
    //---------------------------------------------------------
    private async Task LoadSessionsAsync()
    {
        using (LoadingService.Show("Loading sessions..."))
        {
            await Task.Delay(200);

            SessionsList.Items.Clear();
            SessionsList.Items.Add("iqn.2024-01.com.example:storage02   10.0.0.20   /dev/sdb   Active");

            Log("Sessions loaded.");
        }
    }

  

    //---------------------------------------------------------
    // MÉTODOS ICSI (limpios)
    //---------------------------------------------------------
    public async Task DiscoverTargets(string ip)
    {
        using (LoadingService.Show("Discovering targets..."))
        {
            await IscsiHelper.Descubrir(ip);
        }
    }

    public async Task ConnectTarget(IscsiDestino d)
    {
        using (LoadingService.Show("Connecting to target..."))
        {
            await IscsiHelper.Conectar(d);
            await Task.Delay(3000);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task DisconnectTarget(IscsiDestino d)
    {
        using (LoadingService.Show("Disconnecting target..."))
        {
            await IscsiHelper.Desconectar(d);
            await Task.Delay(3000);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    public async Task InitializeDisk(IscsiDestino d, string label, string fsType)
    {
        using (LoadingService.Show("Initializing disk..."))
        {
            await IscsiHelper.InicializarDestino(d, label, fsType);
            await Task.Delay(3000);

            if (StatusPanel is StatusView status)
                await status.RefreshStatus();
        }
    }

    //---------------------------------------------------------
    // DOUBLE TAP → OPEN MOUNTPOINT
    //---------------------------------------------------------
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
            Log($"[ERROR] Failed to open folder: {ex.Message}");
        }
    }

    //---------------------------------------------------------
    // LOGGING
    //---------------------------------------------------------
    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogsList.Items.Add($"[{timestamp}] {message}");
    }

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
}
