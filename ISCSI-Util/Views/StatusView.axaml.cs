using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace ISCSI_Util.Views
{
    public partial class StatusView : UserControl
    {
        private readonly DispatcherTimer _metricsTimer = new()
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        private bool _isRefreshing;
        private bool _metricsRefreshing;
        private bool _hasLoadedStatus;
        private string _lastSummaryState = "";

        private int _activeSessions;
        private int _activeTargets;
        private int _activeLuns;

        private ThroughputSample? _lastThroughputSample;

        private sealed record ThroughputSample(
            DateTime At,
            long ReadBytes,
            long WriteBytes,
            long ReadIos,
            long WriteIos,
            int Devices,
            string Signature);

        public StatusView()
        {
            InitializeComponent();
            HookButtons();

            _metricsTimer.Tick += OnMetricsTick;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _metricsTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _metricsTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private async void OnMetricsTick(object? sender, EventArgs e)
        {
            if (!IsEffectivelyVisible || _isRefreshing || _metricsRefreshing)
                return;

            try
            {
                await RefreshMetricsAsync();
            }
            catch (Exception ex)
            {
                TraceError($"OnMetricsTick(): {ex.Message}");
            }
        }

        // La traza de esta vista va al log (solo con Verbose activado) en vez
        // de a la consola, para que el diagnóstico quede junto al resto.
        private static void Trace(string message)
            => LogService.Debug($"[STATUS] {message}");

        private static void TraceError(string message)
            => LogService.Error($"[STATUS] {message}");

        private static void SetIndicator(Ellipse icon, TextBlock label, string text, IBrush color)
        {
            icon.Fill = color;
            label.Text = text;
            label.Foreground = color;
        }

        private static readonly Color OkColor = Color.Parse("#8BC53F");
        private static readonly Color WarningColor = Color.Parse("#E6A23C");
        private static readonly Color CriticalColor = Color.Parse("#E05A5A");

        private static readonly Color OkBackground = Color.Parse("#1E4620");
        private static readonly Color WarningBackground = Color.Parse("#4A3A12");
        private static readonly Color CriticalBackground = Color.Parse("#5A1E1E");

        private void SetSummary(string state, string detail, Color foreground, Color background)
        {
            var brush = new SolidColorBrush(foreground);

            SummaryBorder.Background = new SolidColorBrush(background);
            SummaryBorder.BorderBrush = brush;
            TxtSummary.Foreground = brush;
            TxtSummaryDetail.Foreground = brush;
            TxtSummary.Text = $"SYSTEM STATUS: {state}";
            TxtSummaryDetail.Text = detail;
        }

        private static string Capitalize(string text)
            => string.IsNullOrEmpty(text)
                ? text
                : text[..1].ToUpperInvariant() + text[1..];

        private static string FormatCount(int value, string singular, string plural)
            => $"{value} {(value == 1 ? singular : plural)}";

        private static string FormatIops(double value)
            => value >= 1000
                ? $"{value / 1000d:F1}k"
                : $"{value:F0}";

        // Lee un valor de /proc sin depender de herramientas externas ni del
        // idioma del sistema. Es la fuente más portable en Linux moderno.
        private static string? ReadProcValue(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var value = File.ReadAllText(path).Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception ex)
            {
                TraceError($"ReadProcValue('{path}'): {ex.Message}");
                return null;
            }
        }

        // Primer campo de /proc/uptime: segundos desde el arranque.
        internal static double? ParseUpSeconds(string? procUptime)
        {
            if (string.IsNullOrWhiteSpace(procUptime))
                return null;

            var first = procUptime
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

            return double.TryParse(first, NumberStyles.Float,
                       CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                ? seconds
                : null;
        }

        internal static string FormatUptime(double? seconds)
        {
            if (seconds is null || !double.IsFinite(seconds.Value) || seconds.Value < 0)
                return "Unknown";

            var ts = TimeSpan.FromSeconds(seconds.Value);
            var parts = new List<string>();

            if (ts.Days > 0)
                parts.Add($"{ts.Days} {(ts.Days == 1 ? "day" : "days")}");
            if (ts.Hours > 0)
                parts.Add($"{ts.Hours} {(ts.Hours == 1 ? "hour" : "hours")}");
            if (ts.Minutes > 0)
                parts.Add($"{ts.Minutes} {(ts.Minutes == 1 ? "minute" : "minutes")}");
            if (parts.Count == 0)
                parts.Add($"{ts.Seconds} {(ts.Seconds == 1 ? "second" : "seconds")}");

            return string.Join(", ", parts);
        }

        // Extrae la IP del gateway de `ip route show default` sin depender de
        // `awk`, que no está garantizado en instalaciones mínimas.
        internal static string? SelectDefaultGateway(string? ipRouteOutput)
        {
            if (string.IsNullOrWhiteSpace(ipRouteOutput))
                return null;

            foreach (var line in ipRouteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int via = Array.IndexOf(parts, "via");

                if (via >= 0 && via + 1 < parts.Length)
                    return parts[via + 1];
            }

            return null;
        }

        // Con LC_ALL=C fijado por ShellHelper, la salida de ping siempre trae
        // "time=12.3 ms" (nunca "tiempo=").
        internal static string? ParseLatencyMs(string? pingOutput)
        {
            if (string.IsNullOrWhiteSpace(pingOutput))
                return null;

            int idx = pingOutput.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            var value = pingOutput[(idx + 5)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return string.IsNullOrEmpty(value) ? null : value;
        }

        // Texto de ayuda que se muestra bajo el estado de servicios cuando
        // iscsid no está activo, con el comando exacto para habilitarlo y
        // arrancarlo. Es puro para poder probarlo: devuelve null si no aplica.
        internal static string? ServiceHintFor(string? iscsidText)
        {
            if (string.IsNullOrWhiteSpace(iscsidText))
                return null;

            var text = iscsidText.Trim();

            // En ejecución (o arrancando) no hay nada que sugerir.
            if (text.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Starting", StringComparison.OrdinalIgnoreCase))
                return null;

            if (text.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return "Cannot check the iscsid service: systemd may not be available. " +
                       "Start it with your init system.";

            return "The iscsid service is not running. Use \"Start Service\" below, " +
                   "or run: sudo systemctl enable --now iscsid";
        }

        // La fila "Last Update" muestra siempre la hora de la última lectura
        // correcta del estado. Los resultados de las acciones van en el banner
        // de estado, no aquí.
        private void SetLastUpdate()
            => TxtLastOp.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        // El botón de recarga se convierte en "Start Service" cuando el daemon
        // está parado o fallido. En el resto de estados solo recarga el estado.
        internal static bool NeedsStart(string? iscsidText)
        {
            var text = iscsidText?.Trim();
            return text?.Equals("Stopped", StringComparison.OrdinalIgnoreCase) == true ||
                   text?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true;
        }

        private void ApplyServiceHint()
        {
            var hint = ServiceHintFor(TxtIscsid.Text);
            TxtServiceHint.Text = hint ?? "";
            TxtServiceHint.IsVisible = hint is not null;

            // Solo cambia la etiqueta; el tooltip es fijo y describe las dos
            // acciones del botón para que nunca quede desincronizado de ella.
            BtnReload.Content = NeedsStart(TxtIscsid.Text) ? "Start Service" : "Reload";
        }

        public async Task RefreshStatus()
        {
            if (_isRefreshing)
            {
                Trace("RefreshStatus() ignorado: ya hay un refresco en curso.");
                return;
            }

            _isRefreshing = true;
            SetButtonsEnabled(false);

            try
            {
                Trace("RefreshStatus() → LoadStatus()");
                await LoadStatus();
                Trace("RefreshStatus() ← OK");
            }
            catch (Exception ex)
            {
                TraceError($"RefreshStatus(): {ex.Message}");
                SetSummary("Error", ex.Message, CriticalColor, CriticalBackground);
            }
            finally
            {
                _isRefreshing = false;
                SetButtonsEnabled(true);
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            BtnReload.IsEnabled = enabled;
            BtnRestart.IsEnabled = enabled;
        }

        private void HookButtons()
        {
            // Un único botón: si el daemon está parado o fallido lo arranca;
            // en cualquier otro estado solo recarga. La etiqueta la actualiza
            // ApplyServiceHint() según TxtIscsid.Text.
            BtnReload.Click += async (_, _) =>
            {
                if (NeedsStart(TxtIscsid.Text))
                    await StartIscsidAsync();
                else
                    await RefreshStatus();
            };

            BtnRestart.Click += async (_, _) =>
            {
                if (!await ConfirmRestartAsync())
                {
                    Trace("BtnRestart: cancelado por el usuario.");
                    return;
                }

                Trace("BtnRestart: systemctl restart iscsid");
                SetButtonsEnabled(false);

                try
                {
                    var restart = await SystemOperationLock.RunAsync(
                        () => ShellHelper.EjecutarComoRootAsync("systemctl restart iscsid"));

                    if (restart.ExitCode != 0)
                    {
                        var detail = string.IsNullOrWhiteSpace(restart.Stderr)
                            ? $"exit code {restart.ExitCode}"
                            : restart.Stderr.Trim();

                        throw new Exception(detail);
                    }

                    SetSummary("Updating", "Restarting the iSCSI service...",
                        WarningColor, WarningBackground);

                    // Esperar a que iscsid vuelva a estar activo en vez de
                    // asumir que 4 segundos siempre son suficientes.
                    if (!await WaitForDaemonReadyAsync(TimeSpan.FromSeconds(10)))
                        TraceError("BtnRestart: iscsid no volvió a estar activo tras el reinicio.");

                    await RefreshStatus();
                }
                catch (Exception ex)
                {
                    TraceError($"BtnRestart: {ex.Message}");
                    SetSummary("Error", $"Restart failed: {ex.Message}",
                        CriticalColor, CriticalBackground);
                }
                finally
                {
                    SetButtonsEnabled(true);
                }
            };
        }

        // Arranca y habilita iscsid en un solo paso. A diferencia de Restart,
        // no corta sesiones activas, así que no pide confirmación.
        private async Task StartIscsidAsync()
        {
            Trace("StartIscsidAsync: systemctl enable --now iscsid");
            SetButtonsEnabled(false);

            try
            {
                var start = await SystemOperationLock.RunAsync(
                    () => ShellHelper.EjecutarComoRootAsync("systemctl enable --now iscsid"));

                if (start.ExitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(start.Stderr)
                        ? $"exit code {start.ExitCode}"
                        : start.Stderr.Trim();

                    throw new Exception(detail);
                }

                SetSummary("Updating", "Starting the iSCSI service...",
                    WarningColor, WarningBackground);

                if (!await WaitForDaemonReadyAsync(TimeSpan.FromSeconds(10)))
                    TraceError("StartIscsidAsync: iscsid no se activó tras el arranque.");

                await RefreshStatus();
            }
            catch (Exception ex)
            {
                TraceError($"StartIscsidAsync: {ex.Message}");
                SetSummary("Error", $"Start failed: {ex.Message}",
                    CriticalColor, CriticalBackground);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        /// <summary>
        /// Reiniciar iscsid puede cortar sesiones y volúmenes montados, así que
        /// se pide confirmación explícita antes de ejecutar la acción.
        /// </summary>
        private async Task<bool> ConfirmRestartAsync()
        {
            if (this.GetVisualRoot() is not Window owner)
                return false;

            var dialog = new Window
            {
                Width = 420,
                Height = 190,
                Title = "Restart iSCSI service",
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var panel = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Restarting iscsid can interrupt active iSCSI sessions and " +
                       "mounted volumes.\n\nDo you want to continue?",
                TextWrapping = TextWrapping.Wrap
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 100,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            var confirm = new Button
            {
                Content = "Restart",
                Width = 110,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#3A1518")),
                BorderBrush = new SolidColorBrush(Color.Parse("#D9534F")),
                BorderThickness = new Thickness(2),
                Foreground = new SolidColorBrush(Color.Parse("#FF9A95"))
            };

            cancel.Click += (_, _) => dialog.Close(false);
            confirm.Click += (_, _) => dialog.Close(true);

            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            dialog.Content = panel;

            return await dialog.ShowDialog<bool>(owner);
        }

        private async Task<bool> WaitForDaemonReadyAsync(TimeSpan timeout)
        {
            Trace($"WaitForDaemonReadyAsync() → esperando iscsid (máx. {timeout.TotalSeconds:0}s)...");

            var deadline = DateTime.UtcNow + timeout;
            int attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                attempt++;

                var result = await ShellHelper.RunCleanResultAsync(
                    "systemctl is-active --quiet iscsid");

                if (result.ExitCode == 0)
                {
                    Trace($"WaitForDaemonReadyAsync() ← activo tras {attempt} intento(s).");
                    return true;
                }

                await Task.Delay(500);
            }

            TraceError("WaitForDaemonReadyAsync(): iscsid no llegó a estar activo a tiempo.");
            return false;
        }

        private async Task LoadStatus()
        {
            Trace("LoadStatus() →");

            var tService  = LoadServiceStatus();
            var tNetwork  = LoadNetworkStatus();
            var tDaemon   = LoadDaemonStatus();
            var tUptime   = LoadUptime();
            var tHostname = LoadHostname();
            var tIp       = LoadIpAddress();
            var tLatency  = LoadLatency();
            var tSessions = LoadSessionMetrics();
            var tThroughput = LoadThroughput();

            await Task.WhenAll(
                tService, tNetwork, tDaemon, tUptime, tHostname, tIp,
                tLatency, tSessions, tThroughput);

            SetLastUpdate();
            _hasLoadedStatus = true;
            UpdateSummary();

            Trace("LoadStatus() ← OK");
        }

        /// <summary>
        /// Refresco ligero que usa el temporizador: solo sesiones/targets y
        /// throughput, sin repetir ping ni lecturas de sistema.
        /// </summary>
        private async Task RefreshMetricsAsync()
        {
            if (_metricsRefreshing)
                return;

            _metricsRefreshing = true;

            try
            {
                await Task.WhenAll(LoadSessionMetrics(), LoadThroughput());

                // Antes del primer LoadStatus() los campos aún tienen sus
                // valores por defecto; no se debe resumir el estado todavía.
                if (_hasLoadedStatus)
                    UpdateSummary();
            }
            finally
            {
                _metricsRefreshing = false;
            }
        }

        private async Task LoadSessionMetrics()
        {
            try
            {
                Trace("LoadSessionMetrics() → /dev/disk/by-path");

                var result = await ShellHelper.RunCleanResultAsync("ls -1 /dev/disk/by-path/");

                var entries = IscsiByPath.ParseAll(
                    result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();

                _activeLuns = entries.Count;

                _activeSessions = entries
                    .Select(e => $"{e.Portal}|{e.Iqn}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                _activeTargets = entries
                    .Select(e => e.Iqn)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                TxtSessions.Text =
                    $"{FormatCount(_activeSessions, "session", "sessions")} · " +
                    $"{FormatCount(_activeTargets, "target", "targets")} · " +
                    $"{FormatCount(_activeLuns, "LUN", "LUNs")}";

                Trace(
                    $"LoadSessionMetrics() ← sessions={_activeSessions}, " +
                    $"targets={_activeTargets}, luns={_activeLuns}");
            }
            catch (Exception ex)
            {
                TraceError($"LoadSessionMetrics(): {ex.Message}");
                _activeSessions = 0;
                _activeTargets = 0;
                _activeLuns = 0;
                TxtSessions.Text = "Unknown";
            }
        }

        /// <summary>
        /// Suma los contadores de todos los dispositivos iSCSI y calcula la
        /// velocidad entre dos muestras. Los sectores del kernel son siempre
        /// de 512 bytes, independientemente del tamaño lógico del disco.
        /// </summary>
        private async Task LoadThroughput()
        {
            try
            {
                const string script =
                    """
                    for l in /dev/disk/by-path/ip-*; do
                      case "$l" in *-part*) continue;; esac
                      d=$(readlink -f "$l" 2>/dev/null) || continue
                      n=$(basename "$d")
                      s="/sys/class/block/$n/stat"
                      [ -r "$s" ] || continue
                      printf '%s ' "$n"
                      cat "$s"
                    done | sort -u -k1,1
                    """;

                var result = await ShellHelper.RunCleanResultAsync(script);

                long readSectors = 0;
                long writeSectors = 0;
                long readIos = 0;
                long writeIos = 0;
                var deviceNames = new List<string>();

                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    // parts[0] = nombre; tras él vienen los campos del fichero
                    // stat: 1) I/Os leídas, 3) sectores leídos, 5) I/Os
                    // escritas y 7) sectores escritos.
                    if (parts.Length < 8 ||
                        !long.TryParse(parts[1], out long ri) ||
                        !long.TryParse(parts[3], out long rs) ||
                        !long.TryParse(parts[5], out long wi) ||
                        !long.TryParse(parts[7], out long ws))
                    {
                        continue;
                    }

                    readIos += ri;
                    writeIos += wi;
                    readSectors += rs;
                    writeSectors += ws;
                    deviceNames.Add(parts[0]);
                }

                int devices = deviceNames.Count;

                long readBytes = readSectors * 512L;
                long writeBytes = writeSectors * 512L;
                var now = DateTime.UtcNow;

                if (devices == 0)
                {
                    _lastThroughputSample = null;
                    TxtThroughput.Text = "No active iSCSI devices";
                    TxtIops.Text = "—";
                    Trace("LoadThroughput() ← sin dispositivos iSCSI");
                    return;
                }

                string signature = string.Join(",", deviceNames.OrderBy(n => n, StringComparer.Ordinal));

                if (_lastThroughputSample is { } previous && previous.Signature == signature)
                {
                    double seconds = (now - previous.At).TotalSeconds;

                    if (seconds >= 0.2)
                    {
                        double readMbps =
                            (readBytes - previous.ReadBytes) / seconds / (1024d * 1024d);
                        double writeMbps =
                            (writeBytes - previous.WriteBytes) / seconds / (1024d * 1024d);

                        double readIops = (readIos - previous.ReadIos) / seconds;
                        double writeIops = (writeIos - previous.WriteIos) / seconds;

                        // Si el contador retrocede (dispositivo reiniciado),
                        // no se muestran velocidades negativas.
                        readMbps = Math.Max(0, readMbps);
                        writeMbps = Math.Max(0, writeMbps);
                        readIops = Math.Max(0, readIops);
                        writeIops = Math.Max(0, writeIops);

                        TxtThroughput.Text =
                            $"{readMbps:F2} / {writeMbps:F2} MB/s · " +
                            $"{devices} device(s)";

                        TxtIops.Text =
                            $"{FormatIops(readIops)} / {FormatIops(writeIops)} IOPS";

                        Trace($"LoadThroughput() ← {TxtThroughput.Text}; {TxtIops.Text}");
                    }
                }
                else
                {
                    // Primera muestra: hace falta otra para calcular velocidad.
                    TxtThroughput.Text = "Calculating...";
                    TxtIops.Text = "Calculating...";
                }

                _lastThroughputSample =
                    new ThroughputSample(
                        now, readBytes, writeBytes, readIos, writeIos, devices, signature);
            }
            catch (Exception ex)
            {
                TraceError($"LoadThroughput(): {ex.Message}");
                _lastThroughputSample = null;
                TxtThroughput.Text = "Unknown";
                TxtIops.Text = "Unknown";
            }
        }

        private async Task LoadServiceStatus()
        {
            try
            {
                Trace("LoadServiceStatus() → systemctl is-active iscsid");
                var result = await ShellHelper.RunCleanResultAsync("systemctl is-active iscsid");

                // Se compara el estado exacto: "inactive" contiene la palabra
                // "active" y antes se mostraba como Running.
                string state = result.Stdout.Trim().ToLowerInvariant();
                Trace($"LoadServiceStatus() state='{state}' exit={result.ExitCode}");

                // Sin salida el comando no llegó a responder (por ejemplo,
                // systemctl no está disponible). Eso no significa que el
                // servicio esté detenido.
                if (string.IsNullOrWhiteSpace(state))
                {
                    SetIndicator(IconIscsid, TxtIscsid, "Unknown", Brushes.Goldenrod);
                    return;
                }

                switch (state)
                {
                    case "active":
                        SetIndicator(IconIscsid, TxtIscsid, "Running", Brushes.LimeGreen);
                        break;

                    case "activating":
                    case "reloading":
                    case "deactivating":
                        SetIndicator(IconIscsid, TxtIscsid, "Starting", Brushes.Goldenrod);
                        break;

                    case "failed":
                        SetIndicator(IconIscsid, TxtIscsid, "Failed", Brushes.Red);
                        break;

                    default:
                        SetIndicator(IconIscsid, TxtIscsid, "Stopped", Brushes.Red);
                        break;
                }
            }
            catch (Exception ex)
            {
                TraceError($"LoadServiceStatus(): {ex.Message}");
                SetIndicator(IconIscsid, TxtIscsid, "Unknown", Brushes.Goldenrod);
            }
        }

        private async Task LoadNetworkStatus()
        {
            try
            {
                // Una dirección global puede ser solo docker0. Lo que indica
                // conectividad real es tener una ruta por defecto, en IPv4 o
                // IPv6.
                Trace("LoadNetworkStatus() → ip route show default (v4/v6)");
                var v4 = await ShellHelper.RunCleanResultAsync("ip -o -4 route show default");
                var v6 = await ShellHelper.RunCleanResultAsync("ip -o -6 route show default");

                // Si `ip` no existe o falla, se comprueba con las API de .NET
                // antes de declarar el estado como desconocido.
                if (v4.ExitCode != 0 && v6.ExitCode != 0)
                {
                    bool dotnetConnected = HasDefaultRouteViaDotNet();

                    SetIndicator(IconNetwork, TxtNetwork,
                        dotnetConnected ? "Connected" : "Unknown",
                        dotnetConnected ? Brushes.LimeGreen : Brushes.Goldenrod);

                    Trace($"LoadNetworkStatus() ← ip no disponible " +
                          $"(v4={v4.ExitCode}, v6={v6.ExitCode}), .NET={dotnetConnected}");
                    return;
                }

                bool connected = !string.IsNullOrWhiteSpace(v4.Stdout) ||
                                 !string.IsNullOrWhiteSpace(v6.Stdout);

                SetIndicator(IconNetwork, TxtNetwork,
                    connected ? "Connected" : "Disconnected",
                    connected ? Brushes.LimeGreen : Brushes.Red);

                Trace($"LoadNetworkStatus() ← connected={connected}");
            }
            catch (Exception ex)
            {
                TraceError($"LoadNetworkStatus(): {ex.Message}");
                SetIndicator(IconNetwork, TxtNetwork, "Unknown", Brushes.Goldenrod);
            }
        }

        internal static string GetSystemdProperty(string output, string name)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = line.IndexOf('=');

                if (idx <= 0)
                    continue;

                if (line[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(idx + 1)..].Trim().ToLowerInvariant();
            }

            return "";
        }

        // iscsid es Type=notify: cuando ActiveState es "active" ya envió READY.
        // StatusText no sirve aquí porque iscsid no lo establece y siempre
        // aparecía vacío, dejando el daemon como "Not operational" aunque
        // estuviera funcionando.
        private async Task LoadDaemonStatus()
        {
            try
            {
                Trace("LoadDaemonStatus() → systemctl show ActiveState/SubState iscsid");
                var result = await ShellHelper.RunCleanResultAsync(
                    "systemctl show -p ActiveState -p SubState iscsid");

                string active = GetSystemdProperty(result.Stdout, "ActiveState");
                string sub = GetSystemdProperty(result.Stdout, "SubState");

                Trace($"LoadDaemonStatus() ActiveState='{active}' SubState='{sub}'");

                if (active == "active")
                    SetIndicator(IconSocket, TxtSocket, "Operational", Brushes.LimeGreen);
                else if (active is "activating" or "reloading" or "deactivating")
                    SetIndicator(IconSocket, TxtSocket, "Syncing", Brushes.Goldenrod);
                else
                    SetIndicator(IconSocket, TxtSocket, "Not operational", Brushes.Red);
            }
            catch (Exception ex)
            {
                TraceError($"LoadDaemonStatus(): {ex.Message}");
                SetIndicator(IconSocket, TxtSocket, "Unknown", Brushes.Goldenrod);
            }
        }

        private async Task LoadUptime()
        {
            try
            {
                // /proc/uptime no depende de `uptime` (util-linux) ni del idioma,
                // y está disponible en cualquier Linux moderno.
                var seconds = ParseUpSeconds(ReadProcValue("/proc/uptime"));
                if (seconds is not null)
                {
                    TxtUptime.Text = FormatUptime(seconds);
                    Trace($"LoadUptime() ← '{TxtUptime.Text}' (from /proc/uptime)");
                    return;
                }

                // Fallback para sistemas donde /proc no sea legible.
                string raw = await ShellHelper.RunCleanAsync("uptime -p");
                if (string.IsNullOrWhiteSpace(raw))
                {
                    TxtUptime.Text = "Unknown";
                    Trace("LoadUptime() ← Unknown");
                    return;
                }

                const string prefix = "up ";
                if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    raw = raw[prefix.Length..];

                TxtUptime.Text = raw.Trim();
                Trace($"LoadUptime() ← '{TxtUptime.Text}' (from uptime -p)");
            }
            catch (Exception ex)
            {
                TraceError($"LoadUptime(): {ex.Message}");
                TxtUptime.Text = "Unknown";
            }
        }

        private async Task LoadHostname()
        {
            try
            {
                // /proc/sys/kernel/hostname refleja el nombre real en caliente y
                // no requiere net-tools (`hostname`), que falta en muchas
                // distribuciones mínimas modernas.
                var name = ReadProcValue("/proc/sys/kernel/hostname");

                if (string.IsNullOrEmpty(name))
                {
                    name = await ShellHelper.RunCleanAsync("hostname");

                    if (string.IsNullOrWhiteSpace(name))
                        name = await ShellHelper.RunCleanAsync("hostnamectl hostname");
                }

                TxtHostname.Text = string.IsNullOrWhiteSpace(name)
                    ? "Unknown"
                    : name.Trim();

                Trace($"LoadHostname() ← '{TxtHostname.Text}'");
            }
            catch (Exception ex)
            {
                TraceError($"LoadHostname(): {ex.Message}");
                TxtHostname.Text = "Unknown";
            }
        }

        private async Task LoadIpAddress()
        {
            try
            {
                Trace("LoadIpAddress() → ip -o -4 addr show scope global");
                var result = await ShellHelper.RunCleanResultAsync(
                    "ip -o -4 addr show scope global");

                var address = SelectPrimaryIpAddress(result.Stdout);
                if (address == "Unknown")
                {
                    // `iproute2` puede no estar instalado en distribuciones
                    // mínimas; en ese caso se consulta el propio sistema.
                    address = SelectPrimaryIpAddressFromDotNet();
                }

                TxtIpAddress.Text = address;
                Trace($"LoadIpAddress() ← '{TxtIpAddress.Text}'");
            }
            catch (Exception ex)
            {
                TraceError($"LoadIpAddress(): {ex.Message}");
                TxtIpAddress.Text = "Unknown";
            }
        }

        // Elige la dirección IPv4 principal a partir de la salida de
        // `ip -o -4 addr show scope global`. Se ignoran las interfaces virtuales
        // (Docker, libvirt, VPN, contenedores) para no mostrar la dirección de un
        // bridge o de un contenedor en lugar de la de la máquina.
        internal static string SelectPrimaryIpAddress(string? ipOutput)
        {
            if (string.IsNullOrWhiteSpace(ipOutput))
                return "Unknown";

            var ips = ipOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 4)
                // En `ip -o` cada línea es: "2: eth0    inet 192.168.1.10/24 ...":
                // parts[1] es la interfaz y parts[3] la dirección con prefijo.
                .Select(parts => (Interface: parts[1], Address: parts[3].Split('/')[0]))
                .Where(item => !IsVirtualInterface(item.Interface))
                .Select(item => item.Address)
                .Where(ip => !string.IsNullOrWhiteSpace(ip) && !ip.StartsWith("127."))
                .ToList();

            return ips.FirstOrDefault() ?? "Unknown";
        }

        // Interfaces virtuales habituales (Docker, libvirt, VPN, contenedores)
        // que no representan la dirección real de la máquina. Antes se excluían
        // rangos de IP concretos, lo que ocultaba direcciones privadas legítimas
        // (por ejemplo un NAS en 172.16.x o 192.168.194.x).
        internal static bool IsVirtualInterface(string name) =>
            name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("vmnet", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tap", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("lo", StringComparison.OrdinalIgnoreCase);

        // Comprobación de ruta por defecto sin depender de `iproute2`.
        private static bool HasDefaultRouteViaDotNet()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (ni.GetIPProperties().GatewayAddresses.Count > 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                TraceError($"HasDefaultRouteViaDotNet(): {ex.Message}");
            }

            return false;
        }

        // Último recurso para la IP principal cuando `ip` no está disponible.
        private static string SelectPrimaryIpAddressFromDotNet()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        IsVirtualInterface(ni.Name))
                        continue;

                    var address = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a =>
                            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !a.Address.ToString().StartsWith("127."));

                    if (address is not null)
                        return address.Address.ToString();
                }
            }
            catch (Exception ex)
            {
                TraceError($"SelectPrimaryIpAddressFromDotNet(): {ex.Message}");
            }

            return "Unknown";
        }

        private async Task LoadLatency()
        {
            try
            {
                // Se parsea en C# en lugar de usar `awk`, que no está garantizado
                // en instalaciones mínimas.
                Trace("LoadLatency() → obtener gateway");
                var route = await ShellHelper.RunCleanResultAsync("ip -4 route show default");

                string gateway = SelectDefaultGateway(route.Stdout) ?? "";
                // El gateway se inyecta en la línea de comandos: solo se admite
                // si es una dirección IP válida.
                if (!System.Net.IPAddress.TryParse(gateway, out _))
                    gateway = "";

                Trace($"LoadLatency() gateway='{gateway}'");

                string target = string.IsNullOrEmpty(gateway) ? "8.8.8.8" : gateway;

                // `-w` es de iputils; busybox usa `-W`. Se prueban ambas y se
                // silencia stderr para que un intento fallido no invalide la
                // respuesta válida del siguiente (RunCleanAsync descarta stderr).
                string pingCmd =
                    $"ping -c 1 -w 2 {target} 2>/dev/null || " +
                    $"ping -c 1 -W 2 {target} 2>/dev/null || " +
                    "ping -c 1 -w 2 8.8.8.8 2>/dev/null || " +
                    "ping -c 1 -W 2 8.8.8.8 2>/dev/null";

                Trace($"LoadLatency() → {pingCmd}");
                string raw = await ShellHelper.RunCleanAsync(pingCmd);

                // Un ping sin respuesta no implica un problema: muchas redes
                // bloquean ICMP. Es un dato informativo, no un estado degradado.
                string? ms = ParseLatencyMs(raw);
                TxtLatency.Text = ms is null ? "Unavailable" : $"{ms} ms";
                Trace($"LoadLatency() ← '{TxtLatency.Text}'");
            }
            catch (Exception ex)
            {
                TraceError($"LoadLatency(): {ex.Message}");
                TxtLatency.Text = "Unavailable";
            }
        }

        private void UpdateSummary()
        {
            ApplyServiceHint();

            var reasons = new List<string>();
            bool critical = false;
            bool degraded = false;

            switch (TxtIscsid.Text)
            {
                case "Running":
                    break;

                case "Starting":
                    degraded = true;
                    reasons.Add("iscsid is starting");
                    break;

                case "Stopped":
                    critical = true;
                    reasons.Add("iscsid is stopped");
                    break;

                case "Failed":
                    critical = true;
                    reasons.Add("iscsid failed to start");
                    break;

                default:
                    degraded = true;
                    reasons.Add("iscsid state is unknown");
                    break;
            }

            switch (TxtSocket.Text)
            {
                case "Operational":
                    break;

                case "Syncing":
                    degraded = true;
                    reasons.Add("iSCSI daemon is syncing");
                    break;

                case "Not operational":
                    critical = true;
                    reasons.Add("iSCSI daemon is not operational");
                    break;

                default:
                    degraded = true;
                    reasons.Add("iSCSI daemon state is unknown");
                    break;
            }

            switch (TxtNetwork.Text)
            {
                case "Connected":
                    break;

                case "Disconnected":
                    degraded = true;
                    reasons.Add("no default network route");
                    break;

                default:
                    degraded = true;
                    reasons.Add("network state is unknown");
                    break;
            }

            // La latencia ICMP no se tiene en cuenta para el resumen: muchas
            // redes bloquean `ping` aunque el equipo esté perfectamente
            // conectado. Se muestra solo como dato informativo.

            string state = critical ? "Critical" : degraded ? "Degraded" : "OK";

            Trace($"UpdateSummary() state={state} service={TxtIscsid.Text}, " +
                  $"network={TxtNetwork.Text}, daemon={TxtSocket.Text}, " +
                  $"sessions={_activeSessions}, luns={_activeLuns}");

            // Solo se registra cuando el estado cambia, para que el log sea
            // útil sin llenarse con una línea en cada refresco.
            if (state != _lastSummaryState)
            {
                _lastSummaryState = state;

                LogService.Write(
                    $"[STATUS] System status {state}: iscsid={TxtIscsid.Text}, " +
                    $"network={TxtNetwork.Text}, daemon={TxtSocket.Text}, " +
                    $"sessions={_activeSessions}, targets={_activeTargets}, " +
                    $"luns={_activeLuns}, latency={TxtLatency.Text}" +
                    (reasons.Count > 0
                        ? $", reasons={string.Join("; ", reasons)}"
                        : ""));
            }

            string detail = reasons.Count > 0
                ? Capitalize(reasons[0]) +
                  (reasons.Count > 1 ? $" (+{reasons.Count - 1} more)" : "")
                : _activeSessions == 0
                    ? "All systems operational · no active iSCSI sessions"
                    : $"All systems operational · {_activeSessions} active session(s)";

            if (critical)
                SetSummary("Critical", detail, CriticalColor, CriticalBackground);
            else if (degraded)
                SetSummary("Degraded", detail, WarningColor, WarningBackground);
            else
                SetSummary("OK", detail, OkColor, OkBackground);
        }
    }
}
