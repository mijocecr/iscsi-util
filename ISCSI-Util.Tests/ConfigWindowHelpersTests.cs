using ISCSI_Util.Views;

namespace ISCSI_Util.Tests;

public class ConfigWindowHelpersTests
{
    [Theory]
    [InlineData("iqn.2000-01.com.synology:NAS.Target1", "NAS.Target1")]
    [InlineData("iqn.1991-05.com.microsoft:win-abc.def", "win-abc.def")]
    [InlineData("iqn.1994-04.com.redhat:storage.example.com:disk0", "disk0")]
    [InlineData("plain-name", "plain-name")]
    [InlineData("iqn.2000-01.com.example:", "iqn.2000-01.com.example:")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortIqn_returns_the_name_after_the_last_colon(string? iqn, string expected)
        => Assert.Equal(expected, ConfigWindow.ShortIqn(iqn));
}
