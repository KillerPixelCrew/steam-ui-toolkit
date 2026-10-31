namespace SteamUiToolkit;

/// <summary>Shared JavaScript fragments for the read-only webpack probes of patches that are not gates.</summary>
/// <remarks>
///     A gate's probe is the gate's own <c>probe()</c>, so a gate never needs anything here. What stays is
///     what the bridge's own probe and the native surface commands still evaluate standalone. The preamble
///     captures webpack's require by pushing an empty chunk, which evaluates nothing; a probe built on it
///     then reads factory source as text and resolves each module by the source tokens its authors
///     typed, never by a module id. Constructing exports is the one thing a probe must never do: it has
///     restarted a machine and signed Steam out.
/// </remarks>
public static class SteamUiProbeJs
{
    /// <summary>Closes a probe opened by <see cref="Preamble" />.</summary>
    /// <remarks>
    ///     A probe that throws answers with the error as its result, so the manager records
    ///     why the client is incompatible instead of a bare JavaScript exception.
    /// </remarks>
    public const string Close = "}catch(error){return JSON.stringify({error:String(error)}); } })()";

    /// <summary>Structural source tokens used to prove the React export is a unique match.</summary>
    public const string ReactTokens =
        "['react.transitional.element','useState','cloneElement','createElement']";

    /// <summary>The source tokens of the module holding Steam's route table.</summary>
    internal const string RouteTableTokens = "['GameAPIOSK:','/gameapiosk']";

    /// <summary>Opens a probe IIFE, captures webpack's require and defines the token counter.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    /// <returns>
    ///     The opening of a probe expression that defines <c>req</c> and <c>count(tokens)</c>;
    ///     the caller closes it with <see cref="Close" />.
    /// </returns>
    public static string Preamble(string chunkLabel)
    {
        return $$"""
                 (()=>{try{
                   const req={{SteamUiModuleResolver.CreateExpression(chunkLabel)}};
                   const count=req.count;
                 """;
    }
}
