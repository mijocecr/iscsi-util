using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using ISCSI_Util.Helpers;
using ISCSI_Util.Services;
using ISCSI_Util.Views;

namespace ISCSI_Util;

/// <summary>
/// Main application class for the iSCSI Utility.
/// Initializes Avalonia and sets up the main window with its view model.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Initializes the Avalonia framework and loads XAML resources.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Completes framework initialization and creates the main window.
    /// Disables redundant data validation plugins for cleaner binding behavior.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // Último recurso para excepciones no controladas en el hilo de UI.
        // Se registra en el log y se evita que Avalonia cierre la ventana.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogService.Error($"[GLOBAL] UI thread exception: {e.Exception}");
            NotificadorLinux.Enviar(
                $"Unexpected error: {e.Exception.Message}", 8000, "critical");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // Antes de abrir la ventana principal comprobamos que open-iscsi
            // esté instalado. Si no lo está, mostramos un aviso con las
            // instrucciones de instalación y cerramos la aplicación de forma
            // limpia al cerrar esa ventana.
            if (!IscsiEnvironment.IsOpenIscsiInstalled())
            {
                LogService.Write("[APP] open-iscsi is not installed; showing installation notice.");
                desktop.MainWindow = new IscsiMissingWindow();
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            // Después comprobamos que el usuario pueda usar sudo: sin privilegios
            // de administrador iSCSI-Util no puede ejecutar iscsiadm. Se avisa en
            // lugar de pedir una contraseña que nunca funcionaría.
            else if (!SudoEnvironment.CurrentUserHasSudo())
            {
                LogService.Write("[APP] Current user cannot run sudo; showing privileges notice.");
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                desktop.MainWindow = new SudoRequiredWindow(
                    () => new MainWindow { DataContext = new MainWindow() }.Show());
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindow(),
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Removes Avalonia data annotation validation plugins to avoid conflicts with CommunityToolkit validation.
    /// Prevents duplicate validation warnings and improves data binding behavior.
    /// </summary>
    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}