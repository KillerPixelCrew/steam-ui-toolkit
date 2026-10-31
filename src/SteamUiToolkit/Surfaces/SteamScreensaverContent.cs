using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One local source offered beside the stock entries in Steam's chooser.</summary>
public sealed record SteamScreensaverSource
{
    /// <summary>Stable host identity.</summary>
    public required string Id { get; init; }
    /// <summary>Steam's uioverride-prefixed chooser identity; its decoded name is visible.</summary>
    public required string SelectorId { get; init; }
    /// <summary>User-facing name.</summary>
    public required string Name { get; init; }
    /// <summary>Images, scene, or pack when a manifest could not be read.</summary>
    public required string Type { get; init; }
    /// <summary>The user-selected local source folder.</summary>
    public required string Folder { get; init; }
    /// <summary>Whether discovery found playable content.</summary>
    public bool Available { get; init; }
    /// <summary>Content count, compatibility or missing-source reason.</summary>
    public required string Detail { get; init; }
    /// <summary>Whether the source is a manifest pack rather than a plain image folder.</summary>
    public bool Pack { get; init; }
    /// <summary>Number of playable images, zero for scenes or unavailable folders.</summary>
    public int ImageCount { get; init; }
    /// <summary>Whether a cover image can be requested lazily.</summary>
    public bool HasPreview { get; init; }
    /// <summary>Identity of the current cover, used to invalidate surface-local image caches.</summary>
    public string PreviewRevision { get; init; } = "";
    /// <summary>Shuffle images instead of sorting by filename.</summary>
    public bool Shuffle { get; init; }
    /// <summary>Image transition interval in seconds.</summary>
    public int IntervalSeconds { get; init; }
    /// <summary>Crop images to fill instead of fitting their aspect ratio.</summary>
    public bool Fill { get; init; }
    /// <summary>Maximum scene animation callback rate.</summary>
    public int FramesPerSecond { get; init; }
}

/// <summary>Detached host-owned screensaver discovery, configuration and playback state.</summary>
public sealed record SteamScreensaverContentState
{
    /// <summary>Every registered source, including currently unavailable removable folders.</summary>
    public IReadOnlyList<SteamScreensaverSource> Sources { get; init; } = [];
    /// <summary>Currently reported host source identity, empty for a stock choice.</summary>
    public string SelectedId { get; init; } = "";
    /// <summary>The native identity, including stock choices.</summary>
    public string NativeSelection { get; init; } = "";
    /// <summary>One requested native selector choice, or null after Steam acknowledges it.</summary>
    public string? SelectionRequest { get; init; }
    /// <summary>Monotonic selection operation identity.</summary>
    public long SelectionRevision { get; init; }
    /// <summary>Preview once the requested selection is confirmed.</summary>
    public bool PreviewSelection { get; init; }
    /// <summary>Monotonic request to dismiss after real native host input.</summary>
    public long DismissRevision { get; init; }
    /// <summary>Player status, resource policy and safe fallback diagnostics.</summary>
    public string Status { get; init; } = "";
    /// <summary>Last discovery or player error, or null.</summary>
    public string? Error { get; init; }
    /// <summary>Monotonic state revision.</summary>
    public long Revision { get; init; }
}

/// <summary>A validated command from trusted screensaver configuration chrome, never from scene content.</summary>
public sealed record SteamScreensaverContentCommand
{
    /// <summary>The closed operation name.</summary>
    public required string Action { get; init; }
    /// <summary>Host source identity, or an empty identity for stock playback.</summary>
    public string Id { get; init; } = "";
    /// <summary>New source name.</summary>
    public string Name { get; init; } = "";
    /// <summary>New local folder.</summary>
    public string Folder { get; init; } = "";
    /// <summary>The one field to update, preserving changes to the other source options.</summary>
    public string Option { get; init; } = "";
    /// <summary>Image shuffle choice.</summary>
    public bool Shuffle { get; init; }
    /// <summary>Image interval.</summary>
    public int IntervalSeconds { get; init; }
    /// <summary>Image aspect-fill choice.</summary>
    public bool Fill { get; init; }
    /// <summary>Scene animation callback limit.</summary>
    public int FramesPerSecond { get; init; }
    /// <summary>The effective native screensaver visibility, after real input reconciliation.</summary>
    public bool Active { get; init; }
    /// <summary>The native selected identity reported by trusted Steam settings.</summary>
    public string SelectorId { get; init; } = "";
    /// <summary>The selection operation answered by native configuration chrome.</summary>
    public long Revision { get; init; }
    /// <summary>Selection refusal or uncertainty, or null after confirmation.</summary>
    public string? Error { get; init; }
}

