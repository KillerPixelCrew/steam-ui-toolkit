namespace SteamUiToolkit.Tests;

/// <summary>Steam's remote-debugging opt-in and the anti-squatter gates in front of its CEF port.</summary>
/// <remarks>
///     The port is unauthenticated by design (Steam's CEF has no auth) and loopback-only, so
///     these checks are what stops another same-user listener from being driven as if it were Steam, or
///     redirecting the CDP client off-box through a spoofed target list.
/// </remarks>
public sealed class SteamCefTests
{
    private const int DebugPort = 8080;

    [Fact]
    public void DebugFlagFollowsExplicitOptInAndPreservesExistingFlag()
    {
        using var directory = new TemporaryDirectory();
        var flag = Path.Combine(directory.Root, ".cef-enable-remote-debugging");
        Assert.False(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, false));
        Assert.False(File.Exists(flag));
        Assert.True(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, true));
        Assert.True(File.Exists(flag));
        Assert.False(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, false));
        Assert.True(File.Exists(flag));
    }

    [Fact]
    public void ResolverResourceEncodesScopeAndContainsSharedDiscoveryBoundary()
    {
        var expression = SteamUiModuleResolver.CreateExpression("quote\"\\\n");
        Assert.Contains("function createSteamUiModuleResolver", expression);
        Assert.EndsWith($")({SteamCef.JsString("quote\"\\\n")})", expression);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:8080/devtools/page/A")]
    [InlineData("ws://localhost:8080/devtools/page/A")]
    [InlineData("wss://127.0.0.1:8080/devtools/page/A")]
    public void LoopbackWebSocketUrlsOnTheDebugPortAreAccepted(string url)
    {
        Assert.True(SteamCef.IsAllowedDebuggerUrl(url));
    }

    [Theory]
    [InlineData("ws://10.0.0.5:8080/devtools/page/A")] // off-box host
    [InlineData("ws://evil.example:8080/devtools/page/A")] // named foreign host
    [InlineData("ws://127.0.0.1:9222/devtools/page/A")] // foreign port
    [InlineData("http://127.0.0.1:8080/devtools/page/A")] // non-WebSocket scheme
    [InlineData("file:///C:/windows/system32/cmd.exe")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingOtherThanALoopbackWebSocketOnTheDebugPortIsRejected(string? url)
    {
        Assert.False(SteamCef.IsAllowedDebuggerUrl(url));
    }

    [Fact]
    public void AnUnreadableListenerTableIsNotTreatedAsNothingListening()
    {
        Assert.False(SteamCef.IsSteamPortOwner(null, static _ => "steam", out var reason));
        Assert.Contains("unavailable", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoListenerOnTheDebugPortIsNotSteam()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, 1234, 42)],
            static _ => "steam",
            out var reason));
        Assert.Contains("nothing is listening", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    public void ASteamProcessOwningTheDebugPortIsAccepted(string name)
    {
        Assert.True(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            _ => name,
            out var reason));
        Assert.Contains(name, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignProcessOwningTheDebugPortIsRejected()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            static _ => "squatter",
            out var reason));
        Assert.Contains("squatter", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AListenerWhoseProcessHasExitedDoesNotDecideTheVerdict()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            static _ => null,
            out var reason));
        Assert.Contains("could not be attributed", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The four ways the probe can fail must not describe each other. The caller logs this string
    ///     verbatim, and when it hardcoded one cause for all of them the log reported a squatter on a
    ///     port that nothing was listening on — sending the maintainer after a process that did not
    ///     exist.
    /// </summary>
    [Fact]
    public void EachRefusalReasonNamesItsOwnCause()
    {
        NativeTcp.Listener[] onPort = [new(NativeTcp.Loopback, DebugPort, 42)];
        SteamCef.IsSteamPortOwner(null, static _ => "steam", out var unreadable);
        SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, 1234, 42)],
            static _ => "steam",
            out var absent);
        SteamCef.IsSteamPortOwner(onPort, static _ => "squatter", out var foreign);
        SteamCef.IsSteamPortOwner(onPort, static _ => null, out var unidentified);

        string[] reasons = [unreadable, absent, foreign, unidentified];
        Assert.Equal(reasons.Length, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(reasons, r => string.IsNullOrWhiteSpace(r));
    }

    /// A squatter on 127.0.0.1 must not be able to hide behind Steam's own wildcard
    /// row: the loopback listener is examined first, so it — not Steam — decides.
    [Fact]
    public void ALoopbackSquatterIsCheckedBeforeSteamsWildcardListener()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [
                new NativeTcp.Listener(NativeTcp.AnyAddress, DebugPort, 1),
                new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 2)
            ],
            static pid => pid == 1 ? "steam" : "squatter",
            out _));
    }
}
