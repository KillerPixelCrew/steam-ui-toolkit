namespace SteamUiToolkit;

/// <summary>Shared shaping for the text a surface hands to Steam's page.</summary>
public static class SteamUiText
{
    /// <summary>Normalizes an optional detail into renderable text, whole.</summary>
    /// <param name="value">The detail, which may be null or blank.</param>
    /// <returns>The empty string for nothing to say, otherwise the text as given.</returns>
    public static string Of(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }
}
