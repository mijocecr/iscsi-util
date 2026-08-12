using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views;

public partial class SessionsView : UserControl
{
    private static long _loadId;
    private List<SessionInfo> _sesiones = new();
    private SessionInfo? _selected;

    public SessionsView()
    {
        InitializeComponent();
        Console.WriteLine("[SESSIONS] SessionsView inicializado.");
    }

    // ============================================================
    //   CARGAR SESIONES REALES
    // ============================================================

    public async Task CargarSesiones()
    {
        long id = ++_loadId;
        Console.WriteLine($"[SESSIONS] #{id} → CargarSesiones()");

        try
        {
            var nuevas = await IscsiSessions.ObtenerVistaGlobal(); // ahora devuelve SessionInfo
            _sesiones = nuevas ?? new List<SessionInfo>();

            // Mantener selección previa
            if (_selected != null)
                _selected = _sesiones.Find(x => x.Iqn == _selected.Iqn &&
                                                x.Portal == _selected.Portal &&
                                                x.LunId == _selected.LunId);

            PintarLista();

            if (_selected != null)
                PintarDetalles(_selected);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS] ERROR: {ex.Message}");
            _sesiones.Clear();
            PintarLista();
        }
    }

    // ============================================================
    //   PINTAR LISTA
    // ============================================================

    private void PintarLista()
    {
        SessionsList.Children.Clear();

        foreach (var s in _sesiones)
        {
            var card = CrearTarjeta(s);

            if (_selected == s)
                card.BorderBrush = Brushes.Gold;

            SessionsList.Children.Add(card);
        }
    }
    
   private Border CrearTarjeta(SessionInfo s)
{
    // ============================================================
    // 1. Preparar datos
    // ============================================================

    string fs = string.IsNullOrWhiteSpace(s.Filesystem) ? "RAW" : s.Filesystem;
    string mp = string.IsNullOrWhiteSpace(s.MountPoint) ? "(no mount)" : s.MountPoint;

    // Título principal = nombre del destino (IQN)
    string titulo = s.Iqn;

    // ============================================================
    // 2. Tarjeta Steam
    // ============================================================

    var border = new Border
    {
        Cursor = new Cursor(StandardCursorType.Hand),
        Classes = { "SteamCard" },
        BorderBrush = s.Connected
            ? (IBrush)Resources["SteamGreen"]!
            : (IBrush)Resources["SteamBlue"]!,
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 10)
    };

    var panel = new StackPanel { Spacing = 6 };

    // ============================================================
    // 3. Título (destino / IQN)
    // ============================================================

    panel.Children.Add(new TextBlock
    {
        Text = titulo,
        FontSize = 16,
        FontWeight = FontWeight.Bold,
        Foreground = (IBrush)Resources["SteamBlue"]!,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 380
    });

    // ============================================================
    // 4. FS + mount (línea secundaria)
    // ============================================================

    panel.Children.Add(new TextBlock
    {
        Text = $"{fs} | {mp}",
        Foreground = (IBrush)Resources["SteamGreen"]!,
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap
    });

    // ============================================================
    // 5. Portal
    // ============================================================

    panel.Children.Add(new TextBlock
    {
        Text = s.Portal,
        Foreground = Brushes.Gray,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    });

    // ============================================================
    // 6. LUN
    // ============================================================

    panel.Children.Add(new TextBlock
    {
        Text = $"LUN {s.LunId}",
        Foreground = Brushes.LightGray,
        FontSize = 12
    });

    border.Child = panel;

    // ============================================================
    // 7. Selección
    // ============================================================

    border.PointerPressed += (_, _) =>
    {
        _selected = _sesiones.Find(x =>
            x.Iqn == s.Iqn &&
            x.Portal == s.Portal &&
            x.LunId == s.LunId);

        PintarLista();
        if (_selected != null)
            PintarDetalles(_selected);
    };

    return border;
}


   
    // ============================================================
    //   PINTAR DETALLES
    // ============================================================

    private void PintarDetalles(SessionInfo s)
    {
        if (s == null)
            return;

        DetailsPanel.Children.Clear();

        // Normalized values
        string fs   = string.IsNullOrWhiteSpace(s.Filesystem) ? "RAW" : s.Filesystem;
        string mp   = string.IsNullOrWhiteSpace(s.MountPoint) ? "-" : s.MountPoint;
        string dev  = string.IsNullOrWhiteSpace(s.Device)     ? "-" : s.Device;
        string auth = string.IsNullOrWhiteSpace(s.Auth)       ? "-" : s.Auth;

        // Human-readable size
        string size = s.SizeGb > 0 ? FormatSize(s.SizeGb) : "-";

        // ============================
        //  SESSION
        // ============================
        AddDetail("Target (IQN):", s.Iqn, true);
        AddDetail("Portal:", s.Portal);
        AddDetail("Status:", s.Connected ? "Connected" : "Disconnected");
        AddDetail("C. Since:", s.ConnectedSince.ToString("yyyy-MM-dd HH:mm:ss"));

        // ============================
        //  DISK
        // ============================
        AddDetail("Device:", dev);
        AddDetail("Filesystem:", fs);
        AddDetail("Mountpoint:", mp);

        // ============================
        //  LUN
        // ============================
        AddDetail("LUN:", s.LunId.ToString());

        // ============================
        //  HARDWARE
        // ============================
        AddDetail("Size:", size);

        // ============================
        //  AUTH
        // ============================
        AddDetail("Auth:", auth);

        // ============================
        //  BUTTONS
        // ============================

        BtnOpen.IsEnabled = mp != "-" && System.IO.Directory.Exists(mp);
        BtnOpen.Click -= OnOpen;
        BtnOpen.Click += OnOpen;

        BtnMount.Content = s.Connected ? "Disconnect" : "Connect";
        BtnMount.IsEnabled = true;
        BtnMount.Click -= OnMount;
        BtnMount.Click += OnMount;
    }

    private string FormatSize(double sizeGb)
    {
        // Convert GB → TB or MB depending on magnitude
        if (sizeGb >= 1024)
        {
            double tb = sizeGb / 1024.0;
            return $"{tb:0.##} TB";
        }
        else if (sizeGb < 1)
        {
            double mb = sizeGb * 1024.0;
            return $"{mb:0.##} MB";
        }
        else
        {
            return $"{sizeGb:0.##} GB";
        }
    }




    private void AddDetail(string label, string value, bool wrap = true)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120, *"),
            Margin = new Thickness(0, 0, 0, 4)
        };

        // Label (left column)
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "DetailLabel" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        });

        // Value (right column)
        var val = new TextBlock
        {
            Text = value,
            Classes = { "DetailValue" },
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            MaxWidth = 380,   // suficiente para mountpoints largos
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };

        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        DetailsPanel.Children.Add(grid);
    }


    // ============================================================
    //  ACCIONES
    // ============================================================

    private async void OnMount(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        if (_selected.Connected)
        {
            using (LoadingService.Show("Disconnecting..."))
                await IscsiHelper.DesconectarSesion(_selected);

            // ============================
            // LIMPIAR DETALLES AL DESCONECTAR
            // ============================
            _selected = null;
            DetailsPanel.Children.Clear();
        }
        else
        {
            using (LoadingService.Show("Connecting..."))
                await IscsiHelper.ConectarSesion(_selected);
        }

        await CargarSesiones();
    }

    
    private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        if (string.IsNullOrWhiteSpace(_selected.MountPoint))
            return;

        if (!System.IO.Directory.Exists(_selected.MountPoint))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _selected.MountPoint,
            UseShellExecute = true
        });
    }
}
