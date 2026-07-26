using Avalonia;
using System;
using ISCSI_Util.Helpers;

namespace ISCSI_Util;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool hasDisplay = Environment.GetEnvironmentVariable("DISPLAY") != null;
        bool isTerminal = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        if (!hasDisplay && isTerminal)
        {
            CliApp.Run().Wait();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}