using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamPluginFrontendSurfaceTests
{
    [Fact]
    public async Task BackendExceptionsDisableTheOwnerAndRetiredCommandsAreRefused()
    {
        var enabled = true;
        string? failure = null;
        var module = SteamPluginFrontendSurface.Module("plugin.example", "owner", "counter", "", null,
            () => enabled, (_, _, _) => throw new InvalidOperationException("backend failed"),
            (name, reason) =>
            {
                enabled = false;
                failure = name + ": " + reason;
            });
        var command = module.Commands.Single(handler => handler.Command == "invoke");
        var payload = JsonSerializer.SerializeToElement(new { method = "increment", payload = new { value = 4 } });
        SteamUiBridgeRequest request = new(1, "request", module.Id, "invoke", 1, 1, 1, 1, payload);
        var result = await command.Handle(request, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("counter: backend failed", failure);
        Assert.Equal(SteamUiCommandResult.Quarantined, await command.Handle(request, CancellationToken.None));
    }

    [Fact]
    public async Task FrontendOnlyPackagesNeedNoBackendAndFailureReportsNameTheChildModule()
    {
        string? failure = null;
        var module = SteamPluginFrontendSurface.Module("plugin.example", "owner", "frontend", "", null,
            () => true, null, (name, reason) => failure = name + ": " + reason);
        var payload = JsonSerializer.SerializeToElement(new { module = "frontend/page", reason = "render failed" });
        SteamUiBridgeRequest request = new(1, "request", module.Id, "failure", 1, 1, 1, 1, payload);
        await module.Commands.Single(handler => handler.Command == "failure").Handle(request, CancellationToken.None);
        Assert.Equal("frontend/page: render failed", failure);
        Assert.Null(await Assert.Single(module.Publications).Read());
    }
}
