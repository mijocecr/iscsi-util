namespace ISCSI_Util.Helpers;

using System;
using System.IO;
using System.Linq;

// Comprueba el entorno del sistema para saber si open-iscsi (y las
// herramientas que el programa necesita) están instalados. No requiere
// privilegios de root: solo inspecciona binarios y unidades systemd, por lo
// que puede ejecutarse antes de autenticar con sudo.
public static class IscsiEnvironment
{
    // Rutas habituales de los binarios de open-iscsi en las principales
    // distribuciones (Debian/Ubuntu, Fedora/RHEL, Arch, openSUSE, Alpine…).
    private static readonly string[] KnownIscsiadmPaths =
    {
        "/usr/sbin/iscsiadm",
        "/sbin/iscsiadm",
        "/usr/bin/iscsiadm",
        "/usr/local/sbin/iscsiadm",
        "/usr/local/bin/iscsiadm"
    };

    private static readonly string[] KnownIscsidPaths =
    {
        "/usr/sbin/iscsid",
        "/sbin/iscsid",
        "/usr/bin/iscsid",
        "/usr/local/sbin/iscsid",
        "/usr/local/bin/iscsid"
    };

    // Hechos recogidos del sistema, separados de la decisión para poder
    // probarlos sin tocar el sistema real.
    public readonly record struct Probe(
        bool IscsiadmOnPath,
        bool IscsiadmAtKnownPath,
        bool IscsidOnPath,
        bool IscsidAtKnownPath,
        bool SystemdUnitPresent,
        bool SudoOnPath,
        bool SystemctlOnPath);

    // open-iscsi está instalado si aparece cualquiera de sus componentes
    // conocidos (el cliente iscsiadm, el demonio iscsid o su unidad systemd).
    public static bool IsOpenIscsiInstalled(in Probe probe) =>
        probe.IscsiadmOnPath ||
        probe.IscsiadmAtKnownPath ||
        probe.IscsidOnPath ||
        probe.IscsidAtKnownPath ||
        probe.SystemdUnitPresent;

    // El programa eleva privilegios con sudo para ejecutar iscsiadm.
    public static bool IsSudoAvailable(in Probe probe) => probe.SudoOnPath;

    // Las distros modernas usan systemd para gestionar iscsid.
    public static bool IsSystemdAvailable(in Probe probe) => probe.SystemctlOnPath;

    // Recoge los hechos del sistema actual. Es la única parte que ejecuta
    // comandos; la decisión vive en las funciones puras de arriba.
    public static Probe Inspect()
    {
        if (!OperatingSystem.IsLinux())
        {
            // En plataformas no Linux no bloqueamos el arranque: el programa
            // está pensado para Linux, pero no debe impedir su ejecución.
            return new Probe(true, false, false, false, false, true, true);
        }

        var iscsiadm = ShellHelper.RunClean("command -v iscsiadm 2>/dev/null");
        var iscsid = ShellHelper.RunClean("command -v iscsid 2>/dev/null");
        var sudo = ShellHelper.RunClean("command -v sudo 2>/dev/null");
        var systemctl = ShellHelper.RunClean("command -v systemctl 2>/dev/null");
        var unit = ShellHelper.RunClean(
            "systemctl list-unit-files iscsid.service 2>/dev/null");

        return new Probe(
            IscsiadmOnPath: iscsiadm.ExitCode == 0 && !string.IsNullOrWhiteSpace(iscsiadm.Stdout),
            IscsiadmAtKnownPath: KnownIscsiadmPaths.Any(File.Exists),
            IscsidOnPath: iscsid.ExitCode == 0 && !string.IsNullOrWhiteSpace(iscsid.Stdout),
            IscsidAtKnownPath: KnownIscsidPaths.Any(File.Exists),
            SystemdUnitPresent: HasIscsidUnit(unit.Stdout),
            SudoOnPath: sudo.ExitCode == 0 && !string.IsNullOrWhiteSpace(sudo.Stdout),
            SystemctlOnPath: systemctl.ExitCode == 0 && !string.IsNullOrWhiteSpace(systemctl.Stdout));
    }

    public static bool IsOpenIscsiInstalled() => IsOpenIscsiInstalled(Inspect());

    // Detecta la unidad iscsid.service en la salida de
    // `systemctl list-unit-files`. Es tolerante con formatos y estados.
    public static bool HasIscsidUnit(string? systemctlOutput)
    {
        if (string.IsNullOrWhiteSpace(systemctlOutput))
            return false;

        return systemctlOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.StartsWith("iscsid.service", StringComparison.OrdinalIgnoreCase));
    }
}
