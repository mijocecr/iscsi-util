using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ISCSI_Util.Views;

public partial class TargetsView : UserControl
{
    private readonly List<IscsiDestino> _targets = new();
    private readonly SemaphoreSlim _systemOperationLock = new(1, 1);
    private IscsiDestino? _selected;
    private string _lastValidIp = "";

    public TargetsView()
    {
        InitializeComponent();
        DiscoverButton.IsEnabled = false;

        BtnDeleteNode.Click += OnDeleteNode;
        BtnHeaderUnmount.Click += OnPrimaryHeaderAction; // Botón inteligente unificado
        BtnHeaderOpen.Click += OnHeaderOpen;
        BtnTogglePersist.Click += OnTogglePersist;
    }

    // ============================================================
    // IP VALIDATION
    // ============================================================

    private void PortalBox_TextChanging(object? sender, TextChangingEventArgs e)
    {
        if (sender is not TextBox tb)
            return;

        string input = tb.Text ?? "";

        foreach (char c in input)
        {
            if (!(char.IsDigit(c) || c == '.'))
            {
                int pos = tb.CaretIndex;
                tb.Text = RemoveInvalidChars(input);
                tb.CaretIndex = Math.Max(0, pos - 1);
                return;
            }
        }

        if (input.Count(c => c == '.') > 3)
        {
            int pos = tb.CaretIndex;
            tb.Text = RemoveExtraDots(input);
            tb.CaretIndex = Math.Max(0, pos - 1);
            return;
        }

        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (int.TryParse(p, out int val) && (val < 0 || val > 255))
            {
                tb.Text = _lastValidIp;
                tb.CaretIndex = tb.Text.Length;
                return;
            }
        }

