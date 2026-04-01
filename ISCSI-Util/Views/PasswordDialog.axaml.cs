using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;

namespace ISCSI_Util.Views
{
    /// <summary>
    /// Password input dialog window.
    /// Allows users to securely enter their admin password for iSCSI operations.
    /// </summary>
    public partial class PasswordDialog : Window
    {
        /// <summary>
        /// Initializes the password dialog window and loads its XAML definition.
        /// </summary>
        public PasswordDialog()
        {
            InitializeComponent();
        }
 
    }
}