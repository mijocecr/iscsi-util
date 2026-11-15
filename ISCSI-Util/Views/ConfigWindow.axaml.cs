using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ISCSI_Util.Views;

public partial class ConfigWindow : Window
{
    private bool _cleaningGhosts;

    /// <summary>True si la limpieza eliminó al menos un registro fantasma.</summary>
    public bool GhostCleanupPerformed { get; private set; }

    public ConfigWindow()
    {
        InitializeComponent();

        // ============================
        // CARGAR CONFIGURACIÓN
        // ============================
        LoadConfig();

        BtnBrowseMount.Click += OnBrowseMount;
        BtnBrowseLog.Click += OnBrowseLog;
        BtnCleanGhosts.Click += OnCleanGhosts;
        BtnSave.Click += OnSave;
        BtnCancel.Click += (_, _) => Close(false);
    }

    private void LoadConfig()
    {
        // Seleccionar el permiso actual en el ComboBox
        if (PermCombo.Items is not null)
        {
            foreach (var element in PermCombo.Items)
            {
                if (element is ComboBoxItem item &&
                    item.Tag?.ToString() == ConfigManager.DefaultPermissions.ToString())
                {
                    PermCombo.SelectedItem = item;
                    break;
                }
            }
        }

        MountBaseBox.Text = ConfigManager.MountBasePath;
        LogPathBox.Text = ConfigManager.LogPath;
        VerboseCheck.IsChecked = ConfigManager.Verbose;
    }

    private async void OnBrowseMount(object? sender, RoutedEventArgs e)
    {
        var result = await PickFolderAsync("Select mount base path");
        if (!string.IsNullOrWhiteSpace(result))
            MountBaseBox.Text = result;
    }

