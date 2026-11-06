namespace SteamUiToolkit.Tests;

public sealed class SteamNoticesTests
{
    private const string Route = "/settings/display";

    [Fact]
    public void ModuleDeclaresOneGatePatchAndNothingElse()
    {
        var module = SteamNotices.Module();

        Assert.Equal("notices", module.Id);
        Assert.Equal(SteamNotices.PatchId, Assert.Single(module.Patches).Id);
        Assert.Equal("steam-ui.notices", SteamNotices.PatchId);
        Assert.Empty(module.Commands);
        Assert.Empty(module.Publications);
    }

    [Fact]
    public void ShowExpressionIsBoundToTheObservedBridgeGenerations()
    {
        var generations = new SteamUiGenerations(1, 1, 1, 1, 1, 1) with { ExecutionContext = 7, Document = 9 };

        var expression = SteamNotices.ShowExpression(generations, "id", "Title", "Body", Route);

        Assert.Contains("b.gate(\"notices\")", expression, StringComparison.Ordinal);
        Assert.Contains("b.contextGeneration!==7", expression, StringComparison.Ordinal);
        Assert.Contains("b.documentGeneration!==9", expression, StringComparison.Ordinal);
        Assert.Contains("bridge.show(", expression, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptedNoticeInTheSameGenerationIsShown()
    {
        var transport = new FakeSteamUiTransport { EvaluationValue = "true" };

        Assert.True(await SteamNotices.TryShowAsync(transport, "id", "Title", "Body", Route));
        Assert.Single(transport.Expressions);
    }

    [Fact]
    public async Task RefusedNoticeAsksTheHostForItsOwn()
    {
        var transport = new FakeSteamUiTransport { EvaluationValue = "false" };

        Assert.False(await SteamNotices.TryShowAsync(transport, "id", "Title", "Body", Route));
    }

    [Fact]
    public async Task AnswerFromAReplacedGenerationIsNotTrusted()
    {
        var transport = new FakeSteamUiTransport();
        transport.OnEvaluate = _ =>
        {
            transport.AdvanceGenerationWithoutEvent();
            return Task.FromResult(transport.Reply("true"));
        };

        Assert.False(await SteamNotices.TryShowAsync(transport, "id", "Title", "Body", Route));
    }

    [Fact]
    public async Task UnreadyTransportIsNotAsked()
    {
        var transport = new FakeSteamUiTransport { Health = SteamUiTransportHealth.Retrying, EvaluationValue = "true" };

        Assert.False(await SteamNotices.TryShowAsync(transport, "id", "Title", "Body", Route));
        Assert.Empty(transport.Expressions);
    }

    [Theory]
    [InlineData("", "Title", "Body", Route)]
    [InlineData("id", "Title", "Body", "https://example.com")]
    [InlineData("id", "Title", "Body", "/")]
    public async Task InvalidNoticeIsNotSent(string id, string title, string body, string route)
    {
        var transport = new FakeSteamUiTransport { EvaluationValue = "true" };

        Assert.False(await SteamNotices.TryShowAsync(transport, id, title, body, route));
        Assert.Empty(transport.Expressions);
    }

    [Fact]
    public async Task OverlongTextIsNotSent()
    {
        var transport = new FakeSteamUiTransport { EvaluationValue = "true" };
        var title = new string(c: 't', count: SteamNotices.MaximumTitleLength + 1);
        var body = new string(c: 'b', count: SteamNotices.MaximumBodyLength + 1);

        Assert.False(await SteamNotices.TryShowAsync(transport, "id", title, "Body", Route));
        Assert.False(await SteamNotices.TryShowAsync(transport, "id", "Title", body, Route));
        Assert.Empty(transport.Expressions);
    }

    [Fact]
    public async Task TransportFailureFallsBackWithoutRetrying()
    {
        var transport = new FakeSteamUiTransport
        {
            OnEvaluate = _ => throw new InvalidOperationException("closed")
        };

        Assert.False(await SteamNotices.TryShowAsync(transport, "id", "Title", "Body", Route));
        Assert.Single(transport.Expressions);
    }
}
