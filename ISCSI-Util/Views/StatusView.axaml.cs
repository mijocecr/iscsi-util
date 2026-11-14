using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;

namespace ISCSI_Util.Views
{
    public partial class StatusView : UserControl
    {
        private bool _isRefreshing;
        private string _lastSummaryState = "";

        public StatusView()
        {
            InitializeComponent();
            HookButtons();
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
                SummaryBorder.Background = new SolidColorBrush(Color.Parse("#5A1E1E"));
                TxtSummary.Text = "SYSTEM STATUS: Error";
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
                    SummaryBorder.Background = new SolidColorBrush(Color.Parse("#6C5A1E"));
                    TxtSummary.Text = "SYSTEM STATUS: Updating";

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
                    SummaryBorder.Background = new SolidColorBrush(Color.Parse("#5A1E1E"));
                    TxtSummary.Text = "SYSTEM STATUS: Error";
                }
                finally
                {
                    SetButtonsEnabled(true);
                }
            };
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

            await Task.WhenAll(tService, tNetwork, tDaemon, tUptime, tHostname, tIp, tLatency);

            TxtLastOp.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
            UpdateSummary();

            Trace("LoadStatus() ← OK");
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
                    TxtLatency.Text = ms;
                    Trace($"LoadLatency() ← '{ms}'");
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

        // ⭐ ACTUALIZADO: Syncing ya no es crítico
        private void UpdateSummary()
        {
            bool serviceOk = TxtIscsid.Text == "Running";
            bool networkOk = TxtNetwork.Text == "Connected";
            bool daemonOk  = TxtSocket.Text is "Operational" or "Syncing";

            string state = serviceOk && networkOk && daemonOk ? "OK" : "Critical";

            Trace($"UpdateSummary() state={state} service={TxtIscsid.Text}, " +
                  $"network={TxtNetwork.Text}, daemon={TxtSocket.Text}");

            // Solo se registra cuando el estado cambia, para que el log sea
            // útil sin llenarse con una línea en cada refresco.
            if (state != _lastSummaryState)
            {
                _lastSummaryState = state;

                LogService.Write(
                    $"[STATUS] System status {state}: iscsid={TxtIscsid.Text}, " +
                    $"network={TxtNetwork.Text}, daemon={TxtSocket.Text}");
            }

            SummaryBorder.Background = new SolidColorBrush(
                Color.Parse(state == "OK" ? "#1E4620" : "#5A1E1E"));

            TxtSummary.Text = $"SYSTEM STATUS: {state}";
        }
    }
}
