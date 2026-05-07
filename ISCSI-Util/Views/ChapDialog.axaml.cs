using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;

namespace ISCSI_Util.Views;

public partial class ChapDialog : Window
{
    private readonly IscsiDestino _destino;

    public ChapDialog(IscsiDestino destino)
    {
        InitializeComponent();
        _destino = destino;

        // Cargar valores actuales
        UserBox.Text = destino.UsuarioChap;
        PassBox.Text = destino.PasswordChap;

        CancelBtn.Click += (_, _) => Close();
        ApplyBtn.Click += ApplyChanges;
    }

    private void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string user = UserBox.Text?.Trim() ?? "";
        string pass = PassBox.Text?.Trim() ?? "";

        // Guardar en el modelo
        _destino.UsaChap = true;
        _destino.UsuarioChap = user;
        _destino.PasswordChap = pass;

        // Aplicar a iscsiadm
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP");

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username --value={user}");

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password --value={pass}");

        // Refrescar estado CHAP real
        IscsiHelper.DetectarChap(_destino);

        Close();
    }
}