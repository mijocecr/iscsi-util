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
using System.Linq;
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
            IconSummary.Fill = brush;
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
                TxtLastOp.Text = $"Last update failed: {ex.Message}";
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
            BtnRefresh.IsEnabled = enabled;
            BtnRestart.IsEnabled = enabled;
        }

        private void HookButtons()
        {
            BtnReload.Click += async (_, _) => await RefreshStatus();
            BtnRefresh.Click += async (_, _) => await RefreshStatus();

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

                    TxtLastOp.Text = "Service restarted.";
                    SetSummary("Updating", "Restarting the iSCSI service...",
                        WarningColor, WarningBackground);

                    // Esperar a que iscsid vuelva a estar activo en vez de
                    // asumir que 4 segundos siempre son suficientes.
                    if (!await WaitForDaemonReadyAsync(TimeSpan.FromSeconds(10)))
                    {
                        TraceError("BtnRestart: iscsid no volvió a estar activo tras el reinicio.");
                        TxtLastOp.Text = "Service restarted, but it did not become active.";
                    }

                    await RefreshStatus();
                }
                catch (Exception ex)
                {
                    TraceError($"BtnRestart: {ex.Message}");
                    TxtLastOp.Text = $"Restart failed: {ex.Message}";
                    SetSummary("Error", $"Restart failed: {ex.Message}",
                        CriticalColor, CriticalBackground);
                }
                finally
                {
                    SetButtonsEnabled(true);
                }
            };
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

            TxtLastOp.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
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
                var deviceNames = new List<string>();

                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    // parts[0] = nombre; los sectores leídos/escritos son los
                    // campos 3 y 7 del fichero stat (índices 3 y 7 aquí).
                    if (parts.Length < 8 ||
                        !long.TryParse(parts[3], out long rs) ||
                        !long.TryParse(parts[7], out long ws))
                    {
                        continue;
                    }

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

                        // Si el contador retrocede (dispositivo reiniciado),
                        // no se muestran velocidades negativas.
                        readMbps = Math.Max(0, readMbps);
                        writeMbps = Math.Max(0, writeMbps);

                        TxtThroughput.Text =
                            $"{readMbps:F2} / {writeMbps:F2} MB/s (read/write) · " +
                            $"{devices} device(s)";

                        Trace($"LoadThroughput() ← {TxtThroughput.Text}");
                    }
                }
                else
                {
                    // Primera muestra: hace falta otra para calcular velocidad.
                    TxtThroughput.Text = "Calculating...";
                }

                _lastThroughputSample =
                    new ThroughputSample(now, readBytes, writeBytes, devices, signature);
            }
            catch (Exception ex)
            {
                TraceError($"LoadThroughput(): {ex.Message}");
                _lastThroughputSample = null;
                TxtThroughput.Text = "Unknown";
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
                // conectividad real es tener una ruta por defecto.
                Trace("LoadNetworkStatus() → ip -o -4 route show default");
                var result = await ShellHelper.RunCleanResultAsync("ip -o -4 route show default");

                bool connected = result.ExitCode == 0 &&
                                 !string.IsNullOrWhiteSpace(result.Stdout);

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

        private static string GetSystemdProperty(string output, string name)
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
                Trace("LoadUptime() → uptime -p");
                string raw = await ShellHelper.RunCleanAsync("uptime -p");

                if (string.IsNullOrWhiteSpace(raw))
                {
                    TxtUptime.Text = "Unknown";
                    Trace("LoadUptime() ← vacío");
                    return;
                }

                TxtUptime.Text = raw.Replace("up ", "").Trim();
                Trace($"LoadUptime() ← '{TxtUptime.Text}'");
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
                Trace("LoadHostname() → hostname");
                string raw = await ShellHelper.RunCleanAsync("hostname");
                TxtHostname.Text = raw.Trim();
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
                Trace("LoadIpAddress() → ip -o -4 addr show scope global | awk ...");
                string raw = await ShellHelper.RunCleanAsync(
                    "ip -o -4 addr show scope global | awk '{print $4}' | cut -d/ -f1"
                );

                var ips = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(ip => ip.Trim())
                             .Where(ip =>
                                 !string.IsNullOrWhiteSpace(ip) &&
                                 !ip.StartsWith("127.") &&
                                 !ip.StartsWith("172.16.") &&
                                 !ip.StartsWith("172.17.") &&
                                 !ip.StartsWith("172.18.") &&
                                 !ip.StartsWith("172.19.") &&
                                 !ip.StartsWith("192.168.194.") &&
                                 !ip.StartsWith("172.16.206.")
                             )
                             .ToList();

                TxtIpAddress.Text = ips.FirstOrDefault() ?? "Unknown";
                Trace($"LoadIpAddress() ← '{TxtIpAddress.Text}'");
            }
            catch (Exception ex)
            {
                TraceError($"LoadIpAddress(): {ex.Message}");
                TxtIpAddress.Text = "Unknown";
            }
        }

        private async Task LoadLatency()
        {
            try
            {
                Trace("LoadLatency() → obtener gateway");
                string gateway = await ShellHelper.RunCleanAsync(
                    "ip route | awk '/default/ {print $3; exit}'"
                );
                gateway = gateway.Trim();
                Trace($"LoadLatency() gateway='{gateway}'");

                string pingCmd;

                if (!string.IsNullOrWhiteSpace(gateway))
                    pingCmd = $"ping -c 1 -w 1 {gateway} || ping -c 1 -w 1 8.8.8.8";
                else
                    pingCmd = "ping -c 1 -w 1 8.8.8.8";

                Trace($"LoadLatency() → {pingCmd}");
                string raw = await ShellHelper.RunCleanAsync(pingCmd);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    TxtLatency.Text = "Timeout";
                    Trace("LoadLatency() ← vacío → Timeout");
                    return;
                }

                int idx = raw.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
                if (idx == -1)
                    idx = raw.IndexOf("tiempo=", StringComparison.OrdinalIgnoreCase);

                if (idx != -1)
                {
                    bool spanish = raw.IndexOf("tiempo=", StringComparison.OrdinalIgnoreCase) == idx;
                    int offset = spanish ? 7 : 5;

                    string sub = raw[(idx + offset)..];
                    string ms = sub.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    TxtLatency.Text = $"{ms} ms";
                    Trace($"LoadLatency() ← '{TxtLatency.Text}'");
                }
                else
                {
                    TxtLatency.Text = "Timeout";
                    Trace("LoadLatency() ← sin 'time=' → Timeout");
                }
            }
            catch (Exception ex)
            {
                TraceError($"LoadLatency(): {ex.Message}");
                TxtLatency.Text = "Timeout";
            }
        }

        private void UpdateSummary()
        {
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

            if (TxtLatency.Text == "Timeout")
            {
                degraded = true;
                reasons.Add("network latency timeout");
            }

            if (_activeSessions > 0 && _activeLuns == 0)
            {
                degraded = true;
                reasons.Add("check targets: no LUN devices detected");
            }

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
