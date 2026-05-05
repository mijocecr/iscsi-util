using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindow(),
            };
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