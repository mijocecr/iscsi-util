using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace ISCSI_Util.Views
{
    public partial class PasswordDialog : Window
    {
        private bool _closeApp = true; 

        public PasswordDialog()
        {
            InitializeComponent();
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            this.Closed += (s, e) =>
            {
                if (_closeApp)
                {
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
                ShowError("Password cannot be empty.");
                return;
            }

            // No cerrar la app cuando devolvemos la contraseña
            _closeApp = false;
            Close(pass);
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            // Cancel sí debe cerrar la app
            _closeApp = true;
            Close(null);
        }

        private void OnClose(object? sender, RoutedEventArgs e)
        {
            // Cerrar app si el usuario pulsa el botón Close
            _closeApp = true;
            Close(null);
        }

        private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OnAccept(sender, e);
            else if (e.Key == Key.Escape)
                OnCancel(sender, e);
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.IsVisible = true;
            
            PwdBox.Text = "";
            PwdBox.Focus();
        }
    }
}
