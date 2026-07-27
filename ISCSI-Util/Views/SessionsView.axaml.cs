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
    //   HELPER: MOUNTPOINT PERSISTENTE
    // ============================================================

    private string ObtenerMountpointPersistente(IscsiDestino d)
    {
        string safe = IscsiHelper.SanitizarNombre(d.Iqn)
            .Replace('.', '_')
            .Replace('-', '_');

        return Path.Combine(ConfigManager.MountBasePath, safe);
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

            var redesLocales = NetworkHelper.ObtenerRedesLocales();

            _destinos = _destinos
                .Where(d => redesLocales.Any(r => d.Ip.StartsWith(r)))
                .ToList();

            foreach (var d in _destinos)
            {
                d.EsAccesible = redesLocales.Any(r => d.Ip.StartsWith(r));

                await IscsiHelper.CompletarInformacionDestino(d, id);
                d.Persistir = IscsiHelper.DetectarPersistencia(d);
            }

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

        // IQN
        panel.Children.Add(new TextBlock
        {
            Text = d.Iqn,
            Classes = { "IqnList" },
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        });

        // Portal real
        panel.Children.Add(new TextBlock
        {
            Text = d.PortalReal ?? d.Ip,
            Foreground = Brushes.WhiteSmoke,
            FontSize = 12
        });

        // FS + mount runtime
        string fs = d.Conectado && d.TieneFilesystem ? d.FsType : "RAW";
        string mpRuntime = d.Conectado && !string.IsNullOrWhiteSpace(d.MountPoint)
            ? d.MountPoint!
            : "(no mount)";

        panel.Children.Add(new TextBlock
        {
            Text = $"{fs} | {mpRuntime}",
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

        // Sesión
        AddDetail("IQN:", d.Iqn, true);
        AddDetail("Portal:", d.PortalReal ?? d.Ip);
        AddDetail("Status:", d.Conectado ? "Connected" : "Disconnected");

        // Disco
        AddDetail("Device:", d.Conectado && !string.IsNullOrWhiteSpace(d.DevicePath) ? d.DevicePath! : "-");
        AddDetail("Partition:", d.Conectado && !string.IsNullOrWhiteSpace(d.PartitionPath) ? d.PartitionPath! : "-");
        AddDetail("FS:", d.Conectado && d.TieneFilesystem ? d.FsType : "RAW");

        // Mount runtime
        string mpRuntime = d.Conectado && !string.IsNullOrWhiteSpace(d.MountPoint)
            ? d.MountPoint!
            : "-";
        AddDetail("Mount (runtime):", mpRuntime);

        // Mount persistente
        string mpPersistente = ObtenerMountpointPersistente(d);
        string mpPersistShow = d.Persistir ? mpPersistente : "-";
        AddDetail("Mount (persistent):", mpPersistShow);

        // Persistencia
        AddDetail("Persist:", d.Persistir ? "Yes" : "No");

        // Botón Open
        string mpAbrir = null;

        if (d.Conectado && !string.IsNullOrWhiteSpace(d.MountPoint) && Directory.Exists(d.MountPoint))
            mpAbrir = d.MountPoint;
        else if (d.Persistir && Directory.Exists(mpPersistente))
            mpAbrir = mpPersistente;

        BtnOpen.IsEnabled = !string.IsNullOrWhiteSpace(mpAbrir);

        BtnOpen.Click -= OnOpen;
        BtnOpen.Click += OnOpen;

        // Botón Mount / Unmount
        BtnMount.IsEnabled = d.EsAccesible;
        BtnMount.Content = d.Conectado ? "Unmount" : "Mount";

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
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
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

    private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        string mpPersistente = ObtenerMountpointPersistente(_selected);

        string mpAbrir =
            _selected.Conectado && !string.IsNullOrWhiteSpace(_selected.MountPoint)
                ? _selected.MountPoint
                : (_selected.Persistir ? mpPersistente : null);

        if (string.IsNullOrWhiteSpace(mpAbrir))
            return;

        if (!Directory.Exists(mpAbrir))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = mpAbrir,
            UseShellExecute = true
        });
    }
}
