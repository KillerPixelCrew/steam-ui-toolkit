using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>Readers for the exact wire shapes the injected surfaces send.</summary>
/// <remarks>
/// Exact rather than lenient: the page is this library's own script, so anything else arriving
/// here is either a defect or something that is not the injected script, and neither should reach
/// a backend. Every surface's command handler reads its payload through these; a consumer that
/// answers commands of its own is welcome to the same discipline.
/// </remarks>
public static class SteamUiPayload
{
    /// <summary>Reads one required integer within a range, without an object-arity rule.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="minimum">Lowest accepted value, inclusive.</param>
    /// <param name="maximum">Highest accepted value, inclusive.</param>
    /// <param name="value">The value, when this returns true.</param>
    /// <returns>Whether the property is present, numeric and in range.</returns>
    public static bool TryReadInt(
        JsonElement payload,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = default;
        return payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind is JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= minimum
            && value <= maximum;
    }

    /// <summary>Reads a payload that is exactly one boolean named <c>enabled</c>.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="enabled">The flag, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    public static bool TryReadEnabled(JsonElement payload, out bool enabled)
    {
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("enabled", out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !HasExactly(payload, 1))
        {
            return false;
        }

        enabled = property.GetBoolean();
        return true;
    }

    /// <summary>Reads a payload that is exactly one bounded identifier named <c>target</c>.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="target">The identifier, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    /// <remarks>
    /// Identifiers are 1–64 characters of ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.
    /// Uppercase is allowed because ids a host sends are often PascalCase; a lowercase-only rule
    /// once rejected every valid controller target while the row rendered normally.
    /// </remarks>
    public static bool TryReadTarget(JsonElement payload, out string target)
    {
        target = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("target", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (!HasExactly(payload, 1)
            || candidate is not { Length: >= 1 and <= 64 }
            || !ValidTargetId(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    /// <summary>Reads one required non-blank string property within a length bound.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="maximumLength">Longest accepted string.</param>
    /// <param name="value">The string, when this returns true.</param>
    /// <returns>Whether the property is present, a string, non-blank and within the bound.</returns>
    public static bool TryReadBoundedString(
        JsonElement payload,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>Whether the payload object carries exactly this many properties.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyCount">The exact property count required.</param>
    /// <returns>True for an object with exactly that many properties.</returns>
    public static bool HasExactly(JsonElement payload, int propertyCount)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int count = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            count++;
        }

        return count == propertyCount;
    }

    private static bool ValidTargetId(string target)
    {
        foreach (char character in target)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

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
