namespace SteamUiToolkit.Tests;

public sealed class SteamScreensaverDismissalTests
{
    [Fact]
    public void ModuleDeclaresOneGatePatchAndNothingElse()
    {
        var module = SteamScreensaverDismissal.Module();

        Assert.Equal(SteamScreensaverDismissal.PatchId, Assert.Single(module.Patches).Id);
        Assert.Empty(module.Commands);
        Assert.Empty(module.Publications);
    }

    [Fact]
    public void ExpressionIsBoundToTheObservedBridgeGenerations()
    {
        var generations = new SteamUiGenerations(1, 1, 1, 1, 1, 1) with { ExecutionContext = 4, Document = 6 };

        var expression = SteamScreensaverDismissal.DismissExpression(generations);

        Assert.Contains("b.gate(\"screensaverDismissal\")", expression, StringComparison.Ordinal);
        Assert.Contains("b.contextGeneration!==4", expression, StringComparison.Ordinal);
        Assert.Contains("b.documentGeneration!==6", expression, StringComparison.Ordinal);
        Assert.Contains("await bridge.dismiss()", expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"dismissed\":true,\"active\":true}", true, true)]
    [InlineData("{\"dismissed\":true,\"active\":false}", true, false)]
    [InlineData("{\"dismissed\":true,\"active\":null}", true, null)]
    [InlineData("{\"dismissed\":false,\"active\":null}", false, null)]
    [InlineData("not json", false, null)]
    [InlineData("{\"active\":true}", false, null)]
    public void AnswerIsRead(string value, bool delivered, bool? wasActive)
    {
        Assert.Equal(new SteamScreensaverDismissalResult(delivered, wasActive), SteamScreensaverDismissal.Read(value));
    }

    [Fact]
    public async Task DismissalInTheSameGenerationIsDelivered()
    {
        var transport = new FakeSteamUiTransport { EvaluationValue = "{\"dismissed\":true,\"active\":true}" };

        var result = await SteamScreensaverDismissal.TryDismissAsync(transport);

        Assert.Equal(new SteamScreensaverDismissalResult(Delivered: true, WasActive: true), result);
        Assert.Single(transport.Expressions);
    }

    [Fact]
    public async Task AnswerFromAReplacedGenerationIsNotTrusted()
    {
        var transport = new FakeSteamUiTransport();
        transport.OnEvaluate = _ =>
        {
            transport.AdvanceGenerationWithoutEvent();
            return Task.FromResult(transport.Reply("{\"dismissed\":true,\"active\":true}"));
        };

        Assert.False((await SteamScreensaverDismissal.TryDismissAsync(transport)).Delivered);
    }

    [Fact]
    public async Task UnreadyTransportIsNotAsked()
    {
        var transport = new FakeSteamUiTransport { Health = SteamUiTransportHealth.Retrying };

        Assert.False((await SteamScreensaverDismissal.TryDismissAsync(transport)).Delivered);
        Assert.Empty(transport.Expressions);
    }

    [Fact]
    public async Task TransportFailureIsNotRetried()
    {
        var transport = new FakeSteamUiTransport { OnEvaluate = _ => throw new InvalidOperationException("closed") };

        Assert.False((await SteamScreensaverDismissal.TryDismissAsync(transport)).Delivered);
        Assert.Single(transport.Expressions);
    }
}
