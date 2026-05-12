using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views;

public partial class MutualChapDialog : Window
{
    private readonly IscsiDestino _destino;

    public MutualChapDialog(IscsiDestino destino)
    {
        LogService.Debug($"[MUTUAL_CHAP] Inicializando diálogo para {destino.Iqn} ({destino.Ip})");

        InitializeComponent();
        _destino = destino;

        // Cargar valores actuales (preferir usuario → si no, valores detectados)
        UserBox.Text = string.IsNullOrWhiteSpace(destino.UsuarioChap)
            ? destino.LocalUser
            : destino.UsuarioChap;

        PassBox.Text = string.IsNullOrWhiteSpace(destino.PasswordChap)
            ? destino.LocalPass
            : destino.PasswordChap;

        UserInBox.Text = string.IsNullOrWhiteSpace(destino.UsuarioMutualChap)
            ? destino.LocalUserIn
            : destino.UsuarioMutualChap;

        PassInBox.Text = string.IsNullOrWhiteSpace(destino.PasswordMutualChap)
            ? destino.LocalPassIn
            : destino.PasswordMutualChap;

        LogService.Debug("[MUTUAL_CHAP] Valores iniciales cargados.");

        CancelBtn.Click += (_, _) =>
        {
            LogService.Debug("[MUTUAL_CHAP] Cancelado por el usuario.");
            Close();
        };

        ApplyBtn.Click += ApplyChanges;
    }

    private void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        string user = UserBox.Text?.Trim() ?? "";
        string pass = PassBox.Text?.Trim() ?? "";
        string userIn = UserInBox.Text?.Trim() ?? "";
        string passIn = PassInBox.Text?.Trim() ?? "";

        LogService.Write($"[MUTUAL_CHAP] Aplicando Mutual CHAP para {_destino.Iqn} → Out='{user}', In='{userIn}'");

        // --------------------------------------------------------------
        // 1) Guardar en el modelo (preferencias del usuario)
        // --------------------------------------------------------------
        _destino.UsaChap = true;
        _destino.UsaMutualChap = true;

        _destino.UsuarioChap = user;
        _destino.PasswordChap = pass;

        _destino.UsuarioMutualChap = userIn;
        _destino.PasswordMutualChap = passIn;

        // --------------------------------------------------------------
        // 2) Aplicar a iscsiadm (CHAP + Mutual CHAP)
        // --------------------------------------------------------------
        LogService.Debug("[MUTUAL_CHAP] Actualizando CHAP y Mutual CHAP en iscsiadm...");

        // Activar CHAP
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
        );

        // CHAP outgoing
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username --value=\"{user}\""
        );

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password --value=\"{pass}\""
        );

        // Mutual CHAP incoming
        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username_in --value=\"{userIn}\""
        );

        ShellHelper.EjecutarComoRoot(
            $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password_in --value=\"{passIn}\""
        );

        // --------------------------------------------------------------
        // 3) Refrescar estado CHAP real
        // --------------------------------------------------------------
        LogService.Debug("[MUTUAL_CHAP] Ejecutando detección CHAP...");

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

        LogService.Write(
            $"[MUTUAL_CHAP] Resultado final para {_destino.Iqn}: " +
            $"RequiresCHAP={_destino.RequiresChap}, RequiresMutual={_destino.RequiresMutualChap}, " +
            $"LocalCHAP={_destino.HasLocalChapConfigured}, LocalMutual={_destino.HasLocalMutualConfigured}"
        );

        LogService.Debug("[MUTUAL_CHAP] Cerrando diálogo.");
        Close();
    }
}
