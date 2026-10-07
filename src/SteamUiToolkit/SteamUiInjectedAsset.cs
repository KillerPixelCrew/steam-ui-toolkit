namespace SteamUiToolkit;

/// <summary>The script a host injects, and the hash that identifies this exact copy of it.</summary>
/// <param name="Source">
///     The JavaScript to evaluate, containing the configuration placeholder the
///     bridge substitutes.
/// </param>
/// <param name="Sha256">The hash of <paramref name="Source" />, uppercase hex.</param>
/// <remarks>
///     The host supplies both values. The hash distinguishes an updated asset from one left in the
///     same document by a previous host build, even when document/context generations are unchanged.
/// </remarks>
public readonly record struct SteamUiInjectedAsset(string Source, string Sha256);
