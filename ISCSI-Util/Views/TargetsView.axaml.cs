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
using System.Threading.Tasks;

namespace ISCSI_Util.Views;

public partial class TargetsView : UserControl
{
    private readonly List<IscsiDestino> _targets = new();
    private IscsiDestino? _selected;
    private string _lastValidIp = "";

    public TargetsView()
    {
        InitializeComponent();
        DiscoverButton.IsEnabled = false;

        BtnDeleteNode.Click += OnDeleteNode;
        BtnHeaderUnmount.Click += OnHeaderUnmount;
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
            discovered = await IscsiHelper.Descubrir(portal);

            // Scan targets in background immediately to detect active mounts and filesystems
            await Task.Run(async () =>
            {
                foreach (var d in discovered)
                {
                    await IscsiHelper.CompletarInformacionDestino(d, 0);
                }
            });

            _targets.AddRange(discovered);
        }

        if (_targets.Count > 0)
        {
            _selected = _targets[0];
        }
        else
        {
            _selected = null;
        }

        await RefreshTargetsList();
    }

    // ============================================================
    // REFRESH LIST & RE-SCAN TARGET STATES
    // ============================================================

    private async Task RefreshTargetsList()
    {
        string? selectedIQN = _selected?.Iqn;

        // 1. Background scanning to update target state (mounts, filesystems, persistence)
        await Task.Run(async () =>
        {
            foreach (var destino in _targets)
            {
                // Force state resolution for all targets to detect mount status accurately
                await IscsiHelper.CompletarInformacionDestino(destino, 0);

                destino.Persistir = IscsiPersistenceManager.Detect(destino);
                destino.InfoCompleta = true;
                destino.UsaChap = destino.RequiresChap || destino.HasLocalChapConfigured;
                destino.UsaMutualChap = destino.RequiresMutualChap || destino.HasLocalMutualConfigured;
            }
        });

        // 2. Render UI on main thread
        TargetsList.Children.Clear();

        foreach (var destino in _targets)
        {
            TargetsList.Children.Add(CreateTargetCard(destino));
        }

        if (selectedIQN != null)
        {
            _selected = _targets.FirstOrDefault(t => t.Iqn == selectedIQN);
        }

        if (_selected != null)
        {
            await LoadTargetDetailsAsync(_selected);
        }
        else
        {
            DetailsInfoPanel.Children.Clear();
            HeaderRow.IsVisible = false;
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
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = (IBrush)Application.Current!.FindResource("SteamBorderStrong")!,
            BorderThickness = new Thickness(1.4)
        };

        if (_selected?.Iqn == destino.Iqn)
            border.Classes.Add("selected");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("60, *"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 6
        };

        var icon = new Image
        {
            Source = LoadIcon("avares://ISCSI-Util/Assets/Icons/target.jpeg"),
            Stretch = Stretch.Uniform,
            MaxWidth = 90,
            MaxHeight = 90,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 10, 0)
        };

        grid.Children.Add(icon);
        Grid.SetColumn(icon, 0);
        Grid.SetRowSpan(icon, 2);

        var iqnText = new TextBlock
        {
            Cursor = new Cursor(StandardCursorType.Hand),
            Text = destino.Iqn.Contains(':') ? destino.Iqn.Substring(destino.Iqn.LastIndexOf(':') + 1) : destino.Iqn,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!
        };

        grid.Children.Add(iqnText);
        Grid.SetColumn(iqnText, 1);
        Grid.SetRow(iqnText, 0);

        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };

        // CONNECTION AND AUTHENTICATION BUTTONS
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
        else
        {
            var connectBtn = new Button
            {
                Classes = { "SteamButton" },
                Content = destino.Conectado ? "Disconnect" : "Connect",
                Width = 100,
                Height = 28
            };

            connectBtn.Click += async (_, _) =>
            {
                if (!destino.Conectado)
                {
                    using (LoadingService.Show("Connecting..."))
                        await IscsiHelper.Conectar(destino);
                }
                else
                {
                    using (LoadingService.Show("Disconnecting..."))
                        await IscsiHelper.Desconectar_Borrar(destino);
                }

                await RefreshTargetsList();
            };

            btnRow.Children.Add(connectBtn);
        }

        // INIT BUTTON FOR UNFORMATTED DISKS (Only visible if connected and no filesystem detected)
        if (destino.Conectado && !destino.TieneFilesystem)
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
                    var dlg = new InitializeDiskDialog(destino);
                    bool? success = await dlg.ShowDialog<bool>(owner);

                    if (success == true)
                    {
                        using (LoadingService.Show("Updating target info..."))
                        {
                            await Task.Run(async () =>
                            {
                                await IscsiHelper.CompletarInformacionDestino(destino, 0);
                            });
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

        // TARGET SELECTION EVENT
        border.PointerPressed += async (_, _) =>
        {
            _selected = _targets.FirstOrDefault(t => t.Iqn == destino.Iqn);
            if (_selected == null)
                return;

            foreach (var child in TargetsList.Children)
            {
                if (child is Border b)
                    b.Classes.Remove("selected");
            }

            border.Classes.Add("selected");

            using (LoadingService.Show("Reading target info..."))
            {
                await Task.Run(async () =>
                {
                    await IscsiHelper.CompletarInformacionDestino(_selected, 0);
                });
            }

            _selected.InfoCompleta = true;
            await LoadTargetDetailsAsync(_selected);
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
        BtnDeleteNode.IsVisible = true;

        bool persistente = IscsiPersistenceManager.Detect(destino);
        BtnTogglePersist.IsEnabled = destino.Conectado;
        BtnTogglePersist.Content = persistente ? "Remove Persist" : "Persist";

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80, *"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
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

        DetailsInfoPanel.Children.Add(infoGrid);
        DetailsIcon.Source = LoadIcon(GetIconForChap(destino));

        BtnHeaderUnmount.IsVisible = destino.Conectado;
        BtnHeaderOpen.IsVisible = destino.Conectado &&
                                  !string.IsNullOrWhiteSpace(destino.MountPoint) &&
                                  Directory.Exists(destino.MountPoint);

        await Task.CompletedTask;
    }

    // ============================================================
    // PERSISTENCE & HEADER EVENTS
    // ============================================================

    private async void OnTogglePersist(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        bool persistente = IscsiPersistenceManager.Detect(_selected);

        using (LoadingService.Show(persistente ? "Removing persistence..." : "Applying persistence..."))
        {
            await IscsiHelper.CompletarInformacionDestino(_selected, 0);

            if (!persistente)
                await IscsiPersistenceManager.ApplyAsync(_selected);
            else
                await IscsiPersistenceManager.RemoveAsync(_selected);
        }

        await RefreshTargetsList();
    }

    private async void OnHeaderUnmount(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        using (LoadingService.Show("Unmounting..."))
            await IscsiHelper.Desconectar_Borrar(_selected);

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
        if (_selected == null)
            return;

        using (LoadingService.Show("Deleting target node..."))
        {
            await IscsiHelper.Desconectar_Borrar(_selected);
        }

        _targets.Remove(_selected);
        _selected = null;

        await RefreshTargetsList();
    }

    // ============================================================
    // ASSETS / ICONS
    // ============================================================

    private string GetIconForChap(IscsiDestino d)
    {
        bool hdd = !d.TieneFilesystem;

        if (d.UsaMutualChap)
            return hdd ? "avares://ISCSI-Util/Assets/Icons/chap-mutual-hdd.jpeg"
                       : "avares://ISCSI-Util/Assets/Icons/chap-mutual.jpeg";

        if (d.UsaChap)
            return hdd ? "avares://ISCSI-Util/Assets/Icons/chap-hdd.jpeg"
                       : "avares://ISCSI-Util/Assets/Icons/chap.jpeg";

        return hdd ? "avares://ISCSI-Util/Assets/Icons/no-chap-hdd.jpeg"
                   : "avares://ISCSI-Util/Assets/Icons/no-chap.jpeg";
    }

    private Bitmap? LoadIcon(string uri)
    {
        try
        {
            var assets = AssetLoader.Open(new Uri(uri));
            return new Bitmap(assets);
        }
        catch
        {
            return null;
        }
    }
}