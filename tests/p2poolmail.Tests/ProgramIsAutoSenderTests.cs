using System.Reflection;
using p2poolmail;

namespace p2poolmail.Tests;

/// <summary>Tests Program.IsAutoSender (private static, invoked via reflection).</summary>
public class ProgramIsAutoSenderTests
{
    private static readonly MethodInfo IsAutoSender = typeof(Program)
        .GetMethod("IsAutoSender", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Program.IsAutoSender not found");

    private static bool IsAuto(string address) => (bool)IsAutoSender.Invoke(null, [address])!;

    [Theory]
    [InlineData("no-reply@example.com")]
    [InlineData("noreply@example.com")]
    [InlineData("donotreply@example.com")]
    [InlineData("service-noreply@example.com")]
    [InlineData("team_noreply@example.com")]
    [InlineData("mailer-daemon@example.com")]
    [InlineData("postmaster@example.com")]
    [InlineData("bounce-123@example.com")]
    [InlineData("bounces@mta.example.com")]
    [InlineData("noreply+tag@example.com")]
    [InlineData("No-Reply@Example.COM")]
    public void AutoSenders_AreMatched(string address)
    {
        Assert.True(IsAuto(address));
    }

    [Theory]
    [InlineData("miner@example.com")]
    [InlineData("me@gmail.com")]
    [InlineData("reply@example.com")]
    [InlineData("noreplyfan@example.com")]
    [InlineData("postmastersunion@example.com")]
    [InlineData("a@b")]
    // Note: the matcher is exact/suffix based, so do_not_reply is not in the
    // catalog - documented behavior, not a bug.
    [InlineData("do_not_reply@example.com")]
    public void HumanSenders_AreNotMatched(string address)
    {
        Assert.False(IsAuto(address));
    }

    [Fact]
    public void AddressWithoutLocalPart_IsNotAuto()
    {
        Assert.False(IsAuto("@example.com"));
    }
}
