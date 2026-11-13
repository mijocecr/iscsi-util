using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views;

public partial class SessionsView : UserControl
{
    private List<SessionInfo> _sesiones = new();
    private SessionInfo? _selected;
    private long _loadId;
    private string _lastRescanStatus = string.Empty;

    public SessionsView()
    {
        InitializeComponent();
        
        // Asegurarnos de que al iniciar la vista la tarjeta de detalles esté oculta
        if (DetailsCard != null)
            DetailsCard.IsVisible = false;
    }

    public async Task CargarSesiones()
    {
        var id = ++_loadId;
        RefreshButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        SessionCountText.Text = "Loading sessions...";

        try
        {
            var nuevas = await IscsiSessions.ObtenerVistaGlobal();
            if (id != _loadId)
                return;

            var previous = _selected;
            var previousSessions = _sesiones;
            _sesiones = nuevas ?? new List<SessionInfo>();
            
            foreach (var session in _sesiones.Where(x => x.Connected))
            {
                var previousSession = previousSessions.FirstOrDefault(x =>
                    x.Connected &&
                    x.Iqn == session.Iqn &&
                    x.Portal == session.Portal &&
                    x.LunId == session.LunId);
                if (previousSession != null)
                    session.ConnectedSince = previousSession.ConnectedSince;
            }

            if (previous != null)
            {
                _selected = _sesiones.Find(x =>
                    x.Iqn.Equals(previous.Iqn, StringComparison.OrdinalIgnoreCase) &&
                    x.Portal.Equals(previous.Portal, StringComparison.OrdinalIgnoreCase) &&
                    (x.LunId == previous.LunId || previous.LunId == 0));
            }
            else
            {
                _selected = null;
            }

            // Seleccionar el primer target por defecto si no hay ninguno seleccionado previamente
            if (_selected == null && _sesiones.Count > 0)
            {
                _selected = _sesiones.First();
            }

            PintarLista();

            // Gestionar visibilidad y renderizado de la tarjeta de detalles
            if (_selected != null)
            {
                DetailsCard.IsVisible = true;
                PintarDetalles(_selected);
            }
            else
            {
                DetailsCard.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            if (id != _loadId)
                return;

            LogService.Error($"[SESSIONS] No se pudieron cargar las sesiones: {ex.Message}");
            SessionCountText.Text = "Could not load sessions";
            EmptyStateTitle.Text = "Unable to load sessions";
            EmptyStateDescription.Text = ex.Message;
            SessionsList.Children.Clear();
            EmptyState.IsVisible = true;
            DetailsCard.IsVisible = false;
            RescanButton.IsEnabled = false;
        }
        finally
        {
            if (id == _loadId)
            {
                RefreshButton.IsEnabled = true;
                RescanButton.IsEnabled = _sesiones.Any(s => s.Connected);
            }
        }
    }

    /// <summary>
    /// Botón inferior inteligente: conecta la sesión o monta el volumen según
    /// el estado actual. Cuando ya está montado, la apertura se hace con el
    /// botón Open de la cabecera para no duplicar acciones.
    /// </summary>
    private async void OnPrimaryAction(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var mounted = _selected.Connected && IscsiHelper.EstaMontado(_selected.MountPoint);

        if (!_selected.Connected)
        {
            // 1) Si está desconectado -> Conectar sesión iSCSI
            using (LoadingService.Show("Connecting..."))
            {
                await IscsiHelper.ConectarSesion(_selected);
            }
            await CargarSesiones();
            if (_selected != null) { PintarLista(); PintarDetalles(_selected); }
        }
        else if (!mounted && IscsiHelper.EsFilesystemMontable(_selected.Filesystem))
        {
            // 2) Si está conectado pero no montado -> Montar volumen
            using (LoadingService.Show("Mounting..."))
            {
                await IscsiHelper.MontarSesion(_selected);
            }
            await CargarSesiones();
            if (_selected != null) { PintarLista(); PintarDetalles(_selected); }
        }
    }

    private void PintarLista()
    {
        var filter = SearchBox.Text?.Trim();
        var visibleSessions = string.IsNullOrEmpty(filter)
            ? _sesiones
            : _sesiones.Where(session =>
                    session.Iqn.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Portal.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Device.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.MountPoint.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Filesystem.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Auth.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Vendor.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    session.Model.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // La lista refleja la jerarquía Target -> LUN -> dispositivo de bloques.
        var targets = IscsiSessions.AgruparPorTarget(visibleSessions);

        SessionsList.Children.Clear();
        foreach (var target in targets)
        {
            SessionsList.Children.Add(target.HasMultipleLuns
                ? CrearTarjetaMultiLun(target)
                : CrearTarjeta(target.Luns[0].Session));
        }

        var targetCount = _sesiones
            .Select(s => (
                Iqn: s.Iqn.ToLowerInvariant(),
                Portal: s.Portal.ToLowerInvariant()
            ))
            .Distinct()
            .Count();

        var connectedCount = _sesiones.Count(session => session.Connected);
        SessionCountText.Text =
            $"{targetCount} target{(targetCount == 1 ? "" : "s")}  ·  " +
            $"{_sesiones.Count} LUN{(_sesiones.Count == 1 ? "" : "s")}  ·  " +
            $"{connectedCount} connected";

        RescanButton.IsEnabled = connectedCount > 0;

        EmptyState.IsVisible = visibleSessions.Count == 0;
        if (visibleSessions.Count == 0)
        {
            EmptyStateTitle.Text = _sesiones.Count == 0 ? "No saved targets" : "No matching targets";
            EmptyStateDescription.Text = _sesiones.Count == 0
                ? "Targets discovered with open-iscsi will appear here."
                : "Try another search term.";
        }

        SessionsList.InvalidateMeasure();
        SessionsList.InvalidateVisual();
    }

    private Border CrearTarjeta(SessionInfo session)
    {
        var selected = ReferenceEquals(session, _selected);

        var displayName = ShortIqn(session.Iqn);
        var displayPortal = ShortPortal(session.Portal);

        var defaultTextColor = GetThemeBrush("SteamText", "#C7D5E0");
        var cardBackground = selected ? GetThemeBrush("SteamCard", "#2A475E") : new SolidColorBrush(Color.Parse("#151E28"));
        var borderBrush = selected ? new SolidColorBrush(Color.Parse("#4CFF4C")) : GetThemeBrush("SteamBorder", "#0F141A");

        var row = new Border
        {
            Background = cardBackground,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var contentPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 3,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = 13,
            FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Foreground = selected
                ? new SolidColorBrush(Color.Parse("#4CFF4C"))
                : defaultTextColor
        };

        var portalInfo = new TextBlock
        {
            Text = $"{displayPortal}  ·  LUN {session.LunId}",
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Foreground = defaultTextColor,
            Opacity = 0.7
        };

        contentPanel.Children.Add(name);
        contentPanel.Children.Add(portalInfo);
        row.Child = contentPanel;

        row.PointerPressed += (_, _) =>
        {
            _selected = session;
            _lastRescanStatus = string.Empty;

            // Al hacer clic en un target, mostramos la tarjeta de detalles y la rellenamos
            DetailsCard.IsVisible = true;
            
            PintarLista();
            PintarDetalles(session);
        };

        return row;
    }

    /// <summary>
    /// Tarjeta de un target que expone varios LUNs. El target es la cabecera
    /// y cada LUN se muestra como un hijo independiente y seleccionable.
    /// </summary>
    private Border CrearTarjetaMultiLun(IscsiTargetNode target)
    {
        var sessions = target.Luns.Select(l => l.Session).ToList();
        var anySelected = sessions.Any(s => ReferenceEquals(s, _selected));
        var defaultTextColor = GetThemeBrush("SteamText", "#C7D5E0");

        var container = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151E28")),
            BorderBrush = anySelected
                ? new SolidColorBrush(Color.Parse("#4CFF4C"))
                : GetThemeBrush("SteamBorder", "#0F141A"),
            BorderThickness = new Thickness(anySelected ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var panel = new StackPanel { Spacing = 6 };

        var header = new StackPanel { Spacing = 2 };

        header.Children.Add(new TextBlock
        {
            Text = ShortIqn(target.Iqn),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = anySelected
                ? new SolidColorBrush(Color.Parse("#4CFF4C"))
                : defaultTextColor
        });

        header.Children.Add(new TextBlock
        {
            Text = $"{ShortPortal(target.Portal)}  ·  {target.LunCount} LUNs",
            FontSize = 11,
            Opacity = 0.7,
            Foreground = defaultTextColor
        });

        panel.Children.Add(header);

        foreach (var lun in target.Luns)
            panel.Children.Add(CrearFilaLun(lun.Session));

        container.Child = panel;
        return container;
    }

    private Border CrearFilaLun(SessionInfo session)
    {
        var selected = ReferenceEquals(session, _selected);
        var defaultTextColor = GetThemeBrush("SteamText", "#C7D5E0");

        var row = new Border
        {
            Background = selected
                ? GetThemeBrush("SteamCard", "#2A475E")
                : new SolidColorBrush(Color.Parse("#101820")),
            BorderBrush = selected
                ? new SolidColorBrush(Color.Parse("#4CFF4C"))
                : GetThemeBrush("SteamBorder", "#0F141A"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var detail = session.Connected
            ? $"{EmptyAsDash(session.Device)}  ·  {EmptyAsDash(session.Filesystem)}  ·  " +
              $"{(session.SizeBytes > 0 ? IscsiRescan.FormatBytes(session.SizeBytes) : session.SizeGb > 0 ? FormatSize(session.SizeGb) : "-")}"
            : "Saved configuration (not connected)";

        var text = new StackPanel { Spacing = 1 };

        text.Children.Add(new TextBlock
        {
            Text = $"LUN {session.LunId}",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = selected
                ? new SolidColorBrush(Color.Parse("#4CFF4C"))
                : GetThemeBrush("SteamAccent", "#66C0F4")
        });

        text.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 10,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = defaultTextColor
        });

        row.Child = text;

        row.PointerPressed += (_, _) =>
        {
            _selected = session;
            _lastRescanStatus = string.Empty;
            DetailsCard.IsVisible = true;
            PintarLista();
            PintarDetalles(session);
        };

        return row;
    }

    private static string ShortIqn(string iqn)
    {
        var colonIndex = iqn.LastIndexOf(':');
        return colonIndex >= 0 && colonIndex < iqn.Length - 1
            ? iqn[(colonIndex + 1)..]
            : iqn;
    }

    private static string ShortPortal(string portal)
    {
        var portIndex = portal.LastIndexOf(':');
        return portIndex >= 0 ? portal[..portIndex] : portal;
    }

    private void PintarDetalles(SessionInfo session)
    {
        // Asegurarnos de que la tarjeta seja visible al pintar detalles
        DetailsCard.IsVisible = true;

        SessionTitle.Text = session.Iqn;
        SessionTitle.TextWrapping = TextWrapping.Wrap;

        // Verificación real del punto de montaje (findmnt/mountpoint).
        var mounted = session.Connected && IscsiHelper.EstaMontado(session.MountPoint);
        
        SessionSubtitle.IsVisible = true;
        SessionSubtitle.Text = mounted ? "MOUNTED" : session.Connected ? "CONNECTED" : "SAVED";
        SessionSubtitle.Foreground = session.Connected
            ? GetThemeBrush("SteamGreen", "#8BC53F")
            : GetThemeBrush("SteamText", "#C7D5E0");

        // Control del botón Open integrado justo a la izquierda del estado en la cabecera
        if (BtnOpenInline != null)
        {
            BtnOpenInline.IsVisible = mounted;
        }

        DetailsPanel.Children.Clear();
        
        AddWideDetail("PORTAL", EmptyAsDash(session.Portal));
        AddWideDetail("DEVICE", EmptyAsDash(session.Device));
        AddWideDetail("MOUNT POINT", EmptyAsDash(session.MountPoint), isCopyable: true); // Permitir copiar el mount point
        AddWideDetail("FILESYSTEM", EmptyAsDash(session.Filesystem));
        AddWideDetail("AUTHENTICATION", EmptyAsDash(session.Auth));

        // El LUN se mantiene como metadato técnico secundario, incluso cuando
        // el target solo expone una unidad lógica.
        AddWideDetail("LUN", session.LunId.ToString());
        AddWideDetail("SIZE", session.SizeBytes > 0
            ? IscsiRescan.FormatBytes(session.SizeBytes)
            : session.SizeGb > 0 ? FormatSize(session.SizeGb) : "-");

        // Métrica modificada para mostrar la primera vez que se conectó en lugar de "CONNECTED SINCE"
        AddWideDetail("FIRST CONNECTED", session.ConnectedSince != default
            ? session.ConnectedSince.ToString("yyyy-MM-dd HH:mm:ss")
            : "-");

        RescanStatusText.Text = _lastRescanStatus;
        RescanStatusText.IsVisible = !string.IsNullOrWhiteSpace(_lastRescanStatus);

        // Configuración limpia de los botones inferiores (máximo 2 visibles según el estado)
        if (!session.Connected)
        {
            // 1. Desconectado: Solo el botón Connect
            BtnAction.IsVisible = true;
            BtnAction.Content = "Connect";
            BtnAction.Background = GetThemeBrush("SteamAccent", "#66C0F4");
            BtnAction.BorderBrush = GetThemeBrush("SteamAccent", "#66C0F4");
            BtnAction.IsEnabled = true;

            BtnRescan.IsVisible = false;
            BtnUnmount.IsVisible = false;
            BtnDisconnect.IsVisible = false;
        }
        else if (!mounted)
        {
            // 2. Conectado pero sin montar: Botón Mount + Rescan + Disconnect
            BtnAction.IsVisible = true;
            BtnAction.Content = "Mount";
            BtnAction.Background = GetThemeBrush("SteamCard", "#2A475E");
            BtnAction.BorderBrush = GetThemeBrush("SteamBorder", "#0F141A");
            BtnAction.IsEnabled = IscsiHelper.EsFilesystemMontable(session.Filesystem);

            BtnRescan.IsVisible = true;
            BtnUnmount.IsVisible = false;
            BtnDisconnect.IsVisible = true;
            BtnDisconnect.IsEnabled = true;
        }
        else
        {
            // 3. Montado: Ocultamos el botón principal inferior (porque "Open" ya está arriba) 
            // y mostramos Rescan, Unmount y Disconnect (este último bloqueado hasta desmontar).
            BtnAction.IsVisible = false; 

            BtnRescan.IsVisible = true;
            BtnUnmount.IsVisible = true;
            BtnDisconnect.IsVisible = true;
            BtnDisconnect.IsEnabled = false; 
        }

        ToolTip.SetTip(BtnDisconnect, mounted
            ? "Unmount the volume before disconnecting."
            : "Disconnect the iSCSI session without deleting its saved configuration. " +
              "The session is shared by every LUN of this target.");
    }
    
  
    private void AddWideDetail(string label, string value, bool isCopyable = false)
    {
        var border = new Border
        {
            Background = GetThemeBrush("SteamCard", "#2A475E"),
            BorderBrush = GetThemeBrush("SteamBorder", "#0F141A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var stack = new StackPanel 
        { 
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 2,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = GetThemeBrush("SteamAccent", "#66C0F4")
        });

        if (isCopyable)
        {
            // TextBox de solo lectura transparente con ajuste de línea (TextWrapping)
            stack.Children.Add(new TextBox
            {
                Text = value,
                FontSize = 11,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, // <--- Ajuste de línea activado
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 0, 0, 0),
                Foreground = GetThemeBrush("SteamText", "#C7D5E0")
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetThemeBrush("SteamText", "#C7D5E0")
            });
        }

        border.Child = stack;
        DetailsPanel.Children.Add(border);
    }
    
  
    private IBrush GetThemeBrush(string key, string fallbackHex)
    {
        if (TryGetResource(key, ActualThemeVariant, out var res) && res is IBrush brush)
            return brush;

        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var appRes) == true && appRes is IBrush appBrush)
            return appBrush;

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    private static string EmptyAsDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatSize(double sizeGb)
    {
        if (sizeGb >= 1024)
            return $"{sizeGb / 1024.0:0.##} TB";
        if (sizeGb < 1)
            return $"{sizeGb * 1024.0:0.##} MB";
        return $"{sizeGb:0.##} GB";
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        PintarLista();
    }

    private async void RefreshButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _lastRescanStatus = string.Empty;
        await CargarSesiones();
    }

    private async void RescanButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_sesiones.Any(s => s.Connected))
        {
            NotificadorLinux.Enviar("No connected targets to rescan.", 4000, "low");
            return;
        }

