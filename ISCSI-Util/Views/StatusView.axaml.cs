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
        public StatusView()
        {
            InitializeComponent();
            HookButtons();
        }

        public async Task RefreshStatus()
        {
            await LoadStatus();
        }

        private void HookButtons()
        {
            BtnReload.Click += async (_, _) => await RefreshStatus();
            BtnRefresh.Click += async (_, _) => await RefreshStatus();

            BtnRestart.Click += async (_, _) =>
            {
                // Reiniciar servicio
                ShellHelper.EjecutarComoRoot("systemctl restart iscsid");

                // Feedback inmediato
                TxtLastOp.Text = "Service restarted.";
                SummaryBorder.Background = new SolidColorBrush(Color.Parse("#6C5A1E"));
                TxtSummary.Text = "SYSTEM STATUS: Warning";

                // Esperar a que el daemon esté realmente listo
                await WaitForDaemonReady();

                //  Refrescar estado completo
                await RefreshStatus();
            };
        }

        
        private async Task WaitForDaemonReady()
        {
            // Intentos: 30 → ~3 segundos máximo
            for (int i = 0; i < 30; i++)
            {
                var result = ShellHelper.EjecutarComoRoot(
                    "systemctl show -p StatusText iscsid"
                );

                string status = result.Stdout.Trim();

                // Cuando el daemon está listo, systemd devuelve:
                // StatusText=Ready to process requests
                if (status.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                    return;

                await Task.Delay(100);
            }
        }

        
        
        
        
        private async Task LoadStatus()
        {
            await LoadServiceStatus();
            await LoadNetworkStatus();
            await LoadDaemonStatus();

            await LoadUptime();
            await LoadHostname();
            await LoadIpAddress();
            await LoadLatency();

            TxtLastOp.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
            UpdateSummary();
        }

        // ============================================================
        // SERVICE STATUS (sin redirecciones, sin errores)
        // ============================================================
        private async Task LoadServiceStatus()
        {
            var result = ShellHelper.EjecutarComoRoot("systemctl is-active iscsid");

            string raw = result.Stdout.Trim().ToLower();

            // systemctl puede devolver:
            // active
            // active (running)
            // active (exited)
            // inactive
            // failed
            // activating
            // unknown

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

        // ============================================================
        // NETWORK STATUS
        // ============================================================
        private async Task LoadNetworkStatus()
        {
            string output = await ShellHelper.RunCleanAsync("ip -o -4 addr show scope global");

            bool connected = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any();

            IconNetwork.Fill = connected ? Brushes.LimeGreen : Brushes.Red;
            TxtNetwork.Text = connected ? "Connected" : "Disconnected";
        }

        // ============================================================
        // DAEMON STATUS
        // ============================================================
        private async Task LoadDaemonStatus()
        {
            var result = ShellHelper.EjecutarComoRoot("systemctl show -p StatusText iscsid");
            string status = result.Stdout.Trim();

            bool ready = status.Contains("Ready", StringComparison.OrdinalIgnoreCase);

            IconSocket.Fill = ready ? Brushes.LimeGreen : Brushes.Red;
            TxtSocket.Text = ready ? "Operational" : "Not operational";
        }

        // ============================================================
        // UPTIME (sesión)
        // ============================================================
        private async Task LoadUptime()
        {
            string raw = await ShellHelper.RunCleanAsync("uptime -p");

            if (string.IsNullOrWhiteSpace(raw))
            {
                TxtUptime.Text = "Unknown";
                return;
            }

            TxtUptime.Text = raw.Replace("up ", "").Trim();
        }

        // ============================================================
        // HOSTNAME
        // ============================================================
        private async Task LoadHostname()
        {
            string raw = await ShellHelper.RunCleanAsync("hostname");
            TxtHostname.Text = raw.Trim();
        }

        // ============================================================
        // IP ADDRESS (filtrado de múltiples IPs)
        // ============================================================
        private async Task LoadIpAddress()
        {
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
        }

        // ============================================================
        // LATENCY
        // ============================================================
       
        
        private async Task LoadLatency()
        {
            // 1) Obtener gateway usando bash -c (para que funcione el pipe)
            var gwResult = ShellHelper.EjecutarComoRoot(
                "bash -c \"ip route | awk '/default/ {print $3; exit}'\""
            );

            string gateway = gwResult.Stdout.Trim();

            // 2) Comando de ping con fallback, usando SIEMPRE bash -c
            string pingCommand;

            if (!string.IsNullOrWhiteSpace(gateway))
            {
                pingCommand =
                    $"bash -c \"ping -c 1 -w 1 {gateway} || ping -c 1 -w 1 8.8.8.8\"";
            }
            else
            {
                pingCommand =
                    "bash -c \"ping -c 1 -w 1 8.8.8.8\"";
            }

            var pingResult = ShellHelper.EjecutarComoRoot(pingCommand);

            string raw = pingResult.Stdout + pingResult.Stderr; // por si el ping escribe en stderr

            if (string.IsNullOrWhiteSpace(raw))
            {
                TxtLatency.Text = "Timeout";
                return;
            }

            // 3) Buscar time= (inglés) o tiempo= (español)
            int idx = raw.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
            if (idx == -1)
                idx = raw.IndexOf("tiempo=", StringComparison.OrdinalIgnoreCase);

            if (idx != -1)
            {
                string sub = raw[(idx + (raw.Contains("tiempo=") ? 7 : 5))..];
                string ms = sub.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                TxtLatency.Text = ms;
            }
            else
            {
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
