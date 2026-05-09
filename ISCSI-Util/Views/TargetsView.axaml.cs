using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;

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
    }

    // ============================================================
    // VALIDACIÓN DE IP
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
            if (int.TryParse(p, out int val))
            {
                if (val < 0 || val > 255)
                {
                    tb.Text = _lastValidIp;
                    tb.CaretIndex = tb.Text.Length;
                    return;
                }
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
            if (!int.TryParse(p, out int val))
                return false;

            if (val < 0 || val > 255)
                return false;
        }

        return true;
    }

    private string RemoveInvalidChars(string s)
    {
        var result = new List<char>();
        foreach (char c in s)
            if (char.IsDigit(c) || c == '.')
                result.Add(c);
        return new string(result.ToArray());
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
    // DISCOVER
    // ============================================================

    private async void DiscoverButton_Click(object? sender, RoutedEventArgs e)
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (!EsIpCompletaValida(portal))
            return;

        using (LoadingService.Show("Discovering targets..."))
        {
            _targets.Clear();
            _targets.AddRange(await IscsiHelper.Descubrir(portal));
        }

        RefreshTargetsList();

        if (_targets.Count > 0)
        {
            _selected = _targets[0];
            LoadTargetDetails(_selected);
        }
    }

    // ============================================================
    // REFRESH LIST
    // ============================================================

    private void RefreshTargetsList()
    {
        TargetsList.Children.Clear();

        foreach (var destino in _targets)
            TargetsList.Children.Add(CreateTargetCard(destino));
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
            BoxShadow = new BoxShadows(),
            BorderBrush = (IBrush)Application.Current!.FindResource("SteamBorderStrong")!,
            BorderThickness = new Thickness(1.4)
        };

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
            MaxWidth = 80,
            MaxHeight = 80,
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
            Text = destino.Iqn,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (IBrush)Application.Current!.FindResource("SteamText")!
        };

        grid.Children.Add(iqnText);
        Grid.SetColumn(iqnText, 1);
        Grid.SetRow(iqnText, 0);

        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

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
                    await IscsiHelper.Desconectar(destino);
            }

            RefreshTargetsList();
            LoadTargetDetails(destino);
        };
        btnRow.Children.Add(connectBtn);

        if (destino.UsaChap && !destino.UsaMutualChap)
        {
            var chapBtn = new Button
            {
                Content = "CHAP",
                Classes = { "SteamButton" },
                Width = 70,
                Height = 28
            };
            chapBtn.Click += async (_, _) =>
            {
                var dlg = new ChapDialog(destino);
                await dlg.ShowDialog((Window)VisualRoot);
                LoadTargetDetails(destino);
            };
            btnRow.Children.Add(chapBtn);
        }

        if (destino.UsaMutualChap)
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
                var dlg = new MutualChapDialog(destino);
                await dlg.ShowDialog((Window)VisualRoot);
                LoadTargetDetails(destino);
            };
            btnRow.Children.Add(mutualBtn);
        }

        if (destino.Conectado && !destino.TieneFilesystem)
        {
            var initBtn = new Button
            {
                Content = "Init",
                Classes = { "SteamButton" },
                Width = 60,
                Height = 28,
                Background = new SolidColorBrush(Colors.DarkOrange)
            };
            initBtn.Click += async (_, _) =>
            {
                var dlg = new InitializeDiskDialog(destino);
                await dlg.ShowDialog((Window)VisualRoot);
                LoadTargetDetails(destino);
            };
            btnRow.Children.Add(initBtn);
        }

        grid.Children.Add(btnRow);
        Grid.SetColumn(btnRow, 1);
        Grid.SetRow(btnRow, 1);

        border.PointerPressed += (_, _) =>
        {
            _selected = destino;
            LoadTargetDetails(destino);
        };

        border.Child = grid;
        return border;
    }

    // ============================================================
    // DETAILS PANEL
    // ============================================================

    private async void LoadTargetDetails(IscsiDestino destino)
    {
        _selected = destino;

        DetailsInfoPanel.Children.Clear();
        TextBlock_Header.IsVisible = true;
        BtnDeleteNode.IsVisible = true;
        BtnHeaderUnmount.IsVisible = false;
        BtnHeaderOpen.IsVisible = false;

        IscsiHelper.DetectarChap(destino);

        if (destino.Conectado)
        {
            using (LoadingService.Show("Reading target info..."))
                await IscsiHelper.CompletarInformacionDestino(destino, 0);
        }

        destino.Persistir = IscsiHelper.DetectarPersistencia(destino);

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80, *"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing = 2
        };

        infoGrid.Children.Add(new TextBlock
        {
            Text = "IQN:",
            TextAlignment = TextAlignment.Center,
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 1)
        });
        Grid.SetRow(infoGrid.Children[^1], 0);

        infoGrid.Children.Add(new TextBlock
        {
            Text = destino.Iqn,
            Foreground = (IBrush)Application.Current!.FindResource("SteamText")!,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(infoGrid.Children[^1], 1);
        Grid.SetRow(infoGrid.Children[^1], 0);

        infoGrid.Children.Add(new TextBlock
        {
            Text = "Portal:",
            TextAlignment = TextAlignment.Center,
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 1)
        });
        Grid.SetRow(infoGrid.Children[^1], 1);

        infoGrid.Children.Add(new TextBlock
        {
            Text = destino.Ip,
            Foreground = (IBrush)Application.Current!.FindResource("SteamText")!
        });
        Grid.SetColumn(infoGrid.Children[^1], 1);
        Grid.SetRow(infoGrid.Children[^1], 1);

        infoGrid.Children.Add(new TextBlock
        {
            Text = "Status:",
            TextAlignment = TextAlignment.Center,
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 1)
        });
        Grid.SetRow(infoGrid.Children[^1], 2);

        infoGrid.Children.Add(new TextBlock
        {
            Text = destino.Conectado ? "Connected" : "Disconnected",
            Foreground = destino.Conectado
                ? (IBrush)Application.Current!.FindResource("SteamGreen")!
                : Brushes.OrangeRed
        });
        Grid.SetColumn(infoGrid.Children[^1], 1);
        Grid.SetRow(infoGrid.Children[^1], 2);

        infoGrid.Children.Add(new TextBlock
        {
            Text = "Filesystem:",
            Foreground = (IBrush)Application.Current!.FindResource("SteamBlue")!,
            FontWeight = FontWeight.SemiBold
        });
        Grid.SetRow(infoGrid.Children[^1], 3);

        infoGrid.Children.Add(new TextBlock
        {
            Text = destino.TieneFilesystem ? "Yes" : "No",
            Foreground = (IBrush)Application.Current!.FindResource("SteamText")!
        });
        Grid.SetColumn(infoGrid.Children[^1], 1);
        Grid.SetRow(infoGrid.Children[^1], 3);

        DetailsInfoPanel.Children.Add(infoGrid);

        // ICONO
        DetailsIcon.Source = LoadIcon(GetIconForChap(destino));

        // ============================================================
        // BOTONES DEL HEADER
        // ============================================================

        if (destino.Conectado)
        {
            if (!string.IsNullOrEmpty(destino.MountPoint) &&
                Directory.Exists(destino.MountPoint))
            {
                BtnHeaderUnmount.IsVisible = true;
                BtnHeaderOpen.IsVisible = true;
            }
            else
            {
                BtnHeaderUnmount.IsVisible = true;
                BtnHeaderOpen.IsVisible = false;
            }
        }

        // ============================================================
        // PERSISTENCIA
        // ============================================================

        var toggle = new CheckBox
        {
            Content = "Persistent mount",
            IsChecked = destino.Persistir,
            Foreground = (IBrush)Application.Current!.FindResource("SteamText")!,
            Margin = new Thickness(0, 4, 0, 0)
        };
        toggle.Checked += (_, _) =>
        {
            destino.Persistir = true;
            IscsiHelper.AplicarPersistencia(destino);
        };
        toggle.Unchecked += (_, _) =>
        {
            destino.Persistir = false;
            IscsiHelper.AplicarPersistencia(destino);
        };
        DetailsInfoPanel.Children.Add(toggle);
    }

    // ============================================================
    // EVENTOS DEL HEADER
    // ============================================================

    private async void OnHeaderUnmount(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        using (LoadingService.Show("Unmounting..."))
            await IscsiHelper.Desconectar(_selected);

        LoadTargetDetails(_selected);
    }

    private void OnHeaderOpen(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        if (!string.IsNullOrEmpty(_selected.MountPoint) &&
            Directory.Exists(_selected.MountPoint))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _selected.MountPoint,
                UseShellExecute = true
            });
        }
    }

    // ============================================================
    // DELETE NODE
    // ============================================================

    private async void OnDeleteNode(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        await EliminarNodo(_selected);
    }

    private async Task EliminarNodo(IscsiDestino d)
    {
        ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --logout");
        ShellHelper.EjecutarComoRoot($"iscsiadm -m node -T {d.Iqn} -p {d.Ip} --op=delete");
        ShellHelper.EjecutarComoRoot($"iscsiadm -m discoverydb -t sendtargets -p {d.Ip} --op=delete");

        _targets.Remove(d);
        RefreshTargetsList();

        DetailsInfoPanel.Children.Clear();
        DetailsIcon.Source = null;

        BtnHeaderUnmount.IsVisible = false;
        BtnHeaderOpen.IsVisible = false;
    }

    // ============================================================
    // ICONOS
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

    private Bitmap LoadIcon(string uri)
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
