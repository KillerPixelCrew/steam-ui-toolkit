namespace SteamUiToolkit.Tests;

/// <summary>What every gate patch declares: the gate's own probe, and one verify and removal predicate.</summary>
public sealed class SteamGatePatchContractTests
{
    [Fact]
    public async Task EveryGateProbesThroughItsOwnBridgeGateAndSharesOneVerifyAndRemovalPredicate()
    {
        var gates = SteamSurfaceModuleTests.BuildSurfaces(new RecordingBackend())
            .SelectMany(surface => surface.Module.Patches).OfType<SteamGatePatch>()
            .DistinctBy(gate => gate.Id).ToList();
        Assert.NotEmpty(gates);
        foreach (var gate in gates)
        {
            Assert.Contains("bridge.probe()", gate.ProbeExpression, StringComparison.Ordinal);
            Assert.Contains("b.gate(" + SteamCef.JsString(gate.GateName) + ")", gate.ProbeExpression,
                StringComparison.Ordinal);
            await using var transport = new FakeSteamUiTransport();
            var context = new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1));
            await gate.VerifyAsync(context, CancellationToken.None);
            await gate.RemoveAsync(context, CancellationToken.None);
            Assert.Contains(SteamGatePatch.VerifyPredicate, transport.Expressions[0], StringComparison.Ordinal);
            Assert.Contains(SteamGatePatch.RemovePredicate, transport.Expressions[1], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"ok":true,"outcome":"ready","facts":{"homeFound":1}}""", true, true)]
    [InlineData("""{"ok":true,"outcome":"ready","installed":true,"facts":{}}""", true, true)]
    // Not mounted yet, such as Home while Big Picture is closed: absent, probed again.
    [InlineData("""{"ok":false,"outcome":"notReady","reason":"the router is not mounted"}""", false, false)]
    // The bridge or the gate is not there: the same, never a build that moved.
    [InlineData("""{"ok":false,"outcome":"notReady","reason":"bridge unavailable"}""", false, false)]
    [InlineData("""{"ok":false,"outcome":"incompatible","reason":"Home module was not a unique match"}""", true,
        false)]
    // Anything else is incompatible, never an optimistic success.
    [InlineData("""{"ok":true}""", true, false)]
    [InlineData("""{"ok":true,"outcome":"maybe"}""", true, false)]
    [InlineData("""[1]""", true, false)]
    [InlineData("""not json""", true, false)]
    public void TheStandardProbeAnswerReadsAsReadyNotReadyOrIncompatible(
        string answer, bool targetPresent, bool compatible)
    {
        var probe = SteamUiPatchEvaluation.ParseGateProbe(answer, "fingerprint-v1");

        Assert.Equal(targetPresent, probe.TargetPresent);
        Assert.Equal(compatible, probe.Compatible);
        Assert.Equal(compatible ? "fingerprint-v1" : null, probe.Fingerprint);
        if (!compatible)
        {
            Assert.False(string.IsNullOrEmpty(probe.Diagnostic));
        }
    }

    [Fact]
    public void ARefusalKeepsTheWholeAnswerWithItsReasonAndFacts()
    {
        const string answer =
            """{"ok":false,"outcome":"notReady","reason":"the router is not mounted","facts":{"homeRoutes":0,"visited":12}}""";

        var probe = SteamUiPatchEvaluation.ParseGateProbe(answer, "fingerprint-v1");

        Assert.Contains("the router is not mounted", probe.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("\"homeRoutes\":0", probe.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeTheTargetCouldNotAnswerIsAbsentAndOneThatThrewIsIncompatible()
    {
        var gate = new SteamGatePatch("example.gate", "exampleGate", "example-v1", "Example gate");
        await using var unanswered = new FakeSteamUiTransport();
        unanswered.OnEvaluate = _ => Task.FromResult(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.NotSent, "no target"));
        await using var threw = new FakeSteamUiTransport();
        threw.OnEvaluate = _ => Task.FromResult(FakeSteamUiTransport.Answer(null, "ReferenceError: oops"));

        var absent = await gate.ProbeAsync(new SteamUiPatchContext(unanswered, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        var broken = await gate.ProbeAsync(new SteamUiPatchContext(threw, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.False(absent.TargetPresent);
        Assert.True(broken.TargetPresent);
        Assert.False(broken.Compatible);
        Assert.Equal("ReferenceError: oops", broken.Diagnostic);
    }

    [Theory]
    [InlineData("", "gate", "v1", "Subject")]
    [InlineData("id", " ", "v1", "Subject")]
    [InlineData("id", "gate", "", "Subject")]
    [InlineData("id", "gate", "v1", "")]
    public void EveryIdentityIsRequired(string id, string gateName, string fingerprint, string subject)
    {
        Assert.Throws<ArgumentException>(() => new SteamGatePatch(id, gateName, fingerprint, subject));
    }
}
