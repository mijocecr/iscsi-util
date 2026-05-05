using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ISCSI_Util.Views
{
    public partial class PasswordDialog : Window
    {
        public PasswordDialog()
        {
            InitializeComponent();
        }

        private void OnAccept(object? sender, RoutedEventArgs e)
        {
            string pass = PwdBox.Text ?? "";

            if (string.IsNullOrWhiteSpace(pass))
                return;

            Close(pass);
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OnAccept(sender, e);
            else if (e.Key == Key.Escape)
                OnCancel(sender, e);
        }
    }
}