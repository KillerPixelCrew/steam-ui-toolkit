namespace SteamUiToolkit;

/// <summary>Shared JavaScript fragments for read-only webpack structural probes.</summary>
/// <remarks>
/// A probe names the modules it touches. The preamble captures webpack's require by pushing an
/// empty chunk, which evaluates nothing; a probe built on it then reads factory source as text and
/// resolves only literal module ids. Iterating the registry and constructing exports is the one
/// thing a probe must never do — it has restarted a machine and signed Steam out.
/// </remarks>
public static class SteamUiProbeJs
{
    /// <summary>Opens a probe IIFE and captures webpack's require via a throwaway chunk push.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    /// <returns>The opening of a probe expression; the caller closes it.</returns>
    public static string Preamble(string chunkLabel) => $$"""
        (()=>{try{
          const req={{SteamUiModuleResolver.CreateExpression(chunkLabel)}};
        """;

    /// <summary>The preamble plus the factory-source token counter structural probes share.</summary>
    /// <param name="chunkLabel">The stable chunk-label prefix, kept per probe for live diagnostics.</param>
    /// <returns>The opening of a probe expression that defines <c>count(tokens)</c>.</returns>
    public static string CountingPreamble(string chunkLabel) => $$"""
        {{Preamble(chunkLabel)}}
          const count=req.count;
        """;
}
