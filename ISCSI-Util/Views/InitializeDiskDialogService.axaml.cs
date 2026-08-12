using System;
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
        LogService.Debug($"[INIT_DISK] Initializing dialog for {destino.Iqn} ({destino.Ip})");

        InitializeComponent();
        _destino = destino;

        CancelBtn.Click += (_, _) =>
        {
            LogService.Debug("[INIT_DISK] Cancelled by user.");
            Close(false); // Retorna false si el usuario cancela
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
            LogService.Debug("[INIT_DISK] Incomplete fields, operation cancelled.");
            return;
        }

        if (!IscsiHelper.SoportaFs(fs))
        {
            LogService.Error($"[INIT_DISK] Filesystem '{fs}' not supported.");
            await MessageBox("Filesystem not supported on this system.");
            return;
        }

        // Lock controls to prevent double submission
        ApplyBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        LogService.Write($"[INIT_DISK] Initializing disk {_destino.Iqn} with FS={fs}, Label={label}");

        try
        {
            using (LoadingService.Show($"Initializing disk ({fs})..."))
            {
                // 1) Formatear y montar el destino (ya maneja la asignación de dispositivo y montaje)
                await IscsiHelper.InicializarDestino(_destino, label, fs);
                LogService.Debug("[INIT_DISK] Initialization & mount completed.");

                // 2) Actualizar la información completa en el modelo
                await IscsiHelper.CompletarInformacionDestino(_destino, 0);
                LogService.Debug("[INIT_DISK] Target info refreshed after initialization.");

                // 3) Detectar persistencia y CHAP
                _destino.Persistir = IscsiHelper.DetectarPersistencia(_destino);
                IscsiHelper.DetectarChap(_destino);
            }

            LogService.Debug("[INIT_DISK] Closing dialog with success result.");
            
            // RETORNAR true PARA INDICA EXITO A TARGETSVIEW
            Close(true);
        }
        catch (Exception ex)
        {
            LogService.Error($"[INIT_DISK] ERROR during initialization: {ex.Message}");
            await MessageBox($"Failed to initialize disk:\n{ex.Message}");
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
    }

    private async Task MessageBox(string msg)
    {
        LogService.Debug($"[INIT_DISK] MessageBox: {msg}");

        var dlg = new Window
        {
            Width = 320,
            Height = 160,
            Title = "Information",
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };

        panel.Children.Add(new TextBlock
        {
            Text = msg,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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