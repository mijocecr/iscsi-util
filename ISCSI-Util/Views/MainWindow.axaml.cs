using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;

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
        Title = "iSCSI Management";

        InitializeFakeData();
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

            // sudo incorrecto → stderr contiene "incorrect" o "failure"
            if (!string.IsNullOrWhiteSpace(result.Stderr) &&
                (result.Stderr.Contains("incorrect", StringComparison.OrdinalIgnoreCase) ||
                 result.Stderr.Contains("failure", StringComparison.OrdinalIgnoreCase)))
            {
                await MostrarPasswordIncorrecta();
                continue; // volver a pedir
            }

            if (result.ExitCode != 0)
            {
                StatusBarText.Text = "Admin password validation failed.";
                return;
            }

            break; // contraseña correcta
        }

        // ============================================================
        // 2) Asegurar servicio iscsid
        // ============================================================
        StatusBarText.Text = "Ensuring iSCSI service...";
        ShellHelper.EjecutarComoRoot("systemctl start iscsid");

        // Esperar a que esté realmente listo
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

            string status = result.Stdout.Trim();

            if (status.Contains("Ready", StringComparison.OrdinalIgnoreCase))
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

        var pass = await dialog.ShowDialog<string?>(this);
        Credenciales.AdminPassword = pass ?? string.Empty;
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
    // LOAD SESSIONS
    //---------------------------------------------------------
    private async Task LoadSessionsAsync()
    {
        await Task.Delay(200);
        Log("Loading sessions...");

        SessionsList.Items.Clear();
        SessionsList.Items.Add("iqn.2024-01.com.example:storage02   10.0.0.20   /dev/sdb   Active");

        Log("Sessions loaded.");
    }

    //---------------------------------------------------------
    // DOUBLE TAP → OPEN MOUNTPOINT
    //---------------------------------------------------------
    private void Destino_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        if (item.Tag is not IscsiDestino destino)
            return;

        if (!destino.Conectado || string.IsNullOrWhiteSpace(destino.MountPoint))
            return;

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
        string line = $"[{timestamp}] {message}";

        LogsList.Items.Add(line);
        Debug.WriteLine(line);
    }

    //---------------------------------------------------------
    // FAKE DATA
    //---------------------------------------------------------
    private void InitializeFakeData()
    {
        LogsList.Items.Add("[INFO] UI initialized.");
    }

    private void OnTabStatusClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "System summary";
    }

    private void OnTabTargetClick(object? sender, PointerPressedEventArgs e)
    {
        StatusBarText.Text = "All discoverable targets";
    }
}
