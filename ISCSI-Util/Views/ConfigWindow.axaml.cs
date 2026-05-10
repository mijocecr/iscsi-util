using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Services;
using System;
using System.IO;

namespace ISCSI_Util.Views;

public partial class ConfigWindow : Window
{
    public ConfigWindow()
    {
        InitializeComponent();

        // ============================
        // CARGAR CONFIGURACIÓN
        // ============================
        LoadConfig();

        BtnBrowseMount.Click += OnBrowseMount;
        BtnBrowseLog.Click += OnBrowseLog;
        BtnSave.Click += OnSave;
        BtnCancel.Click += (_, _) => Close();
    }

    private void LoadConfig()
    {
        // Seleccionar el permiso actual en el ComboBox
        foreach (ComboBoxItem item in PermCombo.Items)
        {
            if (item.Tag?.ToString() == ConfigManager.DefaultPermissions.ToString())
            {
                PermCombo.SelectedItem = item;
                break;
            }
        }

        MountBaseBox.Text = ConfigManager.MountBasePath;
        LogPathBox.Text = ConfigManager.LogPath;
        VerboseCheck.IsChecked = ConfigManager.Verbose;
    }

    private async void OnBrowseMount(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        var result = await dlg.ShowAsync(this);
        if (!string.IsNullOrWhiteSpace(result))
            MountBaseBox.Text = result;
    }

    private async void OnBrowseLog(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        var result = await dlg.ShowAsync(this);
        if (!string.IsNullOrWhiteSpace(result))
            LogPathBox.Text = result;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // ============================
        // GUARDAR PERMISOS
        // ============================
        if (PermCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int perm))
        {
            ConfigManager.DefaultPermissions = perm;
        }
        else
        {
            // fallback seguro
            ConfigManager.DefaultPermissions = 755;
        }

        // ============================
        // GUARDAR RESTO DE CONFIG
        // ============================
        ConfigManager.MountBasePath = MountBaseBox.Text?.Trim() ?? "";
        ConfigManager.LogPath = LogPathBox.Text?.Trim() ?? "";
        ConfigManager.Verbose = VerboseCheck.IsChecked ?? false;

        ConfigManager.Save();
        Close();
    }
}
