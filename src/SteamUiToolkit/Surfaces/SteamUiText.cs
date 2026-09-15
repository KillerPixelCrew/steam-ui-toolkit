namespace SteamUiToolkit;

/// <summary>Shared shaping for the text a surface hands to Steam's page.</summary>
public static class SteamUiText
{
    /// <summary>Longest status text a surface state carries.</summary>
    /// <remarks>
    /// Backend and driver messages have no useful display length guarantee. The page has one line,
    /// and the injected validators cut at this bound too, so longer text is truncated before
    /// delivery rather than rejected on arrival.
    /// </remarks>
    public const int MaximumLength = 240;

    /// <summary>Normalizes an optional detail into bounded, renderable text.</summary>
    /// <param name="value">The detail, which may be null, blank, or arbitrarily long.</param>
    /// <returns>The empty string for nothing to say, otherwise the text within the bound.</returns>
    public static string Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= MaximumLength ? value : value[..MaximumLength];
}
