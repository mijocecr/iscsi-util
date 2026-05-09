using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;

namespace ISCSI_Util.Views;

public partial class SessionsView : UserControl
{
    private static long _loadId;
    private List<IscsiDestino> _destinos = new();
    private IscsiDestino? _selected;

    public SessionsView()
    {
        InitializeComponent();
        Console.WriteLine("[SESSIONS] SessionsView inicializado.");
    }

    // ============================================================
    //   CARGAR SESIONES (VISTA GLOBAL REAL)
    // ============================================================
    public async Task CargarSesiones()
    {
        long id = ++_loadId;
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"[SESSIONS] #{id} → CargarSesiones()");

        if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
        {
            Console.WriteLine($"[SESSIONS] #{id} Saltando: no hay contraseña.");
            _destinos.Clear();
            PintarLista();
            return;
        }

        try
        {
            var nuevos = await Iscsi_Sessions_Helper.ObtenerVistaGlobal();
            _destinos = nuevos ?? new List<IscsiDestino>();

            if (_selected != null)
                _selected = _destinos.Find(x => x.Iqn == _selected.Iqn && x.Ip == _selected.Ip);

            PintarLista();

            if (_selected != null)
                PintarDetalles(_selected);

            sw.Stop();
            Console.WriteLine($"[SESSIONS] #{id} ← OK en {sw.ElapsedMilliseconds} ms (Destinos={_destinos.Count})");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"[SESSIONS] #{id} ERROR: {ex.Message}");
            _destinos.Clear();
            PintarLista();
        }
    }

    // ============================================================
    //   PINTAR LISTA
    // ============================================================
    private void PintarLista()
    {
        SessionsList.Children.Clear();

        foreach (var d in _destinos)
        {
            var card = CrearTarjeta(d);

            if (_selected == d)
                card.BorderBrush = Brushes.Gold;

            SessionsList.Children.Add(card);
        }

        Console.WriteLine($"[SESSIONS] PintarLista: {_destinos.Count} tarjetas.");
    }

    private Border CrearTarjeta(IscsiDestino d)
    {
        var border = new Border
        {
            Cursor = new Cursor(StandardCursorType.Hand),
            Classes = { "SteamCard" },
            BorderBrush = d.Conectado
                ? (IBrush)Resources["SteamGreen"]!
                : (IBrush)Resources["SteamBlue"]!,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(new TextBlock
        {
            Text = d.Iqn,
            Classes = { "IqnList" },
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260
        });

        panel.Children.Add(new TextBlock
        {
            Text = d.Ip,
            Foreground = Brushes.WhiteSmoke,
            FontSize = 12
        });

        string fs = d.TieneFilesystem ? d.FsType : "RAW";

        panel.Children.Add(new TextBlock
        {
            Text = $"{fs} | {(d.MountPoint ?? "(no mount)")}",
            Foreground = (IBrush)Resources["SteamBlue"]!,
            FontSize = 12
        });

        border.Child = panel;

        border.PointerPressed += (_, _) =>
        {
            _selected = _destinos.Find(x => x.Iqn == d.Iqn && x.Ip == d.Ip);

            Console.WriteLine($"[SESSIONS] Seleccionado: {_selected?.Iqn} @ {_selected?.Ip}");

            PintarLista();

            if (_selected != null)
                PintarDetalles(_selected);
        };

        return border;
    }

    // ============================================================
    //   PINTAR DETALLES (CORREGIDO)
    // ============================================================
    private void PintarDetalles(IscsiDestino d)
    {
        if (!_destinos.Contains(d))
            return;

        DetailsPanel.Children.Clear();

        AddDetail("IQN:", d.Iqn, true);
        AddDetail("IP:", d.Ip);
        AddDetail("Estado:", d.Conectado ? "Connected" : "Disconnected");
        AddDetail("Device:", d.DevicePath ?? "-");
        AddDetail("Partition:", d.PartitionPath ?? "-");
        AddDetail("FS:", d.TieneFilesystem ? d.FsType : "RAW");
        AddDetail("Mount:", d.MountPoint ?? "-");
        AddDetail("Vendor:", d.Vendor ?? "-");
        AddDetail("Model:", d.Model ?? "-");
        AddDetail("Persist:", d.Persistir ? "Yes" : "No");

        // ============================
        // ESTADO REAL DEL DESTINO
        // ============================

        bool isConnected   = d.Conectado;
        bool hasFs         = d.TieneFilesystem;
        bool hasDevice     = !string.IsNullOrWhiteSpace(d.DevicePath);
        bool hasPartition  = !string.IsNullOrWhiteSpace(d.PartitionPath);
        bool hasMountPoint = !string.IsNullOrWhiteSpace(d.MountPoint);

        bool isMounted = false;

        if (hasMountPoint)
        {
            var mp = ShellHelper.EjecutarComoRoot($"mountpoint -q \"{d.MountPoint}\"");
            isMounted = mp.ExitCode == 0;
        }

        // ============================
        // BOTONES (LÓGICA CORRECTA)
        // ============================

        BtnOpen.IsEnabled       = isMounted;
        BtnInit.IsEnabled       = isConnected && !hasFs && hasDevice && hasPartition;
        BtnMount.IsEnabled      = isConnected && hasFs;
        BtnDisconnect.IsEnabled = isConnected;

        BtnMount.Content = isMounted ? "Unmount" : "Mount";

        BtnOpen.Click       -= OnOpen;
        BtnInit.Click       -= OnInit;
        BtnMount.Click      -= OnMount;
        BtnDisconnect.Click -= OnDisconnect;

        BtnOpen.Click       += OnOpen;
        BtnInit.Click       += OnInit;
        BtnMount.Click      += OnMount;
        BtnDisconnect.Click += OnDisconnect;

        Console.WriteLine($"[SESSIONS] Detalles pintados para {d.Iqn}");
    }

    private void AddDetail(string label, string value, bool wrap = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,190"),
            Margin = new Thickness(0, 0, 0, 4)
        };

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "DetailLabel" }
        });

        var val = new TextBlock
        {
            Text = value,
            Classes = { "DetailValue" },
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 190
        };

        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        DetailsPanel.Children.Add(grid);
    }

    // ============================================================
    //  ACCIONES
    // ============================================================

    private void OnOpen(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected?.MountPoint == null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{_selected.MountPoint}\"",
                UseShellExecute = false
            });

            Console.WriteLine($"[SESSIONS] Abriendo mountpoint: {_selected.MountPoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS] Error al abrir mountpoint: {ex.Message}");
        }
    }

    private async void OnInit(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null) return;

        Console.WriteLine($"[SESSIONS] OnInit → {_selected.Iqn}");

        var dlg = new InitializeDiskDialog(_selected);
        await dlg.ShowDialog((Window)VisualRoot);

        await CargarSesiones();
    }

    private async void OnMount(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        Console.WriteLine($"[SESSIONS] OnMount → {_selected.Iqn}");

        if (_selected.Conectado)
            await IscsiHelper.Desconectar(_selected);
        else
            await IscsiHelper.Conectar(_selected);

        await CargarSesiones();
    }

    private async void OnDisconnect(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null) return;

        Console.WriteLine($"[SESSIONS] OnDisconnect → {_selected.Iqn}");
        await IscsiHelper.Desconectar(_selected);
        await CargarSesiones();
    }
}
