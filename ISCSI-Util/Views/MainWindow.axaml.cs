using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ISCSI_Util.Helpers;
using ISCSI_Util.Utils;
using ISCSI_Util.ViewModels;

namespace ISCSI_Util.Views
{
    public partial class MainWindow : Window
    {
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

            // ⭐ El ViewModel se crea aquí, pero NO se inicializa todavía
            DataContext = new MainWindowViewModel();
        }

        // ⭐ Este evento se dispara cuando la ventana YA está visible
        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // 1) Primero pedir la contraseña (PasswordDialog)
            await SolicitarPassword();

            // 2) Ahora que Credenciales.AdminPassword ya está cargada,
            //    podemos arrancar iscsid sin errores
            IscsiHelper.AsegurarServicioIscsid();

            // 3) Ahora sí podemos inicializar el ViewModel
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.InicializarAsync();   // ⭐ Aquí se cargan los destinos conectados
            }
        }

        public async Task SolicitarPassword()
        {
            // Crear el diálogo
            var dialog = new PasswordDialog();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Asignar DataContext
            dialog.DataContext = new PasswordDialogViewModel(pass =>
            {
                Credenciales.AdminPassword = pass;
                dialog.Close();
            });

            await dialog.ShowDialog(this);
        }
    }
}