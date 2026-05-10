using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace ISCSI_Util.Views
{
    public partial class LogsView : UserControl
    {
        public LogsView()
        {
            InitializeComponent();
            LoadLog();

            BtnRefresh.Click += (_, _) => LoadLog();

            BtnCopy.Click += async (_, _) =>
            {
                var top = TopLevel.GetTopLevel(this);

                if (top?.Clipboard != null && !string.IsNullOrWhiteSpace(LogText.Text))
                    await top.Clipboard.SetTextAsync(LogText.Text);
            };
           
        }

        private void LoadLog()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = "-c \"journalctl -u iscsid --no-pager --output=short\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                var p = Process.Start(psi);
                string output = p!.StandardOutput.ReadToEnd();
                p.WaitForExit();

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogText.Text = "No log entries found for iscsid.";
                    return;
                }

                var filtered = FiltrarLineasUtiles(output);

                LogText.Text = string.IsNullOrWhiteSpace(filtered)
                    ? "No relevant iSCSI events found."
                    : filtered;
            }
            catch
            {
                LogText.Text = "Unable to read system logs.";
            }
        }

        private string FiltrarLineasUtiles(string log)
        {
            var lines = log.Split('\n');
            var result = new List<string>();

            foreach (var line in lines)
            {
                string l = line.ToLowerInvariant();

                // Errores
                if (l.Contains("error") || l.Contains("failed") || l.Contains("authentication"))
                {
                    result.Add(line);
                    continue;
                }

                // Warnings
                if (l.Contains("warn"))
                {
                    result.Add(line);
                    continue;
                }

                // Login / Logout
                if (l.Contains("login") || l.Contains("logout"))
                {
                    result.Add(line);
                    continue;
                }

                // Asignación de dispositivos
                if (Regex.IsMatch(l, @"sd[a-z]\d?") || l.Contains("scsi"))
                {
                    result.Add(line);
                    continue;
                }

                // Problemas del kernel relacionados con iSCSI
                if (l.Contains("transport") || l.Contains("connection") || l.Contains("timeout"))
                {
                    result.Add(line);
                    continue;
                }
            }

            return string.Join("\n", result);
        }
    }
}
