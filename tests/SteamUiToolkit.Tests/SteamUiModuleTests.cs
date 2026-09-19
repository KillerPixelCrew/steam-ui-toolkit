using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiModuleTests
{
    private static readonly SteamUiInjectedAsset Asset = new(
        "(()=>__STEAM_UI_CONFIGURATION_JSON__)()",
        "FIXTUREHASH");

    [Fact]
    public void ModulesFlattenIntoThePatchPublicationAndCommandLookups()
    {
        SteamUiModuleSet set = new(
        [
            new SteamUiModule(
                "first",
                [Patch("p.one"), Patch("p.two")],
                [Publication("p.one")],
                [Command("p.one", "go")]),
            new SteamUiModule(
                "second",
                [Patch("p.three")],
                [Publication("p.three")],
                [Command("p.three", "go"), Command("p.three", "stop")])
        ]);

        Assert.Equal(["p.one", "p.two", "p.three"], set.Patches.Select(patch => patch.Id));
        Assert.Equal(["p.one", "p.three"], set.Publications.Select(publication => publication.PatchId));
        Assert.True(set.TryGetCommand("p.three", "stop", out _));
        Assert.Equal(["go"], set.AllowedCommands["p.one"]);
        Assert.Equal(["go", "stop"], set.AllowedCommands["p.three"]);
    }

    [Fact]
    public void StateOnlyPublicationIsStillAllowedToReachItsSubscriber()
    {
        SteamUiModuleSet set = new(
        [
            new SteamUiModule("state", publications: [Publication("p.state")])
        ]);

        Assert.Empty(set.AllowedCommands["p.state"]);
    }

    [Fact]
    public void AnUnansweredCommandIsNotFound()
    {
        SteamUiModuleSet set = new([new SteamUiModule("only", commands: [Command("p", "go")])]);

        Assert.False(set.TryGetCommand("p", "stop", out _));
        Assert.False(set.TryGetCommand("other", "go", out _));
    }

    [Fact]
    public void TwoModulesSharingAnIdFailAtStartupRatherThanSilently()
    {
        // Startup, not first use: the alternative is a surface that half-exists because whichever
        // declaration won the race is the one that registered.
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SteamUiModuleSet([new SteamUiModule("same"), new SteamUiModule("same")]));

        Assert.Contains("same", error.Message);
    }

    [Fact]
    public void TwoModulesRegisteringOnePatchNameBothInTheFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SteamUiModuleSet(
        [
            new SteamUiModule("first", [Patch("p.shared")]),
            new SteamUiModule("second", [Patch("p.shared")])
        ]));

        // Both names, because "duplicate patch" without saying who is a search through the file.
        Assert.Contains("p.shared", error.Message);
        Assert.Contains("second", error.Message);
    }

    [Fact]
    public void TwoModulesAnsweringOneCommandFailRatherThanOneWinning()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SteamUiModuleSet(
        [
            new SteamUiModule("first", commands: [Command("p", "go")]),
            new SteamUiModule("second", commands: [Command("p", "go")])
        ]));

        Assert.Contains("p/go", error.Message);
    }

    [Fact]
    public void ThePatchAndCommandNamespacesAreIndependent()
    {
        // A module may answer commands against a patch another module installs — the TDP row's
        // gate and the row that writes through it are separate patches sharing one id space.
        SteamUiModuleSet set = new(
        [
            new SteamUiModule("installer", [Patch("p.one")]),
            new SteamUiModule("answerer", commands: [Command("p.one", "go")])
        ]);

        Assert.Single(set.Patches);
        Assert.True(set.TryGetCommand("p.one", "go", out _));
    }

    [Fact]
    public void RefusedCarriesAReasonSoANoOpIsNeverSilent()
    {
        Assert.False(SteamUiCommandResult.Refused.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(SteamUiCommandResult.Refused.Error));
        Assert.True(SteamUiCommandResult.Applied.Succeeded);
    }

    [Fact]
    public async Task FailingPublicationDoesNotPreventIndependentPublication()
    {
        await using var transport = new FakeSteamUiTransport();
        SteamUiModuleSet modules = new(
        [
            new SteamUiModule(
                "fixture",
                publications:
                [
                    new SteamUiStatePublication(
                        "fixture.bad",
                        () => true,
                        () => throw new InvalidOperationException("fixture failure")),
                    new SteamUiStatePublication(
                        "fixture.good",
                        () => true,
                        () => ValueTask.FromResult<JsonElement?>(TestJson.Parse("{\"value\":1}")))
                ])
        ]);
        await using var bridge = new SteamUiBridgeHost(
            transport,
            Asset,
            modules.AllowedCommands);
        Assert.True(await bridge.BootstrapAsync());
        await using var runtime = new SteamUiModuleRuntime(
            bridge,
            modules,
            () => true,
            () => true);

        runtime.QueuePublication();

        await TestJson.WaitUntilAsync(() => Deliveries(transport).Count == 1);
        Assert.Contains("fixture.good", Deliveries(transport)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPublicationDoesNotPreventTheNextPublication()
    {
        await using var transport = new FakeSteamUiTransport();
        Queue<string> acknowledgements = new(["{\"ok\":false}", "{\"ok\":true}"]);
        transport.OnEvaluate = call =>
        {
            var acknowledgement = "{\"ok\":true}";
            lock (acknowledgements)
            {
                if (call.Expression.Contains("deliver", StringComparison.Ordinal)
                    && acknowledgements.Count > 0)
                {
                    acknowledgement = acknowledgements.Dequeue();
                }
            }

            return Task.FromResult(transport.Reply(acknowledgement));
        };
        SteamUiModuleSet modules = new(
        [
            new SteamUiModule(
                "fixture",
                publications:
                [
                    Publication("fixture.first", 1),
                    Publication("fixture.second", 2)
                ])
        ]);
        await using var bridge = new SteamUiBridgeHost(
            transport,
            Asset,
            modules.AllowedCommands);
        Assert.True(await bridge.BootstrapAsync());
        await using var runtime = new SteamUiModuleRuntime(
            bridge,
            modules,
            () => true,
            () => true);

        runtime.QueuePublication();

        // Both go out in one round through Task.WhenAll, so which lands first is not a contract. What
        // is: the refused one does not take the other down with it.
        await TestJson.WaitUntilAsync(() => Deliveries(transport).Count == 2);
        var deliveries = Deliveries(transport);
        Assert.Contains(deliveries, sent => sent.Contains("fixture.first", StringComparison.Ordinal));
        Assert.Contains(deliveries, sent => sent.Contains("fixture.second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnchangedStateIsNotDeliveredAgainButAChangeIs()
    {
        await using var transport = new FakeSteamUiTransport();
        var value = 1;
        SteamUiModuleSet modules = new(
        [
            new SteamUiModule(
                "fixture",
                publications:
                [
                    new SteamUiStatePublication(
                        "fixture.only",
                        () => true,
                        () => ValueTask.FromResult<JsonElement?>(
                            TestJson.Parse($"{{\"value\":{value}}}")))
                ])
        ]);
        await using var bridge = new SteamUiBridgeHost(
            transport,
            Asset,
            modules.AllowedCommands);
        Assert.True(await bridge.BootstrapAsync());
        await using var runtime = new SteamUiModuleRuntime(
            bridge,
            modules,
            () => true,
            () => true);

        runtime.QueuePublication();
        await TestJson.WaitUntilAsync(() => Deliveries(transport).Count == 1);

        // The publication signal is raised by fixed polls, so an unchanged round is the common one.
        runtime.QueuePublication();
        await Task.Delay(100);
        Assert.Single(Deliveries(transport));

        value = 2;
        runtime.QueuePublication();
        await TestJson.WaitUntilAsync(() => Deliveries(transport).Count == 2);
        Assert.NotEqual(Deliveries(transport)[0], Deliveries(transport)[1]);
    }

    private static List<string> Deliveries(FakeSteamUiTransport transport)
    {
        return [.. transport.Expressions.Where(expression => expression.Contains("deliver", StringComparison.Ordinal))];
    }

    private static ISteamUiPatch Patch(string id)
    {
        return new FakePatch(id, id);
    }

    private static SteamUiStatePublication Publication(string patchId)
    {
        return new SteamUiStatePublication(patchId, () => true, () => ValueTask.FromResult<JsonElement?>(null));
    }

    private static SteamUiStatePublication Publication(string patchId, int value)
    {
        return new SteamUiStatePublication(
            patchId,
            () => true,
            () => ValueTask.FromResult<JsonElement?>(TestJson.Parse($"{{\"value\":{value}}}")));
    }

    private static SteamUiCommandHandler Command(string patchId, string command)
    {
        return new SteamUiCommandHandler(patchId, command, (_, _) => Task.FromResult(SteamUiCommandResult.Applied));
    }
}
