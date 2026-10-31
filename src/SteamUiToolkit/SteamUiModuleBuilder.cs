using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Reads and validates one plugin-owned command payload.</summary>
/// <typeparam name="T">The validated value passed to the command implementation.</typeparam>
/// <param name="payload">The bridge payload.</param>
/// <param name="value">The parsed value when validation succeeds.</param>
/// <returns>Whether the payload had the exact accepted shape.</returns>
public delegate bool SteamUiPayloadReader<T>(JsonElement payload, out T value);

/// <summary>The one browse vocabulary a host page and its renderer share.</summary>
/// <remarks>
///     The injected kit sends the same names (<c>SteamUiBrowse</c>), and
///     <see cref="SteamUiModuleBuilder.BrowseCommands(string, SteamUiBrowseBindings)" /> declares them.
/// </remarks>
public static class SteamUiBrowseCommands
{
    /// <summary>Shows one of the page's tabs: <c>{"tab": id}</c>.</summary>
    public const string SetTab = "setTab";

    /// <summary>Runs the page's query from its first page; the payload is the page's own.</summary>
    public const string Browse = "browse";

    /// <summary>Appends the query's next page.</summary>
    public const string More = "more";

    /// <summary>Opens one item's detail: <c>{"id": id}</c>.</summary>
    public const string Open = "open";

    /// <summary>Closes the item's detail and returns to the collection.</summary>
    public const string CloseDetail = "closeDetail";

    /// <summary>Clears the page's notice or error.</summary>
    public const string Dismiss = "dismiss";
}

/// <summary>The backend of a page's browse commands; a null member declares no command.</summary>
public sealed class SteamUiBrowseBindings
{
    /// <summary>What the page browses, named in a refusal, such as "themes".</summary>
    public required string Subject { get; init; }

    /// <summary>The tab ids <see cref="SetTab" /> accepts.</summary>
    public IReadOnlyCollection<string>? Tabs { get; init; }

    /// <summary>Shows a tab.</summary>
    public Func<string, CancellationToken, Task<SteamUiCommandResult>>? SetTab { get; init; }

    /// <summary>Appends the query's next page.</summary>
    public Func<CancellationToken, Task<SteamUiCommandResult>>? More { get; init; }

    /// <summary>Opens one item's detail by its id.</summary>
    public Func<string, CancellationToken, Task<SteamUiCommandResult>>? Open { get; init; }

    /// <summary>Closes the item's detail.</summary>
    public Func<CancellationToken, Task<SteamUiCommandResult>>? CloseDetail { get; init; }

    /// <summary>Clears the page's notice or error.</summary>
    public Func<CancellationToken, Task<SteamUiCommandResult>>? Dismiss { get; init; }
}