    private async void OnBrowseLog(object? sender, RoutedEventArgs e)
    {
        var result = await PickFolderAsync("Select log path");
        if (!string.IsNullOrWhiteSpace(result))
            LogPathBox.Text = result;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    // ============================================================
    // LIMPIEZA DE TARGETS FANTASMA
    // ============================================================

    private async void OnCleanGhosts(object? sender, RoutedEventArgs e)
    {
        if (_cleaningGhosts)
            return;

        _cleaningGhosts = true;
        BtnCleanGhosts.IsEnabled = false;
        var previousContent = BtnCleanGhosts.Content;
        BtnCleanGhosts.Content = "Scanning…";

        try
        {
            IscsiGhostCleaner.ScanResult scan;

            try
            {
                var progress = new Progress<string>(message =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => LoadingService.SetMessage(message)));

                using (LoadingService.Show("Scanning for ghost targets…"))
                {
                    scan = await SystemOperationLock.RunAsync(
                        () => IscsiGhostCleaner.ScanAsync(progress));
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[CONFIG] Ghost scan failed: {ex.Message}");
                await ShowMessageAsync("Ghost target scan", $"Scan failed:\n{ex.Message}");
                return;
            }

            if (!scan.HasAny)
            {
                await ShowMessageAsync(
                    "Ghost target scan",
                    $"No ghost targets found.\n\n" +
                    $"Checked {scan.NodeCount} node record(s) and {scan.SessionCount} active session(s); " +
                    $"all of them match the targets currently exported by their portals.");
                return;
            }

            var selection = await ConfirmRemovalAsync(scan);
            if (selection == null || selection.Count == 0)
                return;

            BtnCleanGhosts.Content = "Removing…";

            IscsiGhostCleaner.CleanResult result;

            try
            {
                var progress = new Progress<string>(message =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => LoadingService.SetMessage(message)));

                using (LoadingService.Show("Removing ghost targets…"))
                {
                    result = await SystemOperationLock.RunAsync(
                        () => IscsiGhostCleaner.CleanAsync(selection, progress));
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[CONFIG] Ghost removal failed: {ex.Message}");
                await ShowMessageAsync("Ghost target removal", $"Removal failed:\n{ex.Message}");
                return;
            }

            LogService.Write(
                $"[CONFIG] Ghost cleanup finished: removed={result.Removed.Count}, " +
                $"skippedMounted={result.SkippedMounted.Count}, failed={result.Failed.Count}");

            if (result.Removed.Count > 0)
                GhostCleanupPerformed = true;

            await ShowMessageAsync("Ghost target removal", BuildSummary(result));
        }
        finally
        {
            BtnCleanGhosts.Content = previousContent;
            BtnCleanGhosts.IsEnabled = true;
            _cleaningGhosts = false;
        }
    }

    private async Task<List<IscsiGhostCleaner.GhostTarget>?> ConfirmRemovalAsync(
        IscsiGhostCleaner.ScanResult scan)
    {
        var dialog = new Window
        {
            Width = 580,
            Height = 440,
            Title = "Remove ghost targets",
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10
        };

        var summary = new TextBlock
        {
            Text = $"The following node records will be deleted. Their sessions are logged out first; " +
                   $"targets with mounted volumes are skipped.\n\n" +
                   $"Confirmed ghosts: {scan.Confirmed.Count}    " +
                   $"Records on unreachable portals: {scan.Unreachable.Count}",
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(summary);

        var list = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 230,
            FontFamily = new FontFamily("monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap
        };
        panel.Children.Add(list);

        CheckBox? includeUnreachable = null;

        if (scan.Unreachable.Count > 0)
        {
            includeUnreachable = new CheckBox
            {
                Content = $"Also remove {scan.Unreachable.Count} record(s) from unreachable " +
                          $"portal(s): {string.Join(", ", scan.UnreachablePortals)}",
                IsChecked = false
            };
            panel.Children.Add(includeUnreachable);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "SteamButton" }
        };

        var remove = new Button
        {
            Content = "Remove",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#3A1518")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D9534F")),
            BorderThickness = new Thickness(2),
            Foreground = new SolidColorBrush(Color.Parse("#FF9A95"))
        };

        List<IscsiGhostCleaner.GhostTarget> Selected() =>
            scan.Confirmed
                .Concat(includeUnreachable?.IsChecked == true ? scan.Unreachable : Enumerable.Empty<IscsiGhostCleaner.GhostTarget>())
                .ToList();

        void RefreshList()
        {
            var text = new StringBuilder();

            if (scan.Confirmed.Count > 0)
            {
                text.AppendLine("Confirmed ghosts (the portal no longer exports them):");
                foreach (var ghost in scan.Confirmed)
                    text.AppendLine("  " + Describe(ghost));
                text.AppendLine();
            }

            if (includeUnreachable?.IsChecked == true && scan.Unreachable.Count > 0)
            {
                text.AppendLine("Unreachable portals (the portal did not respond):");
                foreach (var ghost in scan.Unreachable)
                    text.AppendLine("  " + Describe(ghost));
            }

            list.Text = text.ToString().TrimEnd();
            remove.IsEnabled = Selected().Count > 0;
        }

        remove.Click += (_, _) => dialog.Close(Selected());
        cancel.Click += (_, _) => dialog.Close(null);

        if (includeUnreachable != null)
            includeUnreachable.IsCheckedChanged += (_, _) => RefreshList();

        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        RefreshList();

        return await dialog.ShowDialog<List<IscsiGhostCleaner.GhostTarget>?>(this);
    }

    private static string Describe(IscsiGhostCleaner.GhostTarget ghost)
    {
        var flags = new List<string>();

        if (ghost.Connected)
            flags.Add("connected");
        if (ghost.Mounted)
            flags.Add("mounted, will be skipped");
        if (ghost.PortalUnreachable)
            flags.Add("portal unreachable");

        return flags.Count == 0
            ? $"{ghost.Portal}  {ghost.Iqn}"
            : $"{ghost.Portal}  {ghost.Iqn}  [{string.Join(", ", flags)}]";
    }

    private static string BuildSummary(IscsiGhostCleaner.CleanResult result)
    {
        var text = new StringBuilder();

        text.AppendLine($"Removed: {result.Removed.Count}");
        text.AppendLine($"Skipped (mounted): {result.SkippedMounted.Count}");
        text.AppendLine($"Failed: {result.Failed.Count}");

        if (result.Removed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Removed:");
            foreach (var ghost in result.Removed)
                text.AppendLine($"  {ghost.Portal}  {ghost.Iqn}");
        }

        if (result.SkippedMounted.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Skipped because they are still mounted:");
            foreach (var ghost in result.SkippedMounted)
                text.AppendLine($"  {ghost.Portal}  {ghost.Iqn}");
        }

        if (result.Failed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Failed:");
            foreach (var failure in result.Failed)
                text.AppendLine($"  {failure.Target.Portal}  {failure.Target.Iqn}  — {failure.Error}");
        }

        return text.ToString().TrimEnd();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Width = 580,
            Height = 360,
            Title = title,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12
        };

        var body = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap
        };
        panel.Children.Add(body);

        var ok = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "SteamButton" }
        };
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);

        dialog.Content = panel;
        await dialog.ShowDialog(this);
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
        Close(true);
    }
}
