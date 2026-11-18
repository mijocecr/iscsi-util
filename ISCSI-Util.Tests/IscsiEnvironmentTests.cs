using ISCSI_Util.Helpers;

namespace ISCSI_Util.Tests;

public class IscsiEnvironmentTests
{
    private static IscsiEnvironment.Probe Probe(
        bool iscsiadmOnPath = false,
        bool iscsiadmAtKnownPath = false,
        bool iscsidOnPath = false,
        bool iscsidAtKnownPath = false,
        bool systemdUnitPresent = false) =>
        new(iscsiadmOnPath, iscsiadmAtKnownPath, iscsidOnPath, iscsidAtKnownPath,
            systemdUnitPresent, SudoOnPath: true, SystemctlOnPath: true);

    [Fact]
    public void Installed_when_iscsiadm_is_on_path()
        => Assert.True(IscsiEnvironment.IsOpenIscsiInstalled(Probe(iscsiadmOnPath: true)));

    [Fact]
    public void Installed_when_iscsiadm_is_at_a_known_path()
        => Assert.True(IscsiEnvironment.IsOpenIscsiInstalled(Probe(iscsiadmAtKnownPath: true)));

    [Fact]
    public void Installed_when_only_the_daemon_is_present()
        => Assert.True(IscsiEnvironment.IsOpenIscsiInstalled(Probe(iscsidOnPath: true)));

    [Fact]
    public void Installed_when_only_the_systemd_unit_is_present()
        => Assert.True(IscsiEnvironment.IsOpenIscsiInstalled(Probe(systemdUnitPresent: true)));

    [Fact]
    public void Not_installed_when_nothing_is_found()
        => Assert.False(IscsiEnvironment.IsOpenIscsiInstalled(Probe()));

    [Theory]
    [InlineData("iscsid.service   enabled", true)]
    [InlineData("  iscsid.service    enabled enabled", true)]
    [InlineData("iscsid.socket    enabled", false)]
    [InlineData("sshd.service enabled\niscsid.service disabled", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasIscsidUnit_parses_systemctl_output(string? output, bool expected)
        => Assert.Equal(expected, IscsiEnvironment.HasIscsidUnit(output));
}
