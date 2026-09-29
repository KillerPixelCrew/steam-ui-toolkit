using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>Readers for the exact wire shapes the injected surfaces send.</summary>
/// <remarks>
///     Exact rather than lenient: the page is this library's own script, so anything else arriving
///     here is either a defect or something that is not the injected script, and neither should reach
///     a backend. Every surface's command handler reads its payload through these; a consumer that
///     answers commands of its own is welcome to the same discipline.
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
               && payload.TryGetProperty(propertyName, out var property)
               && property.ValueKind is JsonValueKind.Number
               && property.TryGetInt32(out value)
               && value >= minimum
               && value <= maximum;
    }

    /// <summary>Reads one required boolean property, without an object-arity rule.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="value">The flag, when this returns true; otherwise false.</param>
    /// <returns>Whether the payload is an object whose property is literally true or false.</returns>
    public static bool TryReadBoolean(JsonElement payload, string propertyName, out bool value)
    {
        value = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.ValueKind is JsonValueKind.True;
        return true;
    }

    /// <summary>Reads a payload that is exactly one boolean named <c>enabled</c>.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="enabled">The flag, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    public static bool TryReadEnabled(JsonElement payload, out bool enabled)
    {
        if (!TryReadBoolean(payload, "enabled", out enabled) || !HasExactly(payload, 1))
        {
            enabled = false;
            return false;
        }

        return true;
    }

    /// <summary>Reads a payload that is exactly one bounded identifier named <c>target</c>.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="target">The identifier, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    /// <remarks>
    ///     Identifiers are 1–64 characters of ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.
    ///     Uppercase is allowed because ids a host sends are often PascalCase; a lowercase-only rule
    ///     once rejected every valid controller target while the row rendered normally.
    /// </remarks>
    public static bool TryReadTarget(JsonElement payload, out string target)
    {
        target = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("target", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (!HasExactly(payload, 1)
            || candidate is not { Length: >= 1 and <= 64 }
            || !ValidTargetId(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    /// <summary>Reads one required non-blank string property.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="value">The string, when this returns true.</param>
    /// <returns>Whether the property is present, a string and non-blank.</returns>
    /// <remarks>No length limit: the value is whatever the page sent, and the caller decides what it means.</remarks>
    public static bool TryReadNonBlankString(
        JsonElement payload,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>Reads one required string property that may be empty.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="value">The string, empty included, when this returns true.</param>
    /// <returns>Whether the property is present and a string.</returns>
    /// <remarks>
    ///     For a value whose empty spelling means something, such as "no filter" or "back to the
    ///     default", which <see cref="TryReadNonBlankString" /> would refuse.
    /// </remarks>
    public static bool TryReadString(
        JsonElement payload,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>Reads one required string property that may be null.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="value">The string, or null for a JSON null, when this returns true.</param>
    /// <returns>Whether the property is present and either null or a non-blank string.</returns>
    /// <remarks>For a selection that can also be cleared, where null is the clearing spelling.</remarks>
    public static bool TryReadNullableString(
        JsonElement payload,
        string propertyName,
        out string? value)
    {
        value = null;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.Null
               || TryReadNonBlankString(payload, propertyName, out value!);
    }

    /// <summary>Reads one required array of non-blank strings.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <param name="values">The strings, in order, when this returns true.</param>
    /// <returns>Whether the property is an array of that shape; an empty array is accepted.</returns>
    public static bool TryReadStrings(
        JsonElement payload,
        string propertyName,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> read = [];
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } text
                || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            read.Add(text);
        }

        values = read;
        return true;
    }

    /// <summary>Reads a payload that is one non-blank string property and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The one property.</param>
    /// <param name="value">The string, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    public static bool TryReadOnlyString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;
        return HasExactly(payload, 1) && TryReadNonBlankString(payload, propertyName, out value);
    }

    /// <summary>Reads a payload that is one string property, which may be empty, and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The one property.</param>
    /// <param name="value">The string, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    public static bool TryReadOnlyOptionalString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;
        return HasExactly(payload, 1) && TryReadString(payload, propertyName, out value);
    }

    /// <summary>Reads a payload that is one string property naming one of a fixed set, and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The one property.</param>
    /// <param name="allowed">The values accepted.</param>
    /// <param name="value">The value, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape and names an allowed value.</returns>
    public static bool TryReadOnlyChoice(
        JsonElement payload, string propertyName, IReadOnlyCollection<string> allowed, out string value)
    {
        return TryReadOnlyString(payload, propertyName, out value) && allowed.Contains(value);
    }

    /// <summary>Reads a payload that is one boolean property and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="propertyName">The one property.</param>
    /// <param name="value">The value, when this returns true.</param>
    /// <returns>Whether the payload is exactly that shape.</returns>
    public static bool TryReadOnlyBoolean(JsonElement payload, string propertyName, out bool value)
    {
        value = false;
        return HasExactly(payload, 1) && TryReadBoolean(payload, propertyName, out value);
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

        var count = 0;
        foreach (var ignored in payload.EnumerateObject())
        {
            count++;
        }

        return count == propertyCount;
    }

    private static bool ValidTargetId(string target)
    {
        foreach (var character in target)
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
