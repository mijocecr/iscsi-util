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
            var nuevos = await Iscsi_Sessions_Helper.ObtenerVistaGlobal();
            _destinos = nuevos ?? new List<IscsiDestino>();

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
        AddDetail("Vendor:", d.Vendor ?? "-");
        AddDetail("Model:", d.Model ?? "-");
        AddDetail("Persist:", d.Persistir ? "Yes" : "No");

        // ============================
        // BOTONES (LÓGICA CORRECTA)
        // ============================

        BtnOpen.IsEnabled = false;
        BtnInit.IsEnabled = false;

        BtnMount.IsEnabled = true;
        BtnMount.Content = d.Conectado ? "Unmount" : "Mount";

        BtnDisconnect.IsVisible = false;

        BtnMount.Click -= OnMount;
        BtnMount.Click += OnMount;
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
}
