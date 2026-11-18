using System.Collections.Generic;
using ISCSI_Util.Helpers;

namespace ISCSI_Util.Tests;

public class SudoEnvironmentTests
{
    private static SudoEnvironment.Probe Probe(
        bool isRoot = false,
        bool sudoPresent = true,
        string[]? groups = null,
        int exit = 1,
        string output = "") =>
        new(isRoot, sudoPresent, groups ?? [], exit, output);

    [Fact]
    public void Root_always_has_sudo()
        => Assert.True(SudoEnvironment.HasSudoAccess(Probe(isRoot: true, sudoPresent: false)));

    [Fact]
    public void Missing_sudo_binary_means_no_sudo()
        => Assert.False(SudoEnvironment.HasSudoAccess(Probe(sudoPresent: false, groups: ["sudo"])));

    [Theory]
    [InlineData("sudo")]
    [InlineData("wheel")]
    [InlineData("admin")]
    [InlineData("SUDO")]
    public void Membership_in_privileged_group_grants_sudo(string group)
        => Assert.True(SudoEnvironment.HasSudoAccess(Probe(groups: [group])));

    [Fact]
    public void Unrelated_groups_do_not_grant_sudo()
        => Assert.False(SudoEnvironment.HasSudoAccess(
            Probe(groups: ["users", "video", "docker"], exit: 1, output: "is not in the sudoers file")));

    [Fact]
    public void Nopasswd_listing_grants_sudo()
        => Assert.True(SudoEnvironment.HasSudoAccess(Probe(exit: 0, output: "User miguel may run ...")));

    [Fact]
    public void Password_required_message_grants_sudo()
        => Assert.True(SudoEnvironment.HasSudoAccess(
            Probe(exit: 1, output: "sudo: a password is required")));

    [Fact]
    public void Not_in_sudoers_denies_sudo()
        => Assert.False(SudoEnvironment.HasSudoAccess(
            Probe(exit: 1, output: "miguel is not in the sudoers file. This incident will be reported.")));

    [Fact]
    public void Empty_output_without_group_denies_sudo()
        => Assert.False(SudoEnvironment.HasSudoAccess(Probe(exit: 1, output: "")));

    [Theory]
    [InlineData("sudo", true)]
    [InlineData("wheel", true)]
    [InlineData("ADMIN", true)]
    [InlineData("docker", false)]
    public void IsInSudoGroup_matches_case_insensitively(string group, bool expected)
        => Assert.Equal(expected, SudoEnvironment.IsInSudoGroup(new List<string> { group }));
}
