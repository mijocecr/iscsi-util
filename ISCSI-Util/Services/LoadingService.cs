using System;
using ISCSI_Util.Views;

namespace ISCSI_Util.Services;

/// <summary>
/// Muestra un único diálogo de carga compartido por todas las operaciones.
///
/// El diálogo se mantiene abierto mientras haya al menos un scope activo.
/// Antes se cerraba al terminar la primera operación, aunque otra siguiera
/// en curso, y eso daba la sensación de que la ventana "parpadeaba" o se
/// quedaba colgada.
/// </summary>
public static class LoadingService
{
    private static readonly object Sync = new();
    private static LoadingDialog? _dialog;
    private static int _references;

    public static IDisposable Show(string message)
    {
        lock (Sync)
        {
            _references++;

            if (_dialog == null)
            {
                _dialog = new LoadingDialog(message);
                _dialog.Show();
            }

            _dialog.SetMessage(message);
        }

        return new LoadingScope(Release);
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (_references > 0)
                _references--;

            if (_references > 0)
                return;

            _dialog?.Close();
            _dialog = null;
        }
    }
}
