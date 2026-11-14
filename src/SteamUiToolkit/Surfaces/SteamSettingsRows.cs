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

    /// <summary>
    ///     A colour, reading <see cref="SteamSettingsRow.Text" /> as a CSS colour and shown as its swatch
    ///     and text, edited in a modal of Steam's sliders and sent back as <c>hsla()</c>. On a client
    ///     without a modal it is a text field.
    /// </summary>
    public const string Color = "color";

    /// <summary>A fixed-breakpoint curve edited with native sliders and an explicit Apply button.</summary>
    public const string Curve = "curve";

    /// <summary>A native selectable resolution list with custom-size and percentage-step actions.</summary>
    public const string ResolutionList = "resolution-list";

    /// <summary>
    ///     A card naming what the page's profile-aware rows edit: <see cref="SteamSettingsRow.Label" /> is the
    ///     small heading over it, <see cref="SteamSettingsRow.Text" /> the scope and
    ///     <see cref="SteamSettingsRow.Description" /> the line under it. It takes no focus and sends nothing.
    /// </summary>
    public const string Scope = "scope";

    /// <summary>
    ///     One labelled row holding compact dropdowns side by side, one per <see cref="SteamSettingsRow.Columns" />
    ///     entry: a <see cref="Choice" /> row with its own key, labelled above its dropdown by its own label. The
    ///     row itself sends nothing; each entry sends under its own key.
    /// </summary>
    public const string Columns = "columns";
}

/// <summary>Editing capabilities for a resolution list; entries and labels use the row's choices.</summary>
/// <param name="Removable">Choice values the host permits removing.</param>
/// <param name="NativeWidth">Native pixel width for 5% steps, or null when unavailable.</param>
/// <param name="NativeHeight">Native pixel height for 5% steps, or null when unavailable.</param>
/// <param name="RestoreAvailable">Whether the host has an original driver list to restore.</param>
public sealed record SteamSettingsResolutionEditor(IReadOnlyList<string> Removable, int? NativeWidth, int? NativeHeight,
    bool RestoreAvailable = false);

/// <summary>One host-declared curve breakpoint.</summary>
/// <param name="Input">Fixed input, such as temperature in degrees Celsius.</param>
/// <param name="Output">Editable output within the row's published bounds.</param>
public sealed record SteamSettingsCurvePoint(int Input, int Output);

/// <summary>A host-sampled preset over the curve's existing breakpoints.</summary>
/// <param name="Label">The preset button's label.</param>
/// <param name="Points">The points staged by the button; applying still requires Apply.</param>
public sealed record SteamSettingsCurvePreset(string Label, IReadOnlyList<SteamSettingsCurvePoint> Points);

/// <summary>One choice of a <see cref="SteamSettingsRowKind.Choice" /> or <see cref="SteamSettingsRowKind.Order" /> row.</summary>
/// <param name="Value">What the host receives when it is picked.</param>
/// <param name="Label">What the user sees.</param>
/// <param name="Selectable">
///     Whether the user can pick it. An unselectable choice names a state the user cannot choose, such
///     as values that match none of the presets: it is listed only while it is the row's current value
///     and is never sent.
/// </param>
public sealed record SteamSettingsChoice(string Value, string Label, bool Selectable = true);

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

/// <summary>A line of text the user is asked for before an action is sent, in Steam's modal.</summary>
/// <param name="Title">The modal's title.</param>
/// <param name="Label">The text field's label.</param>
/// <param name="ConfirmLabel">The sending button's label.</param>
/// <param name="Text">A sentence above the field, or null.</param>
/// <param name="Initial">What the field starts with, such as a suggested name, or null for empty.</param>
/// <remarks>
///     Only a trimmed, non-empty answer is sent, as the action's string value; Cancel and B send nothing.
///     On a client without a modal, the action sends <paramref name="Initial" /> when it has one.
/// </remarks>
public sealed record SteamSettingsPrompt(
    string Title,
    string Label,
    string ConfirmLabel,
    string? Text = null,
    string? Initial = null);

