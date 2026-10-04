using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One state value a module publishes to the injected side.</summary>
/// <param name="PatchId">The patch the injected side receives this state as.</param>
/// <param name="Enabled">
///     Whether the value may be published right now. Evaluated per publish, so a
///     module can stay registered while its backend is unavailable.
/// </param>
/// <param name="Read">
///     Produces the current value, or <see langword="null" /> to publish nothing this
///     round — which is how a reading that is momentarily unavailable stays distinct from a zero.
/// </param>
/// <param name="Revision">
///     The surface's revision of that value, or <see langword="null" /> when it has none. With one, a
///     round whose revision the document already holds skips <paramref name="Read" /> entirely, so a
///     large state is neither rebuilt nor serialized on rounds raised by other surfaces. The revision
///     has to change whenever the value would.
/// </param>
public sealed record SteamUiStatePublication(
    string PatchId,
    Func<bool> Enabled,
    Func<ValueTask<JsonElement?>> Read,
    Func<long>? Revision = null);

/// <summary>The outcome of one semantic command.</summary>
/// <param name="Succeeded">Whether the command changed what it claimed to change.</param>
/// <param name="Error">
///     Why it did not, when it did not. Supply one on every failure: an unexplained refusal is the
///     defect this contract exists to prevent, because the injected side has nowhere to put a reason
///     and the user sees only a control that did nothing. The runtime answers a failure without one
///     with a fixed "no reason reported" text.
/// </param>
/// <param name="Payload">An optional answer, such as a route to follow or the value as it was written.</param>
public readonly record struct SteamUiCommandResult(
    bool Succeeded,
    string? Error,
    JsonElement? Payload = null)
{
    /// <summary>The command applied.</summary>
    public static SteamUiCommandResult Applied { get; } = new(true, null);

    /// <summary>The backing service is not active, so the command was not attempted.</summary>
    public static SteamUiCommandResult Refused { get; } = new(
        false,
        "The requested semantic service is not active.");

    /// <summary>The module answering the command failed earlier and was turned off for this session.</summary>
    public static SteamUiCommandResult Quarantined { get; } = new(
        false,
        "This surface was turned off after an error.");

    /// <summary>No registered module answers the addressed command.</summary>
    public static SteamUiCommandResult Unhandled { get; } = new(
        false,
        "No handler is registered for this command.");

    /// <summary>The device cannot choose between power modes, so the selection was not attempted.</summary>
    public static SteamUiCommandResult ModeSelectionUnsupported { get; } = new(
        false,
        "Mode selection is not supported on this device.");

    /// <summary>The text a failure is answered with when its handler gave no reason.</summary>
    internal const string NoReasonReported = "no reason reported";

    /// <summary>A refusal of a payload that did not have the command's shape; the backend was not reached.</summary>
    /// <param name="reason">The fixed reason shown to the user.</param>
    /// <returns>The failed result.</returns>
    public static SteamUiCommandResult Invalid(string reason)
    {
        return new SteamUiCommandResult(false, reason);
    }

    /// <summary>
    ///     The command applied and the injected side should open a page: the answer the Extensions
    ///     tab, the game context menu and the navigation panel read as <c>{ route }</c>.
    /// </summary>
    /// <param name="route">The route to follow, such as <c>/wsgm/themes</c>.</param>
    /// <returns>An applied result carrying the route.</returns>
    public static SteamUiCommandResult Route(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("route", route);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new SteamUiCommandResult(true, null, document.RootElement.Clone());
    }
}

/// <summary>Answers one semantic command from the injected side.</summary>
/// <param name="request">The bridge request, already validated and generation-checked.</param>
/// <param name="cancellationToken">Cancels the command.</param>
/// <returns>The truthful outcome, including a reason when nothing happened.</returns>
/// <remarks>
///     Invoked on the bridge's request pump in arrival order, so it must return its task promptly:
///     until it does, no later request and no cancel is processed. A blocking read goes on
///     <see cref="Task.Run(System.Action)" /> inside the handler. A write keeps its order through its
///     backend's own serialization rather than leaving the pump, so two writes to one control cannot
///     finish in the opposite order.
/// </remarks>
public delegate Task<SteamUiCommandResult> SteamUiCommandDelegate(
    SteamUiBridgeRequest request,
    CancellationToken cancellationToken);

