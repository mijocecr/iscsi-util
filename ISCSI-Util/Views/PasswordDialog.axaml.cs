using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views
{
    public partial class PasswordDialog : Window
    {
        private bool _closeApp = true;

        public PasswordDialog()
        {
            LogService.Debug("[PWD_DIALOG] Inicializando diálogo de contraseña.");

            InitializeComponent();
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            this.Closed += (s, e) =>
            {
                LogService.Debug($"[PWD_DIALOG] Closed → closeApp={_closeApp}");

                if (_closeApp)
                {
                    LogService.Write("[PWD_DIALOG] Cerrando aplicación por cierre del diálogo.");

                    var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                        as IClassicDesktopStyleApplicationLifetime;

                    lifetime?.Shutdown();
                }
            };
        }

        private void OnAccept(object? sender, RoutedEventArgs e)
        {
            string pass = PwdBox.Text ?? "";

            if (string.IsNullOrWhiteSpace(pass))
            {
                LogService.Debug("[PWD_DIALOG] Contraseña vacía, mostrando error.");
                ShowError("Password cannot be empty.");
                return;
            }

            LogService.Write("[PWD_DIALOG] Contraseña aceptada por el usuario.");

            _closeApp = false;
            Close(pass);
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            LogService.Write("[PWD_DIALOG] Cancelado por el usuario. Cerrando aplicación.");
            _closeApp = true;
            Close(null);
        }

        private void OnClose(object? sender, RoutedEventArgs e)
        {
            LogService.Write("[PWD_DIALOG] Botón Close pulsado. Cerrando aplicación.");
            _closeApp = true;
            Close(null);
        }

        private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                LogService.Debug("[PWD_DIALOG] Enter presionado → aceptar.");
                OnAccept(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                LogService.Debug("[PWD_DIALOG] Escape presionado → cancelar.");
                OnCancel(sender, e);
            }
        }

        private void ShowError(string message)
        {
            LogService.Debug($"[PWD_DIALOG] Error mostrado: {message}");

            ErrorText.Text = message;
            ErrorText.IsVisible = true;

            PwdBox.Text = "";
            PwdBox.Focus();
        }
    }
}
