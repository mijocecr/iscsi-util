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
}
