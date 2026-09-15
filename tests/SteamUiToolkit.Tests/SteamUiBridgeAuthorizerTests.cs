using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// Host-side authorization of bridge requests, including the wire contract with the injected
/// bootstrap.
/// </summary>
/// <remarks>
/// The bootstrap serialises its envelope in camelCase. The host's source-generated context matched
/// PascalCase with case-insensitivity explicitly disabled, so every property took its default:
/// Version arrived as 0 and the request was refused as a schema version mismatch with an empty
/// patch id. Every native-QAM command had been rejected since the bridge was written, and it stayed
/// invisible because no row rendered to send one until the panel was fixed.
/// <para>
/// The captured envelope below is a real one from the live Runtime binding on the reference Claw,
/// so the wire tests fail if either side of the contract moves.
/// </para>
/// </remarks>
public sealed class SteamUiBridgeAuthorizerTests
{
    private static readonly SteamUiGenerations Generations = new(1, 2, 3, 4, 5, 6);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Commands =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["steam-ui.power-limit"] = ["setPrimaryLimit"],
            ["steam-ui.frame-limit"] = ["setFrameLimit"],
        };

    /// <summary>The generations the captured envelope was produced under.</summary>
    /// <remarks>
    /// Only the execution-context and document generations take part in authorization; the rest
    /// identify the transport and are irrelevant to the envelope's validity.
    /// </remarks>
    private static readonly SteamUiGenerations CapturedGenerations = new(0, 0, 0, 0, 2, 1);

    private const string CapturedEnvelope = """
        {"version":1,"type":"request","patchId":"steam-ui.frame-limit",
        "command":"setFrameLimit","sequence":6,"actionGeneration":99,
        "contextGeneration":2,"documentGeneration":1,"payload":{"value":60}}
        """;

    [Fact]
    public void TheBootstrapsCamelCaseEnvelopeDecodesIntoEveryField()
    {
        SteamUiBridgeRequest? request = JsonSerializer.Deserialize(
            CapturedEnvelope,
            SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest);

        Assert.NotNull(request);
        Assert.Equal(SteamUiBridgeHost.SchemaVersion, request.Version);
        Assert.Equal("request", request.Type);
        Assert.Equal("steam-ui.frame-limit", request.PatchId);
        Assert.Equal("setFrameLimit", request.Command);
        Assert.Equal(6, request.Sequence);
        Assert.Equal(99, request.ActionGeneration);
        Assert.Equal(2, request.ContextGeneration);
        Assert.Equal(1, request.DocumentGeneration);
        Assert.Equal(60, request.Payload.GetProperty("value").GetInt32());
    }

    [Fact]
    public void ADecodedEnvelopeIsAuthorizedRatherThanRefusedAsAVersionMismatch()
    {
        SteamUiBridgeRequest request = JsonSerializer.Deserialize(
            CapturedEnvelope,
            SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest)!;
        SteamUiBridgeAuthorizer authorizer = new(CapturedGenerations, Commands);

        SteamUiBridgeAuthorizationResult result = authorizer.Authorize(request);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void AcceptsOnlyCurrentAllowlistedCommandOnce()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations, Commands);
        var request = Request("steam-ui.power-limit", "setPrimaryLimit", 1, 10);

        Assert.True(authorizer.Authorize(request).Accepted);
        Assert.False(authorizer.Authorize(request).Accepted);
        Assert.False(authorizer.Authorize(
            Request("steam-ui.power-limit", "readRawWmi", 2, 11)).Accepted);
    }

    [Fact]
    public void RejectsStaleGenerationAndActionReplay()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations, Commands);
        Assert.True(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 1, 20)).Accepted);
        Assert.False(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 2, 20)).Accepted);
        Assert.False(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 3, 21) with
            {
                ContextGeneration = 99,
            }).Accepted);
    }

    [Fact]
    public void CancellationMustReferenceAcceptedSequence()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations, Commands);
        Assert.False(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 5, 30) with
            {
                Type = "cancel",
            }).Accepted);
        Assert.True(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 5, 30)).Accepted);
        Assert.True(authorizer.Authorize(
            Request("steam-ui.frame-limit", "setFrameLimit", 5, 30) with
            {
                Type = "cancel",
            }).Accepted);
    }

    private static SteamUiBridgeRequest Request(
        string patchId, string command, long sequence, long actionGeneration) =>
        new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            patchId,
            command,
            sequence,
            actionGeneration,
            Generations.ExecutionContext,
            Generations.Document,
            TestJson.Parse("{\"value\":15}"));
}
