using Avalonia;
using System;
using System.Threading.Tasks;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;

namespace ISCSI_Util;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        RegisterGlobalExceptionHandlers();

        bool hasDisplay = Environment.GetEnvironmentVariable("DISPLAY") != null;
        bool isTerminal = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        if (!hasDisplay && isTerminal)
        {
            try
            {
                CliApp.Run().Wait();
            }
            catch (Exception ex)
            {
                LogService.Error($"[CLI] Fatal error: {ex}");
                Environment.ExitCode = 1;
            }

            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Registra manejadores de último recurso para que un fallo inesperado
    /// quede en el log en lugar de cerrar la aplicación sin rastro.
    /// </summary>
    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogService.Error($"[GLOBAL] Unhandled exception: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogService.Error($"[GLOBAL] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}