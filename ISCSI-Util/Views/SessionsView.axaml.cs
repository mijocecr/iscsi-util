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
        }
        finally
        {
            if (id == _loadId)
                RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Botón Único Inteligente inferior: Conecta, Monta o Abre la carpeta según el estado actual.
    /// </summary>
    private async void OnPrimaryAction(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var mountsRaw = ShellHelper.EjecutarComoRoot("mount").Stdout ?? "";
        var mounted = _selected.Connected &&
                      !string.IsNullOrWhiteSpace(_selected.MountPoint) &&
                      mountsRaw.Contains($"on {_selected.MountPoint} ", StringComparison.OrdinalIgnoreCase);

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
        else if (mounted)
        {
            // 3) Si ya está montado -> Abrir el volumen en el explorador de archivos
            if (!string.IsNullOrWhiteSpace(_selected.MountPoint) && System.IO.Directory.Exists(_selected.MountPoint))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _selected.MountPoint,
                    UseShellExecute = true
                });
            }
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

        SessionsList.Children.Clear();
        foreach (var session in visibleSessions)
            SessionsList.Children.Add(CrearTarjeta(session));

        var connectedCount = _sesiones.Count(session => session.Connected);
        SessionCountText.Text = $"{_sesiones.Count} target{(_sesiones.Count == 1 ? "" : "s")}  ·  {connectedCount} connected";

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

        var displayName = session.Iqn;
        var colonIndex = session.Iqn.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < session.Iqn.Length - 1)
        {
            displayName = session.Iqn.Substring(colonIndex + 1);
        }

        var displayPortal = session.Portal;
        var portIndex = session.Portal.LastIndexOf(':');
        if (portIndex >= 0)
        {
            displayPortal = session.Portal.Substring(0, portIndex);
        }

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
            
            // Al hacer clic en un target, mostramos la tarjeta de detalles y la rellenamos
            DetailsCard.IsVisible = true;
            
            PintarLista();
            PintarDetalles(session);
        };

        return row;
    }
    
    private void PintarDetalles(SessionInfo session)
    {
        // Asegurarnos de que la tarjeta sea visible al pintar detalles
        DetailsCard.IsVisible = true;

        SessionTitle.Text = session.Iqn;
        SessionTitle.TextWrapping = TextWrapping.Wrap;

        // Verificación real contra el comando mount de Linux
        var mountsRaw = ShellHelper.EjecutarComoRoot("mount").Stdout ?? "";
        var mounted = session.Connected &&
                      !string.IsNullOrWhiteSpace(session.MountPoint) &&
                      mountsRaw.Contains($"on {session.MountPoint} ", StringComparison.OrdinalIgnoreCase);
        
        SessionSubtitle.IsVisible = true;
        SessionSubtitle.Text = mounted ? "MOUNTED" : session.Connected ? "CONNECTED" : "SAVED";
        SessionSubtitle.Foreground = mounted 
            ? GetThemeBrush("SteamGreen", "#8BC53F") 
            : session.Connected 
                ? GetThemeBrush("SteamBlue", "#66C0F4") 
                : GetThemeBrush("SteamText", "#C7D5E0");

        // Control del botón Open integrado justo a la izquierda del estado en la cabecera
        if (BtnOpenInline != null)
        {
            BtnOpenInline.IsVisible = mounted;
        }

        DetailsPanel.Children.Clear();
        
        AddWideDetail("DEVICE", EmptyAsDash(session.Device));
        AddWideDetail("MOUNT POINT", EmptyAsDash(session.MountPoint), isCopyable: true); // Permitir copiar el mount point
        AddWideDetail("FILESYSTEM", EmptyAsDash(session.Filesystem));
        AddWideDetail("AUTHENTICATION", EmptyAsDash(session.Auth));
        AddWideDetail("SIZE", session.SizeGb > 0 ? FormatSize(session.SizeGb) : "-");
        AddWideDetail("LUN", session.LunId.ToString());
        AddWideDetail("CONNECTED SINCE", session.Connected && session.ConnectedSince != default
            ? session.ConnectedSince.ToString("yyyy-MM-dd HH:mm:ss")
            : "-");
        
        // Configuración limpia de los botones inferiores (máximo 2 visibles según el estado)
        if (!session.Connected)
        {
            // 1. Desconectado: Solo el botón Connect
            BtnAction.IsVisible = true;
            BtnAction.Content = "Connect";
            BtnAction.Background = GetThemeBrush("SteamAccent", "#66C0F4");
            BtnAction.BorderBrush = GetThemeBrush("SteamAccent", "#66C0F4");
            BtnAction.IsEnabled = true;

            BtnUnmount.IsVisible = false;
            BtnDisconnect.IsVisible = false;
        }
        else if (!mounted)
        {
            // 2. Conectado pero sin montar: Botón Mount + Botón Disconnect
            BtnAction.IsVisible = true;
            BtnAction.Content = "Mount";
            BtnAction.Background = GetThemeBrush("SteamCard", "#2A475E");
            BtnAction.BorderBrush = GetThemeBrush("SteamBorder", "#0F141A");
            BtnAction.IsEnabled = IscsiHelper.EsFilesystemMontable(session.Filesystem);

            BtnUnmount.IsVisible = false;
            BtnDisconnect.IsVisible = true;
            BtnDisconnect.IsEnabled = true;
        }
        else
        {
            // 3. Montado: Ocultamos el botón principal inferior (porque "Open" ya está arriba) 
            // y mostramos únicamente Unmount y Disconnect (este último bloqueado hasta desmontar).
            BtnAction.IsVisible = false; 

            BtnUnmount.IsVisible = true;
            BtnDisconnect.IsVisible = true;
            BtnDisconnect.IsEnabled = false; 
        }

        ToolTip.SetTip(BtnDisconnect, mounted
            ? "Unmount the volume before disconnecting."
            : "Disconnect the iSCSI session without deleting its saved configuration.");
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
        await CargarSesiones();
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