/// <summary>
///     One of an action row's buttons. It sends under its own <paramref name="Key" />, so one row can name an item
///     and carry the item's contextual commands, such as Rename and Delete.
/// </summary>
/// <param name="Key">Stable identity the button sends under, distinct from every row key.</param>
/// <param name="Label">The button's label.</param>
/// <param name="Prompt">A line of text to ask for first; the answer is sent as a string instead of <c>true</c>.</param>
/// <param name="Confirm">A question to ask first; its <c>When</c> is ignored, since a button has one value.</param>
/// <param name="Disabled">Whether the button is shown but cannot be pressed.</param>
public sealed record SteamSettingsAction(
    string Key,
    string Label,
    SteamSettingsPrompt? Prompt = null,
    SteamSettingsConfirmation? Confirm = null,
    bool Disabled = false);

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
/// <param name="Labels">
///     A range row's notch labels, one per step from <paramref name="Minimum" />. With them the row
///     is one of the labels by index: the slider names each notch, the value is the index, and the
///     bounds are the labels' count.
/// </param>
/// <param name="MaximumLength">The longest text or secret the field accepts.</param>
/// <param name="Disabled">Whether the field is shown but cannot be changed.</param>
/// <param name="Confirm">A confirmation to ask first, or null.</param>
/// <param name="ButtonLabel">An action row's button label.</param>
/// <param name="Accent">
///     Whether the row is marked: its <paramref name="Description" />, which the host writes, is drawn
///     in Steam's accent colour.
/// </param>
/// <param name="ColorAlpha">Whether the color editor offers opacity; false keeps the value opaque.</param>
/// <param name="Curve">Fixed curve inputs and current output values.</param>
/// <param name="CurvePresets">Host-sampled presets using the same fixed inputs.</param>
/// <param name="ResolutionEditor">Resolution-list editing capabilities.</param>
/// <param name="Icon">
///     The name of the toolkit glyph drawn beside the field's label in Quick Access, such as
///     <c>power</c>, or null for none. A name the toolkit does not draw leaves the row without one.
/// </param>
/// <param name="LabelToken">
///     A Steam localization token naming the same thing as <paramref name="Label" />. Where the client
///     localizes it, its text replaces the label; otherwise the label is shown.
/// </param>
/// <param name="Summary">
///     What the row says on its Quick Access section's heading while the section is folded, such as
///     the selected choice, or null for nothing. A section joins its rows' summaries in order.
/// </param>
/// <param name="Actions">
///     An action row's buttons, in order, each sending under its own key; null for the one button labelled
///     <paramref name="ButtonLabel" /> that sends under the row's key. A refusal of any of them is shown on
///     the row.
/// </param>
/// <param name="Columns">A columns row's entries, each a choice row with its own key, in order.</param>
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
    string? ButtonLabel = null,
    IReadOnlyList<string>? Labels = null,
    bool Accent = false,
    bool ColorAlpha = true,
    IReadOnlyList<SteamSettingsCurvePoint>? Curve = null,
    IReadOnlyList<SteamSettingsCurvePreset>? CurvePresets = null,
    SteamSettingsResolutionEditor? ResolutionEditor = null,
    string? Icon = null,
    string? LabelToken = null,
    string? Summary = null,
    IReadOnlyList<SteamSettingsAction>? Actions = null,
    IReadOnlyList<SteamSettingsRow>? Columns = null);

/// <summary>A titled group of rows: one of Steam's settings sections.</summary>
/// <param name="Title">The section heading, or null for an untitled section.</param>
/// <param name="Rows">Its rows, in order.</param>
/// <param name="Id">Optional stable group identity for Quick Access expansion state.</param>
/// <param name="Level">
///     The subpage the section belongs to, or null to draw it on the page itself. Every section naming
///     the same level id forms one subpage, opened from one row drawn where the first of them would be.
/// </param>
public sealed record SteamSettingsSection(
    string? Title,
    IReadOnlyList<SteamSettingsRow> Rows,
    string? Id = null,
    SteamSettingsLevel? Level = null);

/// <summary>
///     A subpage of a settings page: the page shows one row naming it, and opening that row shows the
///     subpage's sections in place of the page until Back.
/// </summary>
/// <param name="Id">The subpage's identity, unique on its page and stable across publications.</param>
/// <param name="Title">The row's title and the subpage's heading.</param>
/// <param name="Summary">What the row says under its title, such as the subpage's current values, or null.</param>
/// <param name="Icon">The toolkit glyph drawn on the row, such as <c>display</c>, or null for none.</param>
/// <param name="Badge">A short status tag on the row, such as <c>2 set for this game</c>, or null.</param>
/// <param name="Group">
///     The heading over the rows of adjacent subpages, which share one section; the first row's group
///     names it, and null leaves it untitled.
/// </param>
public sealed record SteamSettingsLevel(
    string Id,
    string Title,
    string? Summary = null,
    string? Icon = null,
    string? Badge = null,
    string? Group = null);

/// <summary>One page of the settings sidebar.</summary>
/// <param name="Id">The page's path segment below the settings route; stable and URL-safe.</param>
/// <param name="Title">The sidebar entry and the page heading.</param>
/// <param name="Sections">The page's sections.</param>
/// <param name="Glyph">
///     The sidebar icon, also drawn beside the page's Quick Access heading, as SVG path data on a 24x24
///     grid, or null.
/// </param>
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
