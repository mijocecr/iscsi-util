using ISCSI_Util.Views;

namespace ISCSI_Util.Tests;

public class StatusViewHelpersTests
{
    [Theory]
    [InlineData("docker0", true)]
    [InlineData("docker123", true)]
    [InlineData("veth0abc", true)]
    [InlineData("br-1a2b3c", true)]
    [InlineData("virbr0", true)]
    [InlineData("vmnet8", true)]
    [InlineData("tun0", true)]
    [InlineData("tap0", true)]
    [InlineData("wg0", true)]
    [InlineData("lo", true)]
    [InlineData("eth0", false)]
    [InlineData("enp0s3", false)]
    [InlineData("wlan0", false)]
    [InlineData("br0", false)]
    [InlineData("bond0", false)]
    [InlineData("eth0.100", false)]
    public void IsVirtualInterface_classifies_interface_names(string name, bool expected)
        => Assert.Equal(expected, StatusView.IsVirtualInterface(name));

    [Fact]
    public void SelectPrimaryIpAddress_ignores_virtual_interfaces()
    {
        const string output =
            "4: br0    inet 192.168.10.71/24 brd 192.168.10.255 scope global dynamic br0\\       valid_lft forever\n" +
            "5: docker0    inet 172.17.0.1/16 brd 172.17.255.255 scope global docker0\\       valid_lft forever";

        Assert.Equal("192.168.10.71", StatusView.SelectPrimaryIpAddress(output));
    }

    [Fact]
    public void SelectPrimaryIpAddress_keeps_valid_private_ranges()
    {
        // Regresión: antes se descartaban rangos como 172.16.x o 192.168.194.x.
        const string output =
            "3: eth1    inet 172.16.5.9/24 brd 172.16.5.255 scope global eth1\\       valid_lft forever";

        Assert.Equal("172.16.5.9", StatusView.SelectPrimaryIpAddress(output));
    }

    [Theory]
    [InlineData("5: docker0    inet 172.17.0.1/16 brd 172.17.255.255 scope global docker0\\  valid_lft forever")]
    [InlineData("1: lo    inet 127.0.0.1/8 scope host lo\\       valid_lft forever")]
    [InlineData("")]
    [InlineData(null)]
    public void SelectPrimaryIpAddress_returns_unknown_without_a_real_interface(string? output)
        => Assert.Equal("Unknown", StatusView.SelectPrimaryIpAddress(output));

    [Theory]
    [InlineData("ActiveState=active\nSubState=running", "ActiveState", "active")]
    [InlineData("ActiveState=active\nSubState=running", "SubState", "running")]
    [InlineData("ActiveState=inactive", "ActiveState", "inactive")]
    [InlineData("SubState=Running", "substate", "running")]
    [InlineData("ActiveState=active", "Missing", "")]
    [InlineData("", "ActiveState", "")]
    public void GetSystemdProperty_reads_named_property(string output, string name, string expected)
        => Assert.Equal(expected, StatusView.GetSystemdProperty(output, name));

    [Theory]
    [InlineData("123456.78 999.99", 123456.78)]
    [InlineData("0.00 1.00", 0.0)]
    [InlineData("  42.5  10.0 ", 42.5)]
    public void ParseUpSeconds_reads_first_proc_field(string procUptime, double expected)
        => Assert.Equal(expected, StatusView.ParseUpSeconds(procUptime));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc def")]
    [InlineData("-5.0 0.0")]
    [InlineData(null)]
    public void ParseUpSeconds_returns_null_on_invalid_input(string? procUptime)
        => Assert.Null(StatusView.ParseUpSeconds(procUptime));

    [Theory]
    [InlineData(0.0, "0 seconds")]
    [InlineData(1.0, "1 second")]
    [InlineData(60.0, "1 minute")]
    [InlineData(3600.0, "1 hour")]
    [InlineData(90061.0, "1 day, 1 hour, 1 minute")]
    public void FormatUptime_builds_locale_independent_text(double seconds, string expected)
        => Assert.Equal(expected, StatusView.FormatUptime(seconds));

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(-1.0, "Unknown")]
    public void FormatUptime_returns_unknown_on_invalid_input(double? seconds, string expected)
        => Assert.Equal(expected, StatusView.FormatUptime(seconds));

    [Theory]
    [InlineData("default via 192.168.1.1 dev eth0 proto static metric 100", "192.168.1.1")]
    [InlineData("default via fe80::1 dev eth0 proto ra metric 1024", "fe80::1")]
    [InlineData("192.168.1.0/24 dev eth0 proto kernel scope link", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SelectDefaultGateway_parses_ip_route_output(string? output, string? expected)
        => Assert.Equal(expected, StatusView.SelectDefaultGateway(output));

    [Theory]
    [InlineData("64 bytes from 8.8.8.8: icmp_seq=1 ttl=117 time=12.3 ms", "12.3")]
    [InlineData("64 bytes from 192.168.1.1: icmp_seq=1 ttl=64 time=0.045 ms", "0.045")]
    [InlineData("Request timeout for icmp_seq 0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseLatencyMs_reads_ping_output(string? output, string? expected)
        => Assert.Equal(expected, StatusView.ParseLatencyMs(output));

    [Theory]
    [InlineData("Running")]
    [InlineData("Starting")]
    [InlineData("running")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ServiceHintFor_returns_null_when_daemon_is_running(string? iscsidText)
        => Assert.Null(StatusView.ServiceHintFor(iscsidText));

    [Theory]
    [InlineData("Stopped")]
    [InlineData("Failed")]
    public void ServiceHintFor_shows_command_when_daemon_is_down(string iscsidText)
    {
        var hint = StatusView.ServiceHintFor(iscsidText);

        Assert.NotNull(hint);
        Assert.Contains("systemctl enable --now iscsid", hint);
        Assert.Contains("Start Service", hint);
    }

    [Fact]
    public void ServiceHintFor_shows_guidance_when_state_is_unknown()
    {
        var hint = StatusView.ServiceHintFor("Unknown");

        Assert.NotNull(hint);
        Assert.Contains("systemd may not be available", hint);
    }

    [Theory]
    [InlineData("Stopped", true)]
    [InlineData("Failed", true)]
    [InlineData(" stopped ", true)]
    [InlineData("Running", false)]
    [InlineData("Starting", false)]
    [InlineData("Unknown", false)]
    [InlineData(null, false)]
    public void NeedsStart_selects_the_dynamic_button_action(string? iscsidText, bool expected)
        => Assert.Equal(expected, StatusView.NeedsStart(iscsidText));
}
