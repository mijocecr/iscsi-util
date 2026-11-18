using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Models;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util.Views;

public partial class ChapDialog : Window
{
    private readonly IscsiDestino _destino;

    public ChapDialog(IscsiDestino destino)
    {
        LogService.Debug($"[CHAP_DIALOG] Inicializando diálogo para {destino.Iqn} ({destino.Ip})");

        InitializeComponent();
        _destino = destino;

        // Cargar valores actuales (del usuario o detectados).
        // open-iscsi devuelve los secretos enmascarados como "********":
        // ese valor nunca se precarga ni se reutiliza como contraseña.
        UserBox.Text = IscsiSecrets.IsUsable(destino.UsuarioChap)
            ? destino.UsuarioChap
            : destino.LocalUser;

        var savedPass = IscsiSecrets.Usable(destino.PasswordChap);
        var detectedPass = IscsiSecrets.Usable(destino.LocalPass);
        PassBox.Text = savedPass.Length > 0 ? savedPass : detectedPass;

        LogService.Debug(
            $"[CHAP_DIALOG] Valores iniciales cargados. User='{UserBox.Text}', " +
            $"secret={(PassBox.Text?.Length > 0 ? "available" : "stored/not-reusable")}"
        );

        CancelBtn.Click += (_, _) =>
        {
            LogService.Debug("[CHAP_DIALOG] Cancelado por el usuario.");
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
            bool passProvided = IscsiSecrets.IsUsable(pass);

            LogService.Write(
                $"[CHAP_DIALOG] Aplicando CHAP para {_destino.Iqn} con usuario '{user}' " +
                $"(secret={(passProvided ? "provided" : "reusing stored")})"
            );

            if (user.Length == 0)
            {
                NotificadorLinux.Enviar("[ERROR] The CHAP username is required.", 6000, "critical");
                return;
            }

            // Si no se introduce una contraseña nueva, solo se permite cuando
            // ya existe un secreto utilizable o configurado en el nodo.
            bool hasStoredSecret =
                IscsiSecrets.IsUsable(_destino.PasswordChap) ||
                _destino.HasLocalChapConfigured;

            if (!passProvided && !hasStoredSecret)
            {
                NotificadorLinux.Enviar("[ERROR] The CHAP password is required.", 6000, "critical");
                return;
            }

            var portal = IscsiSecrets.PortalArg(_destino.Ip);

            // --------------------------------------------------------------
            // 1) Guardar en el modelo (preferencia del usuario)
            // --------------------------------------------------------------
            _destino.UsaChap = true;
            _destino.UsuarioChap = user;

            if (passProvided)
                _destino.PasswordChap = pass;

            // --------------------------------------------------------------
            // 2) Aplicar a iscsiadm (solo CHAP outgoing)
            // --------------------------------------------------------------
            LogService.Debug("[CHAP_DIALOG] Actualizando CHAP en iscsiadm...");

            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.authmethod --value=CHAP"
            );

            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.username --value={ShellHelper.QuoteArg(user)}"
            );

            // Solo se sobrescribe el secreto cuando el usuario ha introducido
            // una contraseña real; nunca con el valor enmascarado de open-iscsi.
            if (passProvided)
            {
                await ShellHelper.EjecutarComoRootAsync(
                    $"iscsiadm -m node -T {ShellHelper.QuoteArg(_destino.Iqn)} -p {ShellHelper.QuoteArg(portal)} --op=update --name node.session.auth.password --value={ShellHelper.QuoteArg(pass)}"
                );
            }

            // --------------------------------------------------------------
            // 3) Refrescar estado CHAP real
            // --------------------------------------------------------------
            LogService.Debug("[CHAP_DIALOG] Ejecutando detección CHAP...");

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
                $"[CHAP_DIALOG] Resultado CHAP para {_destino.Iqn}: " +
                $"Requires={_destino.RequiresChap}, Mutual={_destino.RequiresMutualChap}, " +
                $"LocalCHAP={_destino.HasLocalChapConfigured}, LocalMutual={_destino.HasLocalMutualConfigured}"
            );

            LogService.Debug("[CHAP_DIALOG] Cerrando diálogo.");
            Close();
        }
        catch (Exception ex)
        {
            LogService.Error($"[CHAP_DIALOG] Error aplicando CHAP: {ex.Message}");
            NotificadorLinux.Enviar("[ERROR] Could not apply CHAP.", 6000, "critical");
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
        }
    }
}
