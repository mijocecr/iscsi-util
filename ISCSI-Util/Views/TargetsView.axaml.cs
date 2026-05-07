using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Platform;

namespace ISCSI_Util.Views;

public partial class TargetsView : UserControl
{
    private readonly List<IscsiDestino> _targets = new();
    private IscsiDestino? _selected;

    public TargetsView()
    {
        InitializeComponent();
    }

    // ============================================================
    // DISCOVER
    // ============================================================

    private async void DiscoverButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(portal))
            return;

        _targets.Clear();
        _targets.AddRange(await IscsiHelper.Descubrir(portal));

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
    // TARGET CARD (ACTUALIZADO)
    // ============================================================

  private Control CreateTargetCard(IscsiDestino destino)
{
    var border = new Border
    {
        Classes = { "steam-card" },
        Padding = new Thickness(10),
        CornerRadius = new Avalonia.CornerRadius(6),
        Height = 110,
        Margin = new Thickness(0, 0, 0, 2)
    };

    var root = new StackPanel
    {
        Spacing = 6,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    // IQN compacto sin romper altura
    root.Children.Add(new TextBlock
    {
        Text = destino.Iqn,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    });

    // Fila de botones compacta
    var grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,6,Auto,6,Auto,6,Auto"),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    // CONNECT / DISCONNECT
    var connectBtn = new Button
    {
        Classes = { "steam-button" },
        Content = destino.Conectado ? "Disconnect" : "Connect",
        Width = 100,
        Height = 28
    };
    connectBtn.Click += async (_, _) =>
    {
        if (!destino.Conectado)
            await IscsiHelper.Conectar(destino);
        else
            await IscsiHelper.Desconectar(destino);

        RefreshTargetsList();
        LoadTargetDetails(destino);
    };
    grid.Children.Add(connectBtn);
    Grid.SetColumn(connectBtn, 0);

    // CHAP NORMAL
    if (destino.UsaChap && !destino.UsaMutualChap)
    {
        var chapBtn = new Button
        {
            Content = "CHAP",
            Classes = { "steam-button" },
            Width = 80,
            Height = 28
        };
        chapBtn.Click += async (_, _) =>
        {
            var dlg = new ChapDialog(destino);
            await dlg.ShowDialog((Window)this.VisualRoot);
            LoadTargetDetails(destino);
        };
        grid.Children.Add(chapBtn);
        Grid.SetColumn(chapBtn, 2);
    }

    // MUTUAL CHAP
    if (destino.UsaMutualChap)
    {
        var mutualBtn = new Button
        {
            Content = "Mutual",
            Classes = { "steam-button" },
            Width = 90,
            Height = 28
        };
        mutualBtn.Click += async (_, _) =>
        {
            var dlg = new MutualChapDialog(destino);
            await dlg.ShowDialog((Window)this.VisualRoot);
            LoadTargetDetails(destino);
        };
        grid.Children.Add(mutualBtn);
        Grid.SetColumn(mutualBtn, 2);
    }

    // 🔥 INIT SOLO SI ESTÁ CONECTADO Y NO TIENE FS
    if (destino.Conectado && !destino.TieneFilesystem)
    {
        var initBtn = new Button
        {
            Content = "Init",
            Classes = { "steam-button" },
            Background = Brushes.DarkOrange,
            Width = 60,
            Height = 28
        };
        initBtn.Click += async (_, _) =>
        {
            var dlg = new InitializeDiskDialog(destino);
            await dlg.ShowDialog((Window)this.VisualRoot);
            LoadTargetDetails(destino);
        };
        grid.Children.Add(initBtn);
        Grid.SetColumn(initBtn, 4);
    }

    root.Children.Add(grid);

    border.PointerPressed += (_, _) =>
    {
        _selected = destino;
        LoadTargetDetails(destino);
    };

    border.Child = root;
    return border;
}

    // ============================================================
    // DETAILS PANEL (ACTUALIZADO CON ICONO)
    // ============================================================

    private async void LoadTargetDetails(IscsiDestino destino)
    {
        DetailsInfoPanel.Children.Clear();

        IscsiHelper.DetectarChap(destino);

        if (destino.Conectado)
            await IscsiHelper.CompletarInformacionDestino(destino, 0);

        destino.Persistir = IscsiHelper.DetectarPersistencia(destino);

        DetailsInfoPanel.Children.Add(new TextBlock
        {
            Text = $"IQN: {destino.Iqn}",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400
        });

        DetailsInfoPanel.Children.Add(new TextBlock { Text = $"Portal: {destino.Ip}" });
        DetailsInfoPanel.Children.Add(new TextBlock { Text = $"Status: {(destino.Conectado ? "Connected" : "Disconnected")}" });
        DetailsInfoPanel.Children.Add(new TextBlock { Text = $"Filesystem: {(destino.TieneFilesystem ? "Yes" : "No")}" });

        // 🔥 ICONO ACTIVADO
        DetailsIcon.Source = LoadIcon(GetIconForChap(destino));

        var toggle = new CheckBox
        {
            Content = "Persistent mount",
            IsChecked = destino.Persistir
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

        var mountRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        if (destino.Conectado)
        {
            if (!string.IsNullOrEmpty(destino.MountPoint) &&
                Directory.Exists(destino.MountPoint))
            {
                var unmountBtn = new Button { Content = "Unmount", Classes = { "steam-button" } };
                unmountBtn.Click += async (_, _) =>
                {
                    await IscsiHelper.Desconectar(destino);
                    LoadTargetDetails(destino);
                };
                mountRow.Children.Add(unmountBtn);

                var openBtn = new Button { Content = "Open", Classes = { "steam-button" } };
                openBtn.Click += (_, _) =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = destino.MountPoint,
                        UseShellExecute = true
                    });
                };
                mountRow.Children.Add(openBtn);
            }
            else
            {
                var mountBtn = new Button { Content = "Mount", Classes = { "steam-button" } };
                mountBtn.Click += async (_, _) =>
                {
                    await IscsiHelper.Conectar(destino);
                    LoadTargetDetails(destino);
                };
                mountRow.Children.Add(mountBtn);
            }
        }

        DetailsInfoPanel.Children.Add(mountRow);
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