        RescanButton.IsEnabled = false;
        try
        {
            List<TargetRescanResult> results;
            using (LoadingService.Show("Rescanning targets..."))
            {
                results = await Task.Run(() => IscsiRescan.RescanAllAsync(_sesiones));
            }

            _lastRescanStatus = BuildRescanStatus(results);
            await CargarSesiones();
            NotificarRescan(results);
        }
        finally
        {
            RescanButton.IsEnabled = _sesiones.Any(s => s.Connected);
        }
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || !_selected.Connected)
            return;

        var target = _selected;
        BtnRescan.IsEnabled = false;
        try
        {
            TargetRescanResult result;
            using (LoadingService.Show("Rescanning target..."))
            {
                result = await Task.Run(() => IscsiRescan.RescanTargetAsync(target.Iqn, target.Portal, _sesiones));
            }

            _lastRescanStatus = BuildRescanStatus(new[] { result });
            await CargarSesiones();
            NotificarRescan(new[] { result });
        }
        finally
        {
            BtnRescan.IsEnabled = true;
        }
    }

    private static string BuildRescanStatus(IReadOnlyList<TargetRescanResult> results)
    {
        if (results.Count == 0)
            return "No connected targets to rescan.";

        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
            return $"Rescan failed: {failed[0].Message}";

        var changed = results.Where(r => r.HasChanges).ToList();
        if (changed.Count == 0)
            return "Rescan finished: no LUN or size changes detected.";

        return string.Join(Environment.NewLine,
            changed.Select(r => $"{r.DisplayTarget}: {r.Summary}"));
    }

    private static void NotificarRescan(IReadOnlyList<TargetRescanResult> results)
    {
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            NotificadorLinux.Enviar($"Rescan failed: {failed[0].Message}", 7000, "critical");
            return;
        }

        var changed = results.Where(r => r.HasChanges).ToList();
        if (changed.Count == 0)
        {
            NotificadorLinux.Enviar("Rescan finished: no LUN or size changes detected.", 4000, "low");
            return;
        }

        var summary = string.Join(" | ",
            changed.Select(r => $"{r.DisplayTarget}: {r.ShortSummary}"));

        NotificadorLinux.Enviar(summary, 8000, "normal");
    }

    // Manejador del botón Open rápido situado junto a la etiqueta MOUNTED
    private void OnOpenInline(object? sender, RoutedEventArgs e)
    {
        if (_selected == null ||
            string.IsNullOrWhiteSpace(_selected.MountPoint) ||
            !System.IO.Directory.Exists(_selected.MountPoint))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _selected.MountPoint,
            UseShellExecute = true
        });
    }

    private async void OnUnmount(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        using (LoadingService.Show("Unmounting..."))
        {
            await IscsiHelper.DesmontarSesion(_selected);
        }

        await CargarSesiones();

        if (_selected != null)
        {
            PintarLista();
            PintarDetalles(_selected);
        }
        else
        {
            DetailsCard.IsVisible = false;
        }
    }

    private async void OnDisconnect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        using (LoadingService.Show("Disconnecting..."))
        {
            await IscsiHelper.DesconectarSesion(_selected);
        }

        await CargarSesiones();

        if (_selected != null)
        {
            PintarLista();
            PintarDetalles(_selected);
        }
        else
        {
            DetailsCard.IsVisible = false;
        }
    }
}