/// <summary>Public building blocks for consumer- and plugin-owned typed Steam UI modules.</summary>
/// <remarks>
///     A null typed reading withholds that publication; it does not clear the document's last state.
///     To retract content while its patch remains installed, publish the surface's explicit empty
///     state. Patch enablement and removal are separate host decisions. A publication revision must
///     change whenever its serialized state changes, including a change to empty state.
/// </remarks>
public static class SteamUiModuleBuilder
{
    /// <summary>Declares a surface that publishes one typed state.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="id">The module id.</param>
    /// <param name="patchId">The patch the state is published under.</param>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">Reads the current state, or null to publish nothing this round.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <param name="patches">The patches that install the surface.</param>
    /// <param name="commands">The commands the surface answers.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module<T>(
        string id,
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyList<ISteamUiPatch> patches,
        IReadOnlyList<SteamUiCommandHandler> commands)
        where T : class
    {
        return new SteamUiModule(id, patches, [Publication(patchId, enabled, read, typeInfo)], commands);
    }

    /// <summary>Creates a typed state publication with source-generated JSON metadata.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="patchId">The patch receiving the state.</param>
    /// <param name="enabled">Whether this publication is currently active.</param>
    /// <param name="read">Reads the current state, or null to publish nothing.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <returns>The module publication.</returns>
    public static SteamUiStatePublication Publication<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        return Create(patchId, enabled, read, typeInfo, null);
    }

    /// <summary>Creates a typed state publication that the host stamps with a revision.</summary>
    /// <typeparam name="T">The published reference-type state.</typeparam>
    /// <param name="patchId">The patch receiving the state.</param>
    /// <param name="enabled">Whether this publication is currently active.</param>
    /// <param name="read">Reads the current state, or null to publish nothing.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for the state.</param>
    /// <param name="revision">
    ///     The state's current revision; see <see cref="SteamUiStatePublication.Revision" />.
    /// </param>
    /// <returns>The module publication.</returns>
    public static SteamUiStatePublication Publication<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        Func<long> revision)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(revision);
        return Create(patchId, enabled, read, typeInfo, revision);
    }

    private static SteamUiStatePublication Create<T>(
        string patchId,
        Func<bool> enabled,
        Func<ValueTask<T?>> read,
        JsonTypeInfo<T> typeInfo,
        Func<long>? revision)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new SteamUiStatePublication(patchId, enabled, async () =>
        {
            var state = await read().ConfigureAwait(false);
            return state is null ? null : JsonSerializer.SerializeToElement(state, typeInfo);
        }, revision);
    }

    /// <summary>Creates a command that validates one exact payload before invoking its backend.</summary>
    /// <typeparam name="T">The parsed payload value.</typeparam>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="reader">The exact payload reader.</param>
    /// <param name="apply">The backend operation.</param>
    /// <param name="invalid">The refusal shown when payload validation fails.</param>
    /// <returns>The command handler.</returns>
    /// <remarks>
    ///     <paramref name="apply" /> runs on the bridge's request pump in arrival order and must return
    ///     its task promptly; see <see cref="SteamUiCommandDelegate" />.
    /// </remarks>
    public static SteamUiCommandHandler Command<T>(
        string patchId,
        string command,
        SteamUiPayloadReader<T> reader,
        Func<T, CancellationToken, Task<SteamUiCommandResult>> apply,
        string invalid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (request, cancellationToken) =>
            reader(request.Payload, out var value)
                ? apply(value, cancellationToken)
                : Task.FromResult(SteamUiCommandResult.Invalid(invalid)));
    }

    /// <summary>Declares a page's browse commands in the one browse vocabulary.</summary>
    /// <param name="patchId">The page's patch.</param>
    /// <param name="bindings">The backend of each command; a null member declares no command.</param>
    /// <returns>The handlers, in vocabulary order.</returns>
    /// <remarks>
    ///     The renderer's flows send these names (<c>SteamUiBrowse</c> in the injected kit), so a page
    ///     never invents its own word for the same step. <c>setTab</c> takes exactly
    ///     <c>{"tab": one of Tabs}</c>, <c>open</c> exactly <c>{"id": non-blank}</c>, and <c>more</c>,
    ///     <c>closeDetail</c> and <c>dismiss</c> no payload the backend reads. A page with a query
    ///     declares <c>browse</c> with
    ///     <see cref="BrowseCommands{TQuery}(string, SteamUiBrowseBindings, SteamUiPayloadReader{TQuery}, Func{TQuery, CancellationToken, Task{SteamUiCommandResult}})" />.
    /// </remarks>
    /// <exception cref="ArgumentException">A tab command has no tabs.</exception>
    public static IReadOnlyList<SteamUiCommandHandler> BrowseCommands(string patchId, SteamUiBrowseBindings bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindings.Subject);
        List<SteamUiCommandHandler> commands = [];
        if (bindings.SetTab is { } setTab)
        {
            if (bindings.Tabs is not { Count: > 0 } tabs)
            {
                throw new ArgumentException("A tab command needs the page's tabs.", nameof(bindings));
            }

            commands.Add(Command<string>(patchId, SteamUiBrowseCommands.SetTab,
                (JsonElement payload, out string tab) => SteamUiPayload.TryReadOnlyChoice(payload, "tab", tabs, out tab),
                setTab, $"The {bindings.Subject} tab payload is invalid."));
        }

        if (bindings.More is { } more)
        {
            commands.Add(Command(patchId, SteamUiBrowseCommands.More, more));
        }

        if (bindings.Open is { } open)
        {
            commands.Add(Command<string>(patchId, SteamUiBrowseCommands.Open,
                (JsonElement payload, out string id) => SteamUiPayload.TryReadOnlyString(payload, "id", out id),
                open, $"The {bindings.Subject} item payload is invalid."));
        }

        if (bindings.CloseDetail is { } closeDetail)
        {
            commands.Add(Command(patchId, SteamUiBrowseCommands.CloseDetail, closeDetail));
        }

        if (bindings.Dismiss is { } dismiss)
        {
            commands.Add(Command(patchId, SteamUiBrowseCommands.Dismiss, dismiss));
        }

        return commands;
    }

    /// <summary>Declares a page's browse commands, including its query, in the one browse vocabulary.</summary>
    /// <typeparam name="TQuery">The page's parsed query.</typeparam>
    /// <param name="patchId">The page's patch.</param>
    /// <param name="bindings">The backend of each other command; a null member declares no command.</param>
    /// <param name="readQuery">Reads the page's exact query payload for <c>browse</c>.</param>
    /// <param name="browse">Runs the query.</param>
    /// <returns>The handlers, with <c>browse</c> after <c>setTab</c>.</returns>
    /// <exception cref="ArgumentException">A tab command has no tabs.</exception>
    public static IReadOnlyList<SteamUiCommandHandler> BrowseCommands<TQuery>(
        string patchId,
        SteamUiBrowseBindings bindings,
        SteamUiPayloadReader<TQuery> readQuery,
        Func<TQuery, CancellationToken, Task<SteamUiCommandResult>> browse)
    {
        ArgumentNullException.ThrowIfNull(readQuery);
        ArgumentNullException.ThrowIfNull(browse);
        List<SteamUiCommandHandler> commands = [.. BrowseCommands(patchId, bindings)];
        var at = commands.FindIndex(command => command.Command == SteamUiBrowseCommands.SetTab) + 1;
        commands.Insert(at, Command(patchId, SteamUiBrowseCommands.Browse, readQuery, browse,
            $"The {bindings.Subject} browse payload is invalid."));
        return commands;
    }

    /// <summary>Creates a command whose backend does not consume a payload.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="apply">The backend operation.</param>
    /// <returns>The command handler.</returns>
    /// <remarks>
    ///     <paramref name="apply" /> runs on the bridge's request pump in arrival order and must return
    ///     its task promptly; see <see cref="SteamUiCommandDelegate" />.
    /// </remarks>
    public static SteamUiCommandHandler Command(
        string patchId,
        string command,
        Func<CancellationToken, Task<SteamUiCommandResult>> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(apply);
        return new SteamUiCommandHandler(patchId, command, (_, cancellationToken) => apply(cancellationToken));
    }
}
