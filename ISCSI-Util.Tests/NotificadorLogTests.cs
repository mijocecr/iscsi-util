using System;
using System.IO;
using System.Threading;
using ISCSI_Util.Helpers;
using ISCSI_Util.Services;
using Xunit;

namespace ISCSI_Util.Tests;

public sealed class NotificadorLogTests
{
    [Fact]
    public void NotificationFallbackGoesToLogNotConsole()
    {
        var temp = Path.Combine(
            Path.GetTempPath(), "iscsi-util-notify-test-" + Guid.NewGuid().ToString("N"));
        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var originalLogPath = ConfigManager.LogPath;
        var originalOut = Console.Out;

        try
        {
            var fakeBin = Path.Combine(temp, "bin");
            Directory.CreateDirectory(fakeBin);
            var which = Path.Combine(fakeBin, "which");
            File.WriteAllText(which, "#!/bin/sh\nexit 1\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(which,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);
            ConfigManager.LogPath = Path.Combine(temp, "logs");
            var output = new StringWriter();
            Console.SetOut(output);

            NotificadorLinux.Enviar("mensaje-de-prueba");

            var logFile = Path.Combine(ConfigManager.LogPath, "iscsi-util.log");
            for (var i = 0; i < 100 && !File.Exists(logFile); i++)
                Thread.Sleep(20);

            Assert.True(File.Exists(logFile));
            Assert.Contains("mensaje-de-prueba", File.ReadAllText(logFile));
            Assert.DoesNotContain("[NOTIFICACIÓN]", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            ConfigManager.LogPath = originalLogPath;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
