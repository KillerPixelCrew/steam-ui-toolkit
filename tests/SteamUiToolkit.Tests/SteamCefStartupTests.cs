namespace SteamUiToolkit.Tests;

public sealed class SteamCefStartupTests
{
    [Fact]
    public void DebugFlagFollowsExplicitOptInAndPreservesExistingFlag()
    {
        using var directory = new TemporaryDirectory();
        string flag = Path.Combine(directory.Root, ".cef-enable-remote-debugging");
        Assert.False(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, enabled: false));
        Assert.False(File.Exists(flag));
        Assert.True(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, enabled: true));
        Assert.True(File.Exists(flag));
        Assert.False(SteamCef.EnsureRemoteDebuggingEnabled(directory.Root, enabled: false));
        Assert.True(File.Exists(flag));
    }

    [Fact]
    public void ResolverResourceEncodesScopeAndContainsSharedDiscoveryBoundary()
    {
        string expression = SteamUiModuleResolver.CreateExpression("quote\"\\\n");
        Assert.Contains("function createSteamUiModuleResolver", expression);
        Assert.EndsWith($")({SteamCef.JsString("quote\"\\\n")})", expression);
    }
}