/// <summary>Owns user-approved sources and a player isolated from all privileged Steam/host APIs.</summary>
public interface ISteamScreensaverContentBackend
{
    /// <summary>Executes one validated configuration or playback operation.</summary>
    /// <param name="command">The closed command.</param>
    /// <param name="cancellationToken">Cancellation of the request.</param>
    /// <returns>Accepted or a reasoned refusal.</returns>
    Task<SteamUiCommandResult> ExecuteAsync(SteamScreensaverContentCommand command, CancellationToken cancellationToken);
}

internal static class SteamScreensaverContent
{
    internal static bool TryRead(JsonElement payload, out SteamScreensaverContentCommand command)
    {
        command = new SteamScreensaverContentCommand { Action = "" };
        if (!SteamUiPayload.TryReadNonBlankString(payload, "action", out var action))
        {
            return false;
        }

        switch (action)
        {
            case "rename":
                if (!SteamUiPayload.HasExactly(payload, 3) || !Text(payload, "id", 64, out var renamed)
                    || !Text(payload, "name", 128, out var newName))
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand { Action = action, Id = renamed, Name = newName };
                return true;
            case "relocate":
                if (!SteamUiPayload.HasExactly(payload, 3) || !Text(payload, "id", 64, out var relocated)
                    || !Text(payload, "folder", 4096, out var newFolder))
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand { Action = action, Id = relocated, Folder = newFolder };
                return true;
            case "addImages":
            case "addPack":
                if (!SteamUiPayload.HasExactly(payload, 3)
                    || !Text(payload, "name", 128, out var name)
                    || !Text(payload, "folder", 4096, out var folder))
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand { Action = action, Name = name, Folder = folder };
                return true;
            case "configure":
                if (!SteamUiPayload.HasExactly(payload, 4) || !Text(payload, "id", 64, out var configured)
                    || !Text(payload, "option", 32, out var option))
                {
                    return false;
                }

                if (option is "shuffle" or "fill")
                {
                    if (!SteamUiPayload.TryReadBoolean(payload, "value", out var enabled))
                    {
                        return false;
                    }

                    command = new SteamScreensaverContentCommand
                    {
                        Action = action, Id = configured, Option = option,
                        Shuffle = enabled, Fill = enabled
                    };
                    return true;
                }

                if (option is "intervalSeconds" or "framesPerSecond"
                    && SteamUiPayload.TryReadInt(payload, "value", option == "intervalSeconds" ? 2 : 1,
                        option == "intervalSeconds" ? 3600 : 60, out var value))
                {
                    command = new SteamScreensaverContentCommand
                    {
                        Action = action, Id = configured, Option = option,
                        IntervalSeconds = value, FramesPerSecond = value
                    };
                    return true;
                }

                return false;
            case "remove":
            case "select":
            case "preview":
            case "thumbnail":
                if (!SteamUiPayload.HasExactly(payload, 2) || !Text(payload, "id", 64, out var id))
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand { Action = action, Id = id };
                return true;
            case "selectionResult":
                if (!SteamUiPayload.HasExactly(payload, 3)
                    || !SteamUiPayload.TryReadLong(payload, "revision", 1, out var operation)
                    || !payload.TryGetProperty("error", out var error)
                    || error.ValueKind is not JsonValueKind.String and not JsonValueKind.Null
                    || error.ValueKind == JsonValueKind.String && error.GetString()!.Length > 512)
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand
                {
                    Action = action, Revision = operation,
                    Error = error.ValueKind == JsonValueKind.Null ? null : error.GetString()
                };
                return true;
            case "refresh":
                command = new SteamScreensaverContentCommand { Action = action };
                return SteamUiPayload.HasExactly(payload, 1);
            case "playback":
                if (!SteamUiPayload.HasExactly(payload, 4)
                    || !SteamUiPayload.TryReadBoolean(payload, "active", out var active)
                    || !payload.TryGetProperty("id", out var sourceId) || sourceId.ValueKind != JsonValueKind.String
                    || sourceId.GetString() is not { Length: <= 64 } source
                    || !payload.TryGetProperty("selectorId", out var selectorId) || selectorId.ValueKind != JsonValueKind.String
                    || selectorId.GetString() is not { Length: <= 2048 } selector)
                {
                    return false;
                }

                command = new SteamScreensaverContentCommand
                {
                    Action = action, Id = source, Active = active, SelectorId = selector
                };
                return true;
            default:
                return false;
        }
    }

    private static bool Text(JsonElement payload, string property, int maximum, out string value)
    {
        return SteamUiPayload.TryReadNonBlankString(payload, property, out value)
               && value.Length <= maximum && !value.Contains('\0');
    }
}
