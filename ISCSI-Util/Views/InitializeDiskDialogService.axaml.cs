using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views;

public partial class InitializeDiskDialog : Window
{
    private readonly IscsiDestino _destino;

    public InitializeDiskDialog(IscsiDestino destino)
    {
        LogService.Debug($"[INIT_DISK] Inicializando diálogo para {destino.Iqn} ({destino.Ip})");

        InitializeComponent();
        _destino = destino;

        CancelBtn.Click += (_, _) =>
        {
            LogService.Debug("[INIT_DISK] Cancelado por el usuario.");
            Close();
        };

        ApplyBtn.Click += ApplyChanges;
    }

    private async void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string label = LabelBox.Text?.Trim() ?? "";
        string fs = (FsCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        LogService.Debug($"[INIT_DISK] ApplyChanges → label='{label}', fs='{fs}'");

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(fs))
        {
            LogService.Debug("[INIT_DISK] Campos incompletos, operación cancelada.");
            return;
        }

        // Validar soporte del FS
        if (!IscsiHelper.SoportaFs(fs))
        {
            LogService.Error($"[INIT_DISK] Filesystem '{fs}' no soportado.");
            await MessageBox("Filesystem not supported on this system.");
            return;
        }

        LogService.Write($"[INIT_DISK] Inicializando disco {_destino.Iqn} con FS={fs}, Label={label}");

        // ⭐ USAR TU DIÁLOGO DE CARGA REAL ⭐
        using (LoadingService.Show($"Initializing disk ({fs})..."))
        {
            try
            {
                // Inicializar
                await IscsiHelper.InicializarDestino(_destino, label, fs);
                LogService.Debug("[INIT_DISK] Inicialización completada.");

                // Refrescar estado real
                _destino.Persistir = IscsiHelper.DetectarPersistencia(_destino);
                LogService.Debug($"[INIT_DISK] Persistencia detectada: {_destino.Persistir}");

                IscsiHelper.DetectarChap(_destino);
                LogService.Debug("[INIT_DISK] CHAP actualizado tras inicialización.");
            }
            catch (System.Exception ex)
            {
                LogService.Error($"[INIT_DISK] ERROR durante inicialización: {ex.Message}");
            }
        }

        LogService.Debug("[INIT_DISK] Cerrando diálogo.");
        Close();
    }

    private async Task MessageBox(string msg)
    {
        LogService.Debug($"[INIT_DISK] MessageBox: {msg}");

        var dlg = new Window
        {
            Width = 300,
            Height = 150,
            Title = "Info",
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };

        panel.Children.Add(new TextBlock
        {
            Text = msg,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var okBtn = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Width = 80
        };

        okBtn.Click += (_, _) => dlg.Close();

        panel.Children.Add(okBtn);

        dlg.Content = panel;

        await dlg.ShowDialog(this);
    }
}
