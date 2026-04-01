using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ISCSI_Util.Helpers;
using ISCSI_Util.Utils;
using ISCSI_Util.ViewModels;

namespace ISCSI_Util.Views
{
    /// <summary>
    /// Main application window for the iSCSI Utility.
    /// Handles password prompt, service initialization, and view model setup.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Initializes the main window with fixed dimensions and creates the view model.
        /// Sets up the window UI before it becomes visible.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            this.Width = 500;
            this.Height = 580;
            this.MinHeight = 580;
            this.MinWidth = 500;
            this.MaxHeight = 580;
            this.MaxWidth = 500;
            this.Title = "iscsi-util";

            // Create view model (initialization happens in OnOpened)
            DataContext = new MainWindowViewModel();
        }

        /// <summary>
        /// Handles window opening event.
        /// Prompts for admin password, ensures iscsid service is running, and initializes the view model.
        /// This is called after the window becomes visible.
        /// </summary>
        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // 1) Prompt for admin password
            await SolicitarPassword();

            // 2) If user cancelled or didn't enter password, abort
            if (string.IsNullOrWhiteSpace(Credenciales.AdminPassword))
            {
                Console.WriteLine("[ERROR] No se ingresó contraseña. Abortando inicialización.");
                return;
            }

            // 3) Ensure iscsid service is running
            IscsiHelper.AsegurarServicioIscsid();

            // 4) Initialize view model (load active sessions)
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.InicializarAsync();
            }
        }

        /// <summary>
        /// Shows a password dialog and stores the entered password.
        /// Used to collect admin credentials for iSCSI operations.
        /// </summary>
        public async Task SolicitarPassword()
        {
            // Create and configure the password dialog
            var dialog = new PasswordDialog();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Set up the dialog with a callback to store the entered password
            dialog.DataContext = new PasswordDialogViewModel(pass =>
            {
                Credenciales.AdminPassword = pass;
                dialog.Close();
            });

            await dialog.ShowDialog(this);
        }
    }
}