        _lastValidIp = input;
    }

    private void PortalBox_KeyUp(object? sender, KeyEventArgs e)
    {
        DiscoverButton.IsEnabled = EsIpCompletaValida(PortalBox.Text ?? "");
    }

    private bool EsIpCompletaValida(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;

        var parts = ip.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var p in parts)
        {
            if (!int.TryParse(p, out int val) || val < 0 || val > 255)
                return false;
        }

        return true;
    }

    private string RemoveInvalidChars(string s)
    {
        return new string(s.Where(c => char.IsDigit(c) || c == '.').ToArray());
    }

    private string RemoveExtraDots(string s)
    {
        int dotCount = 0;
        var result = new List<char>();

        foreach (char c in s)
        {
            if (c == '.')
            {
                if (dotCount < 3)
                {
                    result.Add('.');
                    dotCount++;
                }
            }
            else
            {
                result.Add(c);
            }
        }

        return new string(result.ToArray());
    }

    // ============================================================
    // DISCOVER TARGETS
    // ============================================================

    private async void DiscoverButton_Click(object? sender, RoutedEventArgs e)
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (!EsIpCompletaValida(portal))
            return;

        List<IscsiDestino> discovered = new();

        using (LoadingService.Show("Discovering targets..."))
        {
            _targets.Clear();
            discovered = await RunSystemOperationAsync(() => IscsiHelper.Descubrir(portal));

            // Scan targets safely
            foreach (var d in discovered)
            {
                try
                {
                    await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(d, 0));
                }
                catch (Exception ex)
                {
                    LogService.Error($"[DISCOVER] Error scanning target: {ex.Message}");
                }
            }

            _targets.AddRange(discovered);
        }

        _selected = _targets.FirstOrDefault();
        await RefreshTargetsList();
    }

    // ============================================================
    // REFRESH LIST & RE-SCAN TARGET STATES
    // ============================================================

    private async Task RefreshTargetsList()
    {
        string? selectedIQN = _selected?.Iqn;

        // Safe scanning
        foreach (var destino in _targets)
        {
            try
            {
                await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(destino, 0));
            }
            catch (Exception ex)
            {
                LogService.Error($"[REFRESH] Error scanning target: {ex.Message}");
            }

            await UpdateTargetStateAsync(destino);
        }

        RebuildTargetsList();

        _selected = _targets.FirstOrDefault(t => t.Iqn == selectedIQN);

        if (_selected != null)
            await LoadTargetDetailsAsync(_selected);
        else
        {
            DetailsInfoPanel.Children.Clear();
            HeaderRow.IsVisible = false;
            HeaderActionsRow.IsVisible = false;
        }
    }

    private async Task UpdateTargetStateAsync(IscsiDestino destino)
    {
        destino.Persistir = await RunSystemOperationAsync(() => IscsiPersistenceManager.Detect(destino));
        destino.InfoCompleta = true;

        bool hasChap = destino.RequiresChap || destino.HasLocalChapConfigured || IscsiPersistenceManager.HasChapConfigured(destino);
        bool hasMutual = destino.RequiresMutualChap || destino.HasLocalMutualConfigured || IscsiPersistenceManager.HasMutualChapConfigured(destino);

        destino.UsaChap = hasChap;
        destino.UsaMutualChap = hasMutual;
    }

    private async Task RunSystemOperationAsync(Func<Task> operation)
    {
        await _systemOperationLock.WaitAsync();
        try
        {
            await Task.Run(operation);
        }
        finally
        {
            _systemOperationLock.Release();
        }
    }

    private async Task<T> RunSystemOperationAsync<T>(Func<Task<T>> operation)
    {
        await _systemOperationLock.WaitAsync();
        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            _systemOperationLock.Release();
        }
    }

    private async Task<T> RunSystemOperationAsync<T>(Func<T> operation)
    {
        await _systemOperationLock.WaitAsync();
        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            _systemOperationLock.Release();
        }
    }

    private void RebuildTargetsList()
    {
        TargetsList.Children.Clear();

        foreach (var destino in _targets)
        {
            TargetsList.Children.Add(CreateTargetCard(destino));
        }
    }

    // ============================================================
    // TARGET CARD
    // ============================================================

    private Control CreateTargetCard(IscsiDestino destino)
    {
        var border = new Border
        {
            Classes = { "SteamCard" },
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 7),
            BorderBrush = (IBrush)Application.Current!.FindResource("SteamBorderStrong")!,
            BorderThickness = new Thickness(1.5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        if (_selected?.Iqn == destino.Iqn)
            border.Classes.Add("selected");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("78, *"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 5
        };

        var icon = new Image
        {
            Source = LoadIcon(GetIconForChap(destino)),
            Stretch = Stretch.Uniform,
            Width = 54,
            Height = 54,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var iconFrame = new Border
        {
            Width = 62,
            Height = 62,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(9),
            BorderBrush = (IBrush)Application.Current!.FindResource("SteamBorder")!,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.White, // <--- Fondo blanco aplicado
            Child = icon,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        grid.Children.Add(iconFrame);
        Grid.SetColumn(iconFrame, 0);
        Grid.SetRowSpan(iconFrame, 2);

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var iqnText = new TextBlock
        {
            Text = GetTargetDisplayName(destino),
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!
        };

        titleRow.Children.Add(iqnText);

        var connectionIndicator = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = destino.Conectado ? Brushes.LimeGreen : Brushes.Gray,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        titleRow.Children.Add(connectionIndicator);
        Grid.SetColumn(connectionIndicator, 1);

        grid.Children.Add(titleRow);
        Grid.SetColumn(titleRow, 1);
        Grid.SetRow(titleRow, 0);

        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        if (destino.RequiresMutualChap && !destino.HasLocalMutualConfigured)
        {
            var mutualBtn = new Button
            {
                Content = "Mutual",
                Classes = { "SteamButton" },
                Width = 90,
                Height = 28
            };

            mutualBtn.Click += async (_, _) =>
            {
                if (this.GetVisualRoot() is Window owner)
                {
                    var dlg = new MutualChapDialog(destino);
                    await dlg.ShowDialog(owner);
                    await RefreshTargetsList();
                }
            };

            btnRow.Children.Add(mutualBtn);
        }
        else if (destino.RequiresChap && !destino.HasLocalChapConfigured)
        {
            var chapBtn = new Button
            {
                Content = "Configure CHAP",
                Classes = { "SteamButton" },
                Width = 130,
                Height = 28
            };

            chapBtn.Click += async (_, _) =>
            {
                if (this.GetVisualRoot() is Window owner)
                {
                    var dlg = new ChapDialog(destino);
                    await dlg.ShowDialog(owner);
                    await RefreshTargetsList();
                }
            };

            btnRow.Children.Add(chapBtn);
        }

        if (CanInitializeTarget(destino))
        {
            var initBtn = new Button
            {
                Content = "INIT",
                Width = 90,
                Height = 28,
                Background = Brushes.Orange,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                Classes = { "SteamButton" }
            };

            initBtn.Click += async (_, _) =>
            {
                if (this.GetVisualRoot() is Window owner)
                {
                    var current = _targets.FirstOrDefault(t => t.Iqn == destino.Iqn);
                    if (current == null)
                    {
                        await RefreshTargetsList();
                        return;
                    }

                    try
                    {
                        using (LoadingService.Show("Verifying target..."))
                        {
                            await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(current, 0));
                            await UpdateTargetStateAsync(current);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"[INIT] Unable to verify target: {ex.Message}");
                        await RefreshTargetsList();
                        return;
                    }

                    if (!CanInitializeTarget(current))
                    {
                        await RefreshTargetsList();
                        return;
                    }

                    var dlg = new InitializeDiskDialog(current);
                    bool? success = await dlg.ShowDialog<bool>(owner);

                    if (success == true)
                    {
                        using (LoadingService.Show("Updating target info..."))
                        {
                            try
                            {
                                await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(current, 0));
                            }
                            catch (Exception ex)
                            {
                                LogService.Error($"[INIT] {ex.Message}");
                            }
                        }
                    }

                    await RefreshTargetsList();
                }
            };

            btnRow.Children.Add(initBtn);
        }

        grid.Children.Add(btnRow);
        Grid.SetColumn(btnRow, 1);
        Grid.SetRow(btnRow, 1);

        border.PointerPressed += async (_, e) =>
        {
            e.Handled = true;

            var selected = _targets.FirstOrDefault(t => t.Iqn == destino.Iqn);
            if (selected == null)
                return;

            _selected = selected;

            foreach (var child in TargetsList.Children)
            {
                if (child is Border b)
                    b.Classes.Remove("selected");
            }

            border.Classes.Add("selected");

            using (LoadingService.Show("Reading target info..."))
            {
                try
                {
                    await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(selected, 0));
                    await UpdateTargetStateAsync(selected);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[SELECT] {ex.Message}");
                }
            }

            RebuildTargetsList();
            if (_targets.Any(t => t.Iqn == selected.Iqn))
                await LoadTargetDetailsAsync(selected);
        };

        border.Child = grid;
        return border;
    }

    // ============================================================
    // TARGET DETAILS PANEL
    // ============================================================

    private async Task LoadTargetDetailsAsync(IscsiDestino destino)
    {
        _selected = _targets.FirstOrDefault(t => t.Iqn == destino.Iqn);
        if (_selected == null)
            return;

        destino = _selected;

        DetailsInfoPanel.Children.Clear();
        HeaderRow.IsVisible = true;
        HeaderActionsRow.IsVisible = true;
        BtnDeleteNode.IsVisible = true;
        TargetDetailsTitle.Text = GetTargetDisplayName(destino);

        bool persistente = destino.Persistir;
        BtnTogglePersist.IsEnabled = destino.Conectado;
        BtnTogglePersist.Content = persistente ? "No Autostart" : "Autostart";

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80, *"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 4
        };

        void AddRow(string label, string value, int row, IBrush? color = null)
        {
            var lbl = new TextBlock
            {
                Text = label,
                Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetRow(lbl, row);
            infoGrid.Children.Add(lbl);

            var val = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                Foreground = color ?? (IBrush)Application.Current!.FindResource("SteamText")!
            };
            Grid.SetColumn(val, 1);
            Grid.SetRow(val, row);
            infoGrid.Children.Add(val);
        }

        AddRow("IQN:", destino.Iqn, 0);
        AddRow("Portal:", destino.Ip, 1);
        AddRow("Status:",
            destino.Conectado ? "Connected" : "Disconnected",
            2,
            destino.Conectado
                ? (IBrush)Application.Current!.FindResource("SteamGreen")!
                : Brushes.OrangeRed
        );
        AddRow("Filesystem:", destino.TieneFilesystem ? $"Yes ({destino.FsType})" : "No", 3);
        AddRow("Capacity:", destino.SizeGb > 0 ? $"{destino.SizeGb} GB" : "-", 4);

        DetailsInfoPanel.Children.Add(infoGrid);
        
        DetailsIcon.Source = LoadIcon(GetIconForChap(destino));

        var mountsRaw = ShellHelper.EjecutarComoRoot("mount").Stdout ?? "";
        bool mounted = destino.Conectado &&
                       !string.IsNullOrWhiteSpace(destino.MountPoint) &&
                       mountsRaw.Contains($"on {destino.MountPoint} ", StringComparison.OrdinalIgnoreCase);

        if (!destino.Conectado)
        {
            BtnHeaderUnmount.Content = "Connect";
            BtnHeaderUnmount.IsVisible = true;
        }
        else if (!mounted && IscsiHelper.EsFilesystemMontable(destino.FsType))
        {
            BtnHeaderUnmount.Content = "Mount";
            BtnHeaderUnmount.IsVisible = true;
        }
        else if (mounted)
        {
            BtnHeaderUnmount.Content = "Unmount";
            BtnHeaderUnmount.IsVisible = true;
        }
        else
        {
            BtnHeaderUnmount.IsVisible = false;
        }

        BtnHeaderOpen.IsVisible = mounted &&
                                  !string.IsNullOrWhiteSpace(destino.MountPoint) &&
                                  Directory.Exists(destino.MountPoint);

        await Task.CompletedTask;
    }

    private static bool CanInitializeTarget(IscsiDestino destino) =>
        destino.Conectado &&
        !destino.TieneFilesystem &&
        !string.IsNullOrWhiteSpace(destino.DevicePath);

    private static string GetTargetDisplayName(IscsiDestino destino) =>
        destino.Iqn.Contains(':')
            ? destino.Iqn[(destino.Iqn.LastIndexOf(':') + 1)..]
            : destino.Iqn;

    // ============================================================
    // PERSISTENCE & HEADER EVENTS
    // ============================================================

    private async void OnTogglePersist(object? sender, RoutedEventArgs e)
    {
        var selected = _selected;
        if (selected == null)
            return;

        bool persistente = await RunSystemOperationAsync(() => IscsiPersistenceManager.Detect(selected));

        using (LoadingService.Show(persistente ? "Removing persistence..." : "Applying persistence..."))
        {
            await Task.Delay(50);

            try
            {
                await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(selected, 0));

                if (!persistente)
                {
                    await RunSystemOperationAsync(() => IscsiPersistenceManager.ApplyAsync(selected));
                }
                else
                {
                    await RunSystemOperationAsync(() => IscsiPersistenceManager.RemoveAsync(selected));
                    await AutoRediscoverAfterPersistenceRemoval();
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[PERSISTENCE] {ex.Message}");
            }
        }

        await RefreshTargetsList();
    }

    private async Task AutoRediscoverAfterPersistenceRemoval()
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (!EsIpCompletaValida(portal))
            return;

        try
        {
            var discovered = await RunSystemOperationAsync(() => IscsiHelper.Descubrir(portal));

            foreach (var d in discovered)
            {
                try
                {
                    await RunSystemOperationAsync(() => IscsiHelper.CompletarInformacionDestino(d, 0));
                }
                catch (Exception ex)
                {
                    LogService.Error($"[AUTO-DISCOVER] {ex.Message}");
                }
            }

            _targets.Clear();
            _targets.AddRange(discovered);

            _selected = _targets.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogService.Error($"[AUTO-DISCOVER] Discover failed: {ex.Message}");
        }
    }

    // Botón inteligente unificado en la cabecera (Conectar, Montar o Desmontar según estado) - Protegido contra fallos CHAP
    private async void OnPrimaryHeaderAction(object? sender, RoutedEventArgs e)
    {
        var selected = _selected;
        if (selected == null)
            return;

        var mountsRaw = ShellHelper.EjecutarComoRoot("mount").Stdout ?? "";
        var mounted = selected.Conectado &&
                      !string.IsNullOrWhiteSpace(selected.MountPoint) &&
                      mountsRaw.Contains($"on {selected.MountPoint} ", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!selected.Conectado)
            {
                using (LoadingService.Show("Connecting..."))
                {
                    await RunSystemOperationAsync(() => IscsiHelper.Conectar(selected));
                }
            }
            else if (!mounted && IscsiHelper.EsFilesystemMontable(selected.FsType))
            {
                using (LoadingService.Show("Mounting..."))
                {
                    await RunSystemOperationAsync(() => IscsiHelper.MontarDestino(selected));
                }
            }
            else if (mounted)
            {
                using (LoadingService.Show("Unmounting..."))
                {
                    await RunSystemOperationAsync(() => IscsiHelper.DesmontarDestino(selected));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[PRIMARY ACTION] Error en la operación del target: {ex.Message}");
        }

        await RefreshTargetsList();
    }

    private void OnHeaderOpen(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || string.IsNullOrEmpty(_selected.MountPoint) || !Directory.Exists(_selected.MountPoint))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{_selected.MountPoint}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            LogService.Error($"[TARGETS] Error opening directory: {ex.Message}");
        }
    }

    private async void OnDeleteNode(object? sender, RoutedEventArgs e)
    {
        var selected = _selected;
        if (selected == null)
            return;

        using (LoadingService.Show("Deleting target node..."))
        {
            try
            {
                await RunSystemOperationAsync(() => IscsiHelper.Desconectar_Borrar(selected));
            }
            catch (Exception ex)
            {
                LogService.Error($"[DELETE] {ex.Message}");
            }
        }

        _targets.Remove(selected);
        if (_selected?.Iqn == selected.Iqn)
            _selected = null;

        await RefreshTargetsList();
    }

    // ============================================================
    // ASSETS / ICONS
    // ============================================================

    private string GetIconForChap(IscsiDestino d)
    {
        if (d.Conectado && !d.TieneFilesystem && !string.IsNullOrWhiteSpace(d.DevicePath))
            return "avares://ISCSI-Util/Assets/Icons/no-fs.jpeg";

        bool usaMutualReal = d.UsaMutualChap || d.HasLocalMutualConfigured || IscsiPersistenceManager.HasMutualChapConfigured(d);
        bool usaChapReal = d.UsaChap || d.HasLocalChapConfigured || IscsiPersistenceManager.HasChapConfigured(d);

        if (usaMutualReal)
            return "avares://ISCSI-Util/Assets/Icons/chap-mutual.png";

        if (usaChapReal)
            return "avares://ISCSI-Util/Assets/Icons/chap.png";

        return "avares://ISCSI-Util/Assets/Icons/no-chap.png";
    }

    private Bitmap? LoadIcon(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            LogService.Error($"[ICON] Failed to load '{uri}': {ex.Message}");
            return null;
        }
    }
}