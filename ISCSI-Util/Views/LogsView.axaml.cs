using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views
{
    public partial class LogsView : UserControl
    {
        public LogsView()
        {
            InitializeComponent();
            LoadLog();

            BtnRefresh.Click += (_, _) => LoadLog();

            // ============================================================
            //  AHORA ABRE EL ARCHIVO EN EL EDITOR DE TEXTO
            // ============================================================
            BtnCopy.Click += (_, _) =>
            {
                string appLog = Path.Combine(ConfigManager.LogPath, "iscsi-util.log");

                if (File.Exists(appLog))
                {
                    // Abrir el archivo con el editor predeterminado del sistema
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = appLog,
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Si no existe el log del programa, abrir journalctl en un visor
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = "-c \"journalctl -u iscsid | less\"",
                        UseShellExecute = false
                    });
                }
            };
        }

        // ============================================================
        //  CARGAR LOG (PRIMERO LOG DEL PROGRAMA, LUEGO JOURNAL)
        // ============================================================
        private void LoadLog()
        {
            try
            {
                string appLog = Path.Combine(ConfigManager.LogPath, "iscsi-util.log");

                // 1) Intentar leer el log del programa
                if (File.Exists(appLog))
                {
                    string content = File.ReadAllText(appLog);

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        LogText.Text = content;
                        return;
                    }
                }

                // 2) Si no hay log del programa, cargar journalctl
                LoadSystemLog();
            }
            catch
            {
                LogText.Text = "Unable to read application logs.";
            }
        }

        // ============================================================
        //  CARGAR LOG DEL SISTEMA (journalctl)
        // ============================================================
        private void LoadSystemLog()
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

        // ============================================================
        //  FILTRADO DE LÍNEAS ÚTILES
        // ============================================================
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
