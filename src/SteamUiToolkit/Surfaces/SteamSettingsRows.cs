using System.Collections.Generic;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>The row kinds the settings renderer draws, each with one of Steam's own fields.</summary>
public static class SteamSettingsRowKind
{
    /// <summary>A toggle field, reading <see cref="SteamSettingsRow.Checked" />.</summary>
    public const string Boolean = "boolean";

    /// <summary>A dropdown field over <see cref="SteamSettingsRow.Choices" />, reading <see cref="SteamSettingsRow.Text" />.</summary>
    public const string Choice = "choice";

    /// <summary>A slider field, reading <see cref="SteamSettingsRow.Number" /> within its bounds.</summary>
    public const string Range = "range";

    /// <summary>A text field, reading <see cref="SteamSettingsRow.Text" />.</summary>
    public const string Text = "text";

    /// <summary>
    ///     A password field whose value is never published. <see cref="SteamSettingsRow.Text" /> is its
    ///     placeholder, such as "Set" or "Not set".
    /// </summary>
    public const string Secret = "secret";

    /// <summary>An ordered list of <see cref="SteamSettingsRow.Order" />, moved with small buttons.</summary>
    public const string Order = "order";

    /// <summary>A button, labelled <see cref="SteamSettingsRow.ButtonLabel" />, that asks the host to act.</summary>
    public const string Action = "action";

    /// <summary>A read-only value field showing <see cref="SteamSettingsRow.Text" />.</summary>
    public const string Note = "note";
}

/// <summary>One choice of a <see cref="SteamSettingsRowKind.Choice" /> or <see cref="SteamSettingsRowKind.Order" /> row.</summary>
/// <param name="Value">What the host receives when it is picked.</param>
/// <param name="Label">What the user sees.</param>
public sealed record SteamSettingsChoice(string Value, string Label);

/// <summary>A change the user is asked about first, in Steam's own confirm modal.</summary>
/// <param name="When">The boolean value that needs confirming; the other one is sent straight away.</param>
/// <param name="Title">The modal's title.</param>
/// <param name="Description">What happens, in a sentence or two.</param>
/// <param name="ConfirmLabel">The confirming button's label.</param>
/// <param name="Destructive">Whether Steam styles the confirmation as destructive.</param>
public sealed record SteamSettingsConfirmation(
    bool When,
    string Title,
    string Description,
    string ConfirmLabel,
    bool Destructive = true);

/// <summary>One setting, described by kind rather than by component.</summary>
/// <param name="Key">Stable identity, sent back with every change.</param>
/// <param name="Kind">One of <see cref="SteamSettingsRowKind" />.</param>
/// <param name="Label">The field's label.</param>
/// <param name="Description">The line Steam shows under the label, or null.</param>
/// <param name="Checked">A boolean row's value.</param>
/// <param name="Text">A choice, text or note row's value, or a secret row's placeholder.</param>
/// <param name="Number">A range row's value.</param>
/// <param name="Order">An order row's values, in order.</param>
/// <param name="Choices">A choice row's options, or the labels of an order row's values.</param>
/// <param name="Minimum">A range row's lowest value.</param>
/// <param name="Maximum">A range row's highest value.</param>
/// <param name="Step">A range row's step.</param>
/// <param name="Suffix">What follows a range row's value, such as a unit.</param>
/// <param name="MaximumLength">The longest text or secret the field accepts.</param>
/// <param name="Disabled">Whether the field is shown but cannot be changed.</param>
/// <param name="Confirm">A confirmation to ask first, or null.</param>
/// <param name="ButtonLabel">An action row's button label.</param>
public sealed record SteamSettingsRow(
    string Key,
    string Kind,
    string Label,
    string? Description = null,
    bool? Checked = null,
    string? Text = null,
    double? Number = null,
    IReadOnlyList<string>? Order = null,
    IReadOnlyList<SteamSettingsChoice>? Choices = null,
    double? Minimum = null,
    double? Maximum = null,
    double? Step = null,
    string? Suffix = null,
    int? MaximumLength = null,
    bool Disabled = false,
    SteamSettingsConfirmation? Confirm = null,
    string? ButtonLabel = null);

/// <summary>A titled group of rows: one of Steam's settings sections.</summary>
/// <param name="Title">The section heading, or null for an untitled section.</param>
/// <param name="Rows">Its rows, in order.</param>
public sealed record SteamSettingsSection(string? Title, IReadOnlyList<SteamSettingsRow> Rows);

/// <summary>One page of the settings sidebar.</summary>
/// <param name="Id">The page's path segment below the settings route; stable and URL-safe.</param>
/// <param name="Title">The sidebar entry and the page heading.</param>
/// <param name="Sections">The page's sections.</param>
/// <param name="Glyph">The sidebar icon as SVG path data on a 24x24 grid, or null.</param>
public sealed record SteamSettingsPage(
    string Id,
    string Title,
    IReadOnlyList<SteamSettingsSection> Sections,
    string? Glyph = null);

/// <summary>Serializes settings pages exactly as the renderer reads them.</summary>
/// <remarks>
///     The renderer in <c>settings.ts</c> draws these with Steam's own routed sidebar, settings sections
///     and fields, so a host page publishing them looks and navigates like Steam's Settings. A host
///     embedding them in its own state serializes them with camelCase names, as this does.
/// </remarks>
public static class SteamSettingsRows
{
    /// <summary>Serializes the pages as the renderer reads them.</summary>
    /// <param name="pages">The pages.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(IReadOnlyList<SteamSettingsPage> pages)
    {
        return JsonSerializer.SerializeToElement(
            pages, SteamSurfaceJsonContext.Default.IReadOnlyListSteamSettingsPage);
    }
}