/// <summary>One command a module answers.</summary>
/// <param name="PatchId">The patch the injected side addresses.</param>
/// <param name="Command">The command name within that patch.</param>
/// <param name="Handle">
///     The handler, invoked on the bridge's request pump in arrival order; see
///     <see cref="SteamUiCommandDelegate" /> for what that asks of it.
/// </param>
public sealed record SteamUiCommandHandler(
    string PatchId,
    string Command,
    SteamUiCommandDelegate Handle);

/// <summary>
///     One Steam UI surface, declared in one place: the patches that install it, the state it publishes,
///     and the commands it answers.
/// </summary>
/// <remarks>
///     This exists because a surface used to be four scattered edits — a patch registration, a
///     publication row, a command row and an id constant — so adding or removing one meant finding all
///     four and getting them consistent. A module is the unit those four belong to.
///     <para>
///         Registration order does not matter: the patch manager orders the patches itself, and
///         publications and commands are keyed rather than ordered.
///     </para>
/// </remarks>
public interface ISteamUiModule
{
    /// <summary>Stable module identity, for diagnostics and duplicate detection.</summary>
    string Id { get; }

    /// <summary>
    ///     The patches that install and remove this surface. May be empty for a module that
    ///     only answers commands against a surface another module installs.
    /// </summary>
    IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <summary>State this module pushes to the injected side.</summary>
    IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <summary>Commands this module answers.</summary>
    IReadOnlyList<SteamUiCommandHandler> Commands { get; }
}

/// <summary>A module declared inline at its call site.</summary>
public sealed class SteamUiModule : ISteamUiModule
{
    /// <summary>Declares one surface.</summary>
    /// <param name="id">Stable module identity.</param>
    /// <param name="patches">Patches that install and remove the surface.</param>
    /// <param name="publications">State pushed to the injected side.</param>
    /// <param name="commands">Commands answered from the injected side.</param>
    public SteamUiModule(
        string id,
        IReadOnlyList<ISteamUiPatch>? patches = null,
        IReadOnlyList<SteamUiStatePublication>? publications = null,
        IReadOnlyList<SteamUiCommandHandler>? commands = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Patches = patches ?? [];
        Publications = publications ?? [];
        Commands = commands ?? [];
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiCommandHandler> Commands { get; }
}

/// <summary>The registered modules, flattened into the three lookups the host drives.</summary>
/// <remarks>
///     Flattening happens once, at construction, so a conflict between two modules is a startup failure
///     with both names in it rather than whichever one happened to win at runtime. Declaration order does
///     not matter: a module that only answers commands may come before the one that installs its patch.
///     Every lookup names the module that owns the callback, so a failure is charged to that module.
/// </remarks>
public sealed class SteamUiModuleSet
{
    private readonly Dictionary<(string PatchId, string Command), (SteamUiCommandDelegate Handle, ISteamUiModule Module)>
        _commands = [];

