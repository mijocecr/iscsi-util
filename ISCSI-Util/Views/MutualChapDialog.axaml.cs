using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;

namespace ISCSI_Util.Views;

public partial class MutualChapDialog : Window
{
    private readonly IscsiDestino _destino;

    public MutualChapDialog(IscsiDestino destino)
    {
        InitializeComponent();
        _destino = destino;

        // Cargar valores actuales
        UserBox.Text = destino.UsuarioChap;
        PassBox.Text = destino.PasswordChap;
        UserInBox.Text = destino.UsuarioMutualChap;
        PassInBox.Text = destino.PasswordMutualChap;

        CancelBtn.Click += (_, _) => Close();
        ApplyBtn.Click += ApplyChanges;
    }

    private void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string user = UserBox.Text?.Trim() ?? "";
        string pass = PassBox.Text?.Trim() ?? "";
        string userIn = UserInBox.Text?.Trim() ?? "";
        string passIn = PassInBox.Text?.Trim() ?? "";

        // Guardar en el modelo
        _destino.UsaChap = true;
        _destino.UsaMutualChap = true;

        _destino.UsuarioChap = user;
        _destino.PasswordChap = pass;

        _destino.UsuarioMutualChap = userIn;
        _destino.PasswordMutualChap = passIn;

        // Aplicar a iscsiadm
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP");

        // CHAP normal
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username --value={user}");

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password --value={pass}");

        // Mutual CHAP
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username_in --value={userIn}");

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password_in --value={passIn}");

        // Refrescar estado CHAP real
        IscsiHelper.DetectarChap(_destino);

        Close();
    }
}
