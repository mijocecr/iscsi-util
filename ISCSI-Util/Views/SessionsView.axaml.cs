using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;
using System.IO;
using System.Linq;

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
    //   CARGAR SESIONES + NODOS
    // ============================================================
   
    public async Task CargarSesiones()
    {
        long id = ++_loadId;
        Console.WriteLine($"[SESSIONS] #{id} → CargarSesiones()");

        try
        {
            var nuevos = await IscsiSessions.ObtenerVistaGlobal();
            _destinos = nuevos ?? new List<IscsiDestino>();

            // ------------------------------------------------------
            // FILTRAR DESTINOS POR REDES ACCESIBLES
            // ------------------------------------------------------
            var redesLocales = NetworkHelper.ObtenerRedesLocales();

            _destinos = _destinos
                .Where(d => redesLocales.Any(r => d.Ip.StartsWith(r)))
                .ToList();

            // ------------------------------------------------------
            // COMPLETAR INFORMACIÓN (LO QUE FALTABA)
            // ------------------------------------------------------
            foreach (var d in _destinos)
            {
                d.EsAccesible = redesLocales.Any(r => d.Ip.StartsWith(r));

                
                await IscsiHelper.CompletarInformacionDestino(d, 0);
                d.Persistir = IscsiHelper.DetectarPersistencia(d);
            }

            // Mantener selección si existe
            if (_selected != null)
                _selected = _destinos.Find(x => x.Iqn == _selected.Iqn && x.Ip == _selected.Ip);

            PintarLista();

            if (_selected != null)
                PintarDetalles(_selected);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SESSIONS] ERROR: {ex.Message}");
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
            TextAlignment = TextAlignment.Left,
           // TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
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
            PintarLista();
            if (_selected != null)
                PintarDetalles(_selected);
        };

        return border;
    }

    // ============================================================
    //   PINTAR DETALLES
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
       // AddDetail("Vendor:", d.Vendor ?? "-");
       // AddDetail("Model:", d.Model ?? "-");
        AddDetail("Persist:", d.Persistir ? "Yes" : "No");

        // ============================
        // BOTONES (LÓGICA CORRECTA)
        // ============================

        // OPEN solo si está montado
        BtnOpen.IsEnabled =
            d.Conectado &&
            !string.IsNullOrWhiteSpace(d.MountPoint) &&
            Directory.Exists(d.MountPoint);

        // MOUNT solo si el destino es accesible
        BtnMount.IsEnabled = d.EsAccesible;

        // Texto del botón
        BtnMount.Content = d.Conectado ? "Unmount" : "Mount";

        // Eventos
        BtnMount.Click -= OnMount;
        BtnMount.Click += OnMount;

        BtnOpen.Click -= OnOpen;
        BtnOpen.Click += OnOpen;
    }

    private void AddDetail(string label, string value, bool wrap = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("70,190"),
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
    private async void OnMount(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        if (_selected.Conectado)
        {
            using (LoadingService.Show("Unmounting..."))
                await IscsiHelper.Desconectar(_selected);
        }
        else
        {
            using (LoadingService.Show("Connecting..."))
                await IscsiHelper.Conectar(_selected);
        }

        await CargarSesiones();
    }

    private void OnOpen(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        if (!string.IsNullOrWhiteSpace(_selected.MountPoint) &&
            Directory.Exists(_selected.MountPoint))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _selected.MountPoint,
                UseShellExecute = true
            });
        }
    }
}
