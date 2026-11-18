using System;
using System.Threading.Tasks;
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

        // Cargar valores actuales (preferir usuario → si no, valores detectados).
        // Los secretos enmascarados por open-iscsi ("********") nunca se
        // precargan ni se reutilizan como contraseña real.
        UserBox.Text = IscsiSecrets.IsUsable(destino.UsuarioChap)
            ? destino.UsuarioChap
            : destino.LocalUser;

        var savedPass = IscsiSecrets.Usable(destino.PasswordChap);
        var detectedPass = IscsiSecrets.Usable(destino.LocalPass);
        PassBox.Text = savedPass.Length > 0 ? savedPass : detectedPass;

        UserInBox.Text = IscsiSecrets.IsUsable(destino.UsuarioMutualChap)
            ? destino.UsuarioMutualChap
            : destino.LocalUserIn;

        var savedPassIn = IscsiSecrets.Usable(destino.PasswordMutualChap);
        var detectedPassIn = IscsiSecrets.Usable(destino.LocalPassIn);
        PassInBox.Text = savedPassIn.Length > 0 ? savedPassIn : detectedPassIn;

        LogService.Debug(
            $"[MUTUAL_CHAP] Valores iniciales cargados. Out='{UserBox.Text}', In='{UserInBox.Text}', " +
            $"outgoing secret={(PassBox.Text?.Length > 0 ? "available" : "stored/not-reusable")}, " +
            $"incoming secret={(PassInBox.Text?.Length > 0 ? "available" : "stored/not-reusable")}"
        );

        CancelBtn.Click += (_, _) =>
        {
            LogService.Debug("[MUTUAL_CHAP] Cancelado por el usuario.");
            Close();
        };

        ApplyBtn.Click += ApplyChanges;
    }

    private async void ApplyChanges(object? sender, RoutedEventArgs e)
    {
        if (!ApplyBtn.IsEnabled)
            return;

        ApplyBtn.IsEnabled = false;

        try
        {
            string user = UserBox.Text?.Trim() ?? "";
            string pass = PassBox.Text?.Trim() ?? "";
            string userIn = UserInBox.Text?.Trim() ?? "";
            string passIn = PassInBox.Text?.Trim() ?? "";

            bool passProvided = IscsiSecrets.IsUsable(pass);
            bool passInProvided = IscsiSecrets.IsUsable(passIn);

            LogService.Write(
                $"[MUTUAL_CHAP] Aplicando Mutual CHAP para {_destino.Iqn} → Out='{user}', In='{userIn}', " +
                $"outgoing secret={(passProvided ? "provided" : "reusing stored")}, " +
                $"incoming secret={(passInProvided ? "provided" : "reusing stored")}"
            );

            if (user.Length == 0)
            {
                NotificadorLinux.Enviar("[ERROR] The CHAP username is required.", 6000, "critical");
                return;
            }

            if (userIn.Length == 0)
            {
                NotificadorLinux.Enviar("[ERROR] The Mutual CHAP username is required.", 6000, "critical");
                return;
            }

            bool hasStoredOutgoing =
                IscsiSecrets.IsUsable(_destino.PasswordChap) ||
                _destino.HasLocalChapConfigured;

            bool hasStoredIncoming =
                IscsiSecrets.IsUsable(_destino.PasswordMutualChap) ||
                _destino.HasLocalMutualConfigured;

            if (!passProvided && !hasStoredOutgoing)
            {
                NotificadorLinux.Enviar("[ERROR] The CHAP password is required.", 6000, "critical");
                return;
            }

            if (!passInProvided && !hasStoredIncoming)
            {
                NotificadorLinux.Enviar("[ERROR] The Mutual CHAP password is required.", 6000, "critical");
                return;
            }

            var portal = IscsiSecrets.PortalArg(_destino.Ip);

            // --------------------------------------------------------------
            // 1) Guardar en el modelo (preferencias del usuario)
            // --------------------------------------------------------------
            _destino.UsaChap = true;
            _destino.UsaMutualChap = true;

            _destino.UsuarioChap = user;
            if (passProvided)
                _destino.PasswordChap = pass;

            _destino.UsuarioMutualChap = userIn;
            if (passInProvided)
                _destino.PasswordMutualChap = passIn;

            // --------------------------------------------------------------
            // 2) Aplicar a iscsiadm (CHAP + Mutual CHAP)
            // --------------------------------------------------------------
            LogService.Debug("[MUTUAL_CHAP] Actualizando CHAP y Mutual CHAP en iscsiadm...");

            // Activar CHAP
            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.authmethod --value=CHAP"
            );

            // CHAP outgoing
            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.username --value={ShellHelper.QuoteArg(user)}"
            );

            // Solo se sobrescriben los secretos cuando son reales.
            if (passProvided)
            {
                await ShellHelper.EjecutarComoRootAsync(
                    $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.password --value={ShellHelper.QuoteArg(pass)}"
                );
            }

            // Mutual CHAP incoming
            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.username_in --value={ShellHelper.QuoteArg(userIn)}"
            );

            if (passInProvided)
            {
                await ShellHelper.EjecutarComoRootAsync(
                    $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.password_in --value={ShellHelper.QuoteArg(passIn)}"
                );
            }

            // --------------------------------------------------------------
            // 3) Refrescar estado CHAP real
            // --------------------------------------------------------------
            LogService.Debug("[MUTUAL_CHAP] Ejecutando detección CHAP...");

            var r = await Task.Run(() => IscsiChapDetector.Detect(_destino));

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
        catch (Exception ex)
        {
            LogService.Error($"[MUTUAL_CHAP] Error aplicando Mutual CHAP: {ex.Message}");
            NotificadorLinux.Enviar("[ERROR] Could not apply Mutual CHAP.", 6000, "critical");
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
        }
    }
}
