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

        // Cargar valores actuales (del usuario o detectados)
        UserBox.Text = string.IsNullOrWhiteSpace(destino.UsuarioChap)
            ? destino.LocalUser
            : destino.UsuarioChap;

        PassBox.Text = string.IsNullOrWhiteSpace(destino.PasswordChap)
            ? destino.LocalPass
            : destino.PasswordChap;

        LogService.Debug($"[CHAP_DIALOG] Valores iniciales cargados. User='{UserBox.Text}'");

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

        string user = UserBox.Text?.Trim() ?? "";
        string pass = PassBox.Text?.Trim() ?? "";

        LogService.Write($"[CHAP_DIALOG] Aplicando CHAP para {_destino.Iqn} con usuario '{user}'");

        try
        {
            // --------------------------------------------------------------
            // 1) Guardar en el modelo (preferencia del usuario)
            // --------------------------------------------------------------
            _destino.UsaChap = true;
            _destino.UsuarioChap = user;
            _destino.PasswordChap = pass;

            // --------------------------------------------------------------
            // 2) Aplicar a iscsiadm (solo CHAP outgoing)
            // --------------------------------------------------------------
            LogService.Debug("[CHAP_DIALOG] Actualizando CHAP en iscsiadm...");

            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.authmethod --value=CHAP"
            );

            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.username --value=\"{user}\""
            );

            await ShellHelper.EjecutarComoRootAsync(
                $"iscsiadm -m node -T {_destino.Iqn} -p {_destino.Ip} --op=update --name node.session.auth.password --value=\"{pass}\""
            );

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
            NotificadorLinux.Enviar("[ERROR] No se pudo aplicar CHAP.", 6000, "critical");
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
        }
    }
}
