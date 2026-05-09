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
        InitializeComponent();
        _destino = destino;

        CancelBtn.Click += (_, _) => Close();
        ApplyBtn.Click += ApplyChanges;
    }

    private async void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string label = LabelBox.Text?.Trim() ?? "";
        string fs = (FsCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(fs))
            return;

        // Validar soporte del FS
        if (!IscsiHelper.SoportaFs(fs))
        {
            await MessageBox("Filesystem not supported on this system.");
            return;
        }

        // ⭐ USAR TU DIÁLOGO DE CARGA REAL ⭐
        using (LoadingService.Show($"Initializing disk ({fs})..."))
        {
            // Inicializar
            await IscsiHelper.InicializarDestino(_destino, label, fs);

            // Refrescar estado real
            _destino.Persistir = IscsiHelper.DetectarPersistencia(_destino);
            IscsiHelper.DetectarChap(_destino);
        }

        Close();
    }

    private async Task MessageBox(string msg)
    {
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
