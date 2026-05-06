using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;

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
    // DISCOVER REAL (ASYNC)
    // ============================================================

    private async void DiscoverButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(portal))
            return;

        _targets.Clear();

        var lista = await IscsiHelper.Descubrir(portal);
        _targets.AddRange(lista);

        RefreshTargetsList();
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
    // TARGET CARD (LISTA)
    // ============================================================

    private Control CreateTargetCard(IscsiDestino destino)
    {
        var border = new Border
        {
            Classes = { "steam-card" },
            Padding = new Thickness(12),
            CornerRadius = new Avalonia.CornerRadius(6)
        };

        var stack = new StackPanel { Spacing = 6 };

        // IQN
        stack.Children.Add(new TextBlock
        {
            Text = destino.Iqn,
            FontSize = 14
        });

        // Botón Connect / Disconnect
        var btn = new Button
        {
            Classes = { "steam-button" },
            Content = destino.Conectado ? "Disconnect" : "Connect",
            Width = 120
        };

        btn.Click += async (_, _) =>
        {
            if (!destino.Conectado)
                await IscsiHelper.Conectar(destino);
            else
                await IscsiHelper.Desconectar(destino);

            RefreshTargetsList();
            LoadTargetDetails(destino);
        };

        stack.Children.Add(btn);

        border.Child = stack;

        // Selección
        border.PointerPressed += (_, _) =>
        {
            _selected = destino;
            LoadTargetDetails(destino);
        };

        return border;
    }

    // ============================================================
    // DETAILS CARD (ABAJO)
    // ============================================================

    private void LoadTargetDetails(IscsiDestino destino)
    {
        DetailsInfoPanel.Children.Clear();

        // IQN
        DetailsInfoPanel.Children.Add(new TextBlock
        {
            Text = $"IQN: {destino.Iqn}"
        });

        // Portal
        DetailsInfoPanel.Children.Add(new TextBlock
        {
            Text = $"Portal: {destino.Ip}"
        });

        // Estado
        DetailsInfoPanel.Children.Add(new TextBlock
        {
            Text = $"Status: {(destino.Conectado ? "Connected" : "Disconnected")}"
        });

        // Filesystem
        DetailsInfoPanel.Children.Add(new TextBlock
        {
            Text = $"Filesystem: {(destino.TieneFilesystem ? "Yes" : "No")}"
        });

        // ============================================================
        // ICONO REAL SEGÚN CHAP
        // ============================================================

        string chapMode =
            destino.UsaMutualChap ? "mutual" :
            destino.UsaChap ? "chap" :
            "no-chap";

        DetailsIcon.Source = LoadIcon(GetIconForChap(chapMode));

        // ============================================================
        // PERSISTENCIA
        // ============================================================

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

        // ============================================================
        // MOUNT / UNMOUNT + OPEN
        // ============================================================

        var mountRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };

        if (destino.Conectado)
        {
            if (!string.IsNullOrEmpty(destino.MountPoint) &&
                Directory.Exists(destino.MountPoint))
            {
                // UNMOUNT
                var unmountBtn = new Button
                {
                    Content = "Unmount",
                    Classes = { "steam-button" }
                };
                unmountBtn.Click += async (_, _) =>
                {
                    await IscsiHelper.Desconectar(destino);
                    LoadTargetDetails(destino);
                };
                mountRow.Children.Add(unmountBtn);

                // OPEN
                var openBtn = new Button
                {
                    Content = "Open",
                    Classes = { "steam-button" }
                };
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
                // MOUNT
                var mountBtn = new Button
                {
                    Content = "Mount",
                    Classes = { "steam-button" }
                };
                mountBtn.Click += async (_, _) =>
                {
                    await IscsiHelper.Conectar(destino);
                    LoadTargetDetails(destino);
                };
                mountRow.Children.Add(mountBtn);
            }
        }

        DetailsInfoPanel.Children.Add(mountRow);

        // ============================================================
        // INITIALIZE DISK (DIÁLOGO NUEVO)
        // ============================================================

        var initBtn = new Button
        {
            Content = "Initialize Disk",
            Classes = { "steam-button" }
        };

        initBtn.Click += async (_, _) =>
        {
            var dlg = new InitializeDiskDialog(destino);
            await dlg.ShowDialog((Window)this.VisualRoot);
            LoadTargetDetails(destino);
        };

        DetailsInfoPanel.Children.Add(initBtn);
    }

    // ============================================================
    // ICONOS
    // ============================================================

    private string GetIconForChap(string mode)
    {
        return mode switch
        {
            "chap" => "Assets/chap.jpeg",
            "mutual" => "Assets/chap-mutual.jpeg",
            "no-chap" => "Assets/no-chap.jpeg",
            _ => "Assets/main-icon.jpeg"
        };
    }

    private Bitmap LoadIcon(string path)
    {
        if (!File.Exists(path))
            return null;

        return new Bitmap(path);
    }
}