    /// <summary>Flattens a module list, rejecting duplicate identity.</summary>
    /// <param name="modules">The declared modules.</param>
    /// <exception cref="InvalidOperationException">
    ///     Two modules share an id, register the same patch id, publish state for the same patch id, or
    ///     answer the same patch and command.
    /// </exception>
    public SteamUiModuleSet(IReadOnlyList<ISteamUiModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = modules;

        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var patches = new List<ISteamUiPatch>();
        var installers = new Dictionary<string, ISteamUiModule>(StringComparer.Ordinal);
        var publishers = new Dictionary<string, ISteamUiModule>(StringComparer.Ordinal);
        var publications = new List<SteamUiStatePublication>();
        var owned = new List<(SteamUiStatePublication, ISteamUiModule)>();
        var allowedCommands = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (!seenModules.Add(module.Id))
            {
                throw new InvalidOperationException(
                    $"Steam UI module '{module.Id}' is declared twice.");
            }

            foreach (var patch in module.Patches)
            {
                if (!installers.TryAdd(patch.Id, module))
                {
                    throw new InvalidOperationException(
                        $"Steam UI patch '{patch.Id}' is registered by more than one module; "
                        + $"'{installers[patch.Id].Id}' and '{module.Id}'.");
                }

                patches.Add(patch);
            }

            foreach (var publication in module.Publications)
            {
                if (!publishers.TryAdd(publication.PatchId, module))
                {
                    throw new InvalidOperationException(
                        $"Steam UI state '{publication.PatchId}' is published by more than one module; "
                        + $"'{publishers[publication.PatchId].Id}' and '{module.Id}'.");
                }

                publications.Add(publication);
                owned.Add((publication, module));
                allowedCommands.TryAdd(publication.PatchId, []);
            }

            foreach (var command in module.Commands)
            {
                if (!_commands.TryAdd((command.PatchId, command.Command), (command.Handle, module)))
                {
                    throw new InvalidOperationException(
                        $"Steam UI command '{command.PatchId}/{command.Command}' is answered by "
                        + $"more than one module; '{_commands[(command.PatchId, command.Command)].Module.Id}' "
                        + $"and '{module.Id}'.");
                }

                if (!allowedCommands.TryGetValue(command.PatchId, out var names))
                {
                    names = [];
                    allowedCommands.Add(command.PatchId, names);
                }

                names.Add(command.Command);
            }
        }

        Patches = patches;
        Publications = publications;
        OwnedPublications = owned;
        var vocabulary = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (patchId, commands) in allowedCommands)
        {
            vocabulary.Add(patchId, commands.AsReadOnly());
        }

        AllowedCommands = vocabulary;
    }

    /// <summary>The declared modules, in declaration order.</summary>
    public IReadOnlyList<ISteamUiModule> Modules { get; }

    /// <summary>Every patch across every module.</summary>
    public IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <summary>Every publication across every module.</summary>
    public IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <summary>Every publication with the module that declared it.</summary>
    internal IReadOnlyList<(SteamUiStatePublication Publication, ISteamUiModule Module)> OwnedPublications { get; }

    /// <summary>The exact state identities and commands the bridge may carry for these modules.</summary>
    /// <remarks>
    ///     A publication contributes its patch identity even when it accepts no commands, because the
    ///     injected subscriber is guarded by the same vocabulary as command requests. Deriving this
    ///     view from the modules keeps the bridge and its router from drifting apart.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedCommands { get; }

    /// <summary>Finds the handler for one addressed command.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="handler">The handler, when one is registered.</param>
    /// <returns><see langword="true" /> when a module answers this command.</returns>
    public bool TryGetCommand(
        string patchId,
        string command,
        out SteamUiCommandDelegate? handler)
    {
        return TryGetCommand(patchId, command, out handler, out _);
    }

    /// <summary>Finds the handler for one addressed command and the module that declared it.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="handler">The handler, when one is registered.</param>
    /// <param name="module">The module that answers the command, when one does.</param>
    /// <returns><see langword="true" /> when a module answers this command.</returns>
    internal bool TryGetCommand(
        string patchId,
        string command,
        out SteamUiCommandDelegate? handler,
        out ISteamUiModule? module)
    {
        if (_commands.TryGetValue((patchId, command), out var entry))
        {
            handler = entry.Handle;
            module = entry.Module;
            return true;
        }

        handler = null;
        module = null;
        return false;
    }

    /// <summary>Registers every module's patches with the patch manager.</summary>
    /// <param name="patches">The manager that owns patch lifecycle.</param>
    public void RegisterPatches(SteamUiPatchManager patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        foreach (var patch in Patches)
        {
            patches.Register(patch);
        }
    }
}
