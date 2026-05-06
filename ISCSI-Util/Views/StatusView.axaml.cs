using Avalonia.Controls;
using Avalonia.Media;
using ISCSI_Util.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ISCSI_Util.Views
{
    public partial class StatusView : UserControl
    {
        private bool _isRefreshing;

        public StatusView()
        {
            InitializeComponent();
            HookButtons();
        }

        private void Trace(string message)
        {
            Console.WriteLine($"[StatusView] {DateTime.Now:HH:mm:ss} {message}");
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
                Trace($"[ERROR] RefreshStatus(): {ex}");
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
                try
                {
                    ShellHelper.EjecutarComoRoot("systemctl restart iscsid");

                    TxtLastOp.Text = "Service restarted.";
                    SummaryBorder.Background = new SolidColorBrush(Color.Parse("#6C5A1E"));
                    TxtSummary.Text = "SYSTEM STATUS: Warning";

                    await WaitForDaemonReady();
                    await RefreshStatus();
                }
                catch (Exception ex)
                {
                    Trace($"[ERROR] BtnRestart: {ex}");
                    TxtLastOp.Text = $"Restart failed: {ex.Message}";
                    SummaryBorder.Background = new SolidColorBrush(Color.Parse("#5A1E1E"));
                    TxtSummary.Text = "SYSTEM STATUS: Error";
                }
            };
        }

        public async Task WaitForDaemonReady()
        {
            Trace("WaitForDaemonReady() → esperando iscsid listo...");
            for (int i = 0; i < 30; i++)
            {
                var result = ShellHelper.EjecutarComoRoot(
                    "systemctl show -p StatusText iscsid"
                );

                string status = result.Stdout.Trim();
                Trace($"WaitForDaemonReady() intento {i + 1}: '{status}'");

                if (status.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                {
                    Trace("WaitForDaemonReady() ← daemon listo.");
                    return;
                }

                await Task.Delay(100);
            }

            Trace("[WARN] WaitForDaemonReady() timeout: iscsid no reporta Ready.");
        }

        private async Task LoadStatus()
        {
            Trace("LoadStatus() →");

            // Cargar en paralelo lo que no depende entre sí
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

        // ============================================================
        // SERVICE STATUS
        // ============================================================
        private async Task LoadServiceStatus()
        {
            try
            {
                Trace("LoadServiceStatus() → systemctl is-active iscsid");
                var result = ShellHelper.EjecutarComoRoot("systemctl is-active iscsid");

                string raw = result.Stdout.Trim().ToLower();
                Trace($"LoadServiceStatus() raw='{raw}'");

                if (raw.Contains("active"))
                {
                    IconIscsid.Fill = Brushes.LimeGreen;
                    TxtIscsid.Text = "Running";
                }
                else if (raw.Contains("inactive") || raw.Contains("failed"))
                {
                    IconIscsid.Fill = Brushes.Red;
                    TxtIscsid.Text = "Stopped";
                }
                else
                {
                    IconIscsid.Fill = Brushes.Goldenrod;
                    TxtIscsid.Text = "Unknown";
                }
            }
            catch (Exception ex)
            {
                Trace($"[ERROR] LoadServiceStatus(): {ex}");
                IconIscsid.Fill = Brushes.Goldenrod;
                TxtIscsid.Text = "Unknown";
            }
        }

        // ============================================================
        // NETWORK STATUS
        // ============================================================
        private async Task LoadNetworkStatus()
        {
            try
            {
                Trace("LoadNetworkStatus() → ip -o -4 addr show scope global");
                string output = await ShellHelper.RunCleanAsync("ip -o -4 addr show scope global");

                bool connected = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any();

                IconNetwork.Fill = connected ? Brushes.LimeGreen : Brushes.Red;
                TxtNetwork.Text = connected ? "Connected" : "Disconnected";

                Trace($"LoadNetworkStatus() ← connected={connected}");
            }
            catch (Exception ex)
            {
                Trace($"[ERROR] LoadNetworkStatus(): {ex}");
                IconNetwork.Fill = Brushes.Goldenrod;
                TxtNetwork.Text = "Unknown";
            }
        }

        // ============================================================
        // DAEMON STATUS
        // ============================================================
        private async Task LoadDaemonStatus()
        {
            try
            {
                Trace("LoadDaemonStatus() → systemctl show -p StatusText iscsid");
                var result = ShellHelper.EjecutarComoRoot("systemctl show -p StatusText iscsid");
                string status = result.Stdout.Trim();

                Trace($"LoadDaemonStatus() raw='{status}'");

                bool ready = status.Contains("Ready", StringComparison.OrdinalIgnoreCase);

                IconSocket.Fill = ready ? Brushes.LimeGreen : Brushes.Red;
                TxtSocket.Text = ready ? "Operational" : "Not operational";

                Trace($"LoadDaemonStatus() ← ready={ready}");
            }
            catch (Exception ex)
            {
                Trace($"[ERROR] LoadDaemonStatus(): {ex}");
                IconSocket.Fill = Brushes.Goldenrod;
                TxtSocket.Text = "Unknown";
            }
        }

        // ============================================================
        // UPTIME (sesión)
        // ============================================================
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
                Trace($"[ERROR] LoadUptime(): {ex}");
                TxtUptime.Text = "Unknown";
            }
        }

        // ============================================================
        // HOSTNAME
        // ============================================================
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
                Trace($"[ERROR] LoadHostname(): {ex}");
                TxtHostname.Text = "Unknown";
            }
        }

        // ============================================================
        // IP ADDRESS
        // ============================================================
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
                Trace($"[ERROR] LoadIpAddress(): {ex}");
                TxtIpAddress.Text = "Unknown";
            }
        }

        // ============================================================
        // LATENCY
        // ============================================================
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
                Trace($"[ERROR] LoadLatency(): {ex}");
                TxtLatency.Text = "Timeout";
            }
        }

        // ============================================================
        // SUMMARY
        // ============================================================
        private void UpdateSummary()
        {
            bool serviceOk = TxtIscsid.Text == "Running";
            bool networkOk = TxtNetwork.Text == "Connected";
            bool daemonOk  = TxtSocket.Text  == "Operational";

            Trace($"UpdateSummary() serviceOk={serviceOk}, networkOk={networkOk}, daemonOk={daemonOk}");

            if (!serviceOk || !networkOk || !daemonOk)
            {
                SummaryBorder.Background = new SolidColorBrush(Color.Parse("#5A1E1E"));
                TxtSummary.Text = "SYSTEM STATUS: Critical";
                return;
            }

            SummaryBorder.Background = new SolidColorBrush(Color.Parse("#1E4620"));
            TxtSummary.Text = "SYSTEM STATUS: OK";
        }
    }
}
