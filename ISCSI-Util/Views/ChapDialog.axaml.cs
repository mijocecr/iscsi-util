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

        // Cargar valores actuales (del usuario o detectados)
        UserBox.Text = string.IsNullOrWhiteSpace(destino.UsuarioChap)
            ? destino.LocalUser
            : destino.UsuarioChap;

        PassBox.Text = string.IsNullOrWhiteSpace(destino.PasswordChap)
            ? destino.LocalPass
            : destino.PasswordChap;

        CancelBtn.Click += (_, _) => Close();
        ApplyBtn.Click += ApplyChanges;
    }

    private void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string user = UserBox.Text?.Trim() ?? "";
        string pass = PassBox.Text?.Trim() ?? "";

        // --------------------------------------------------------------
        // 1) Guardar en el modelo (preferencia del usuario)
        // --------------------------------------------------------------
        _destino.UsaChap = true;
        _destino.UsuarioChap = user;
        _destino.PasswordChap = pass;

        // --------------------------------------------------------------
        // 2) Aplicar a iscsiadm (solo CHAP outgoing)
        // --------------------------------------------------------------
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
        );

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username --value=\"{user}\""
        );

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password --value=\"{pass}\""
        );

        // --------------------------------------------------------------
        // 3) Refrescar estado CHAP real
        // --------------------------------------------------------------
        var r = IscsiChapDetector.Detect(_destino);

        _destino.RequiresChap = r.RequiresChap;
        _destino.RequiresMutualChap = r.RequiresMutualChap;
        _destino.HasLocalChapConfigured = r.HasLocalChapConfigured;
        _destino.HasLocalMutualConfigured = r.HasLocalMutualConfigured;

        _destino.LocalUser = r.LocalUser;
        _destino.LocalPass = r.LocalPass;
        _destino.LocalUserIn = r.LocalUserIn;
        _destino.LocalPassIn = r.LocalPassIn;

        // Flags usados por la UI
        _destino.UsaChap = _destino.RequiresChap || _destino.HasLocalChapConfigured;
        _destino.UsaMutualChap = _destino.RequiresMutualChap || _destino.HasLocalMutualConfigured;

        Close();
    }
}
