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
    // DISCOVER REAL
    // ============================================================

    private void DiscoverButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string portal = PortalBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(portal))
            return;

        _targets.Clear();
        _targets.AddRange(IscsiHelper.Descubrir(portal));

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

        btn.Click += (_, _) =>
        {
            if (!destino.Conectado)
                IscsiHelper.Conectar(destino);
            else
                IscsiHelper.Desconectar(destino);

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
        // TOGGLE PERSISTENCIA
        // ============================================================

        var toggle = new CheckBox
        {
            Content = "Persistent mount",
            // De momento no lo ligamos a una propiedad del modelo
            IsChecked = false
        };

        toggle.Checked += (_, _) =>
        {
            if (destino.TieneFilesystem)
                IscsiHelper.ConfigurarPersistencia(destino, "ext4");
        };

        toggle.Unchecked += (_, _) =>
        {
            IscsiHelper.EliminarServicioPersistencia(destino);
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
                unmountBtn.Click += (_, _) =>
                {
                    IscsiHelper.Desconectar(destino, eliminarPersistencia: false);
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
                mountBtn.Click += (_, _) =>
                {
                    IscsiHelper.Conectar(destino);
                    LoadTargetDetails(destino);
                };
                mountRow.Children.Add(mountBtn);
            }
        }

        DetailsInfoPanel.Children.Add(mountRow);

        // ============================================================
        // INITIALIZE DISK
        // ============================================================

        var initBtn = new Button
        {
            Content = "Initialize Disk",
            Classes = { "steam-button" }
        };

        initBtn.Click += (_, _) =>
        {
            IscsiHelper.InicializarDestino(destino);
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
