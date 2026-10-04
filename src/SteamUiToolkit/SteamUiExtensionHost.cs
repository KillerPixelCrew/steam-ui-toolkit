using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>Reads installed extensions from a directory and decides which may contribute.</summary>
/// <remarks>
///     <see cref="Discover" /> examines every package directory and returns each extension it found,
///     loaded or rejected with a <see cref="SteamUiExtensionRejection" /> reason. It checks identity,
///     scoping, the API version and the script's location, and it resolves conflicts between packages.
///     <para>
///         This reads and validates only. It loads no assemblies and executes nothing: the script it
///         returns is text. Composing an extension into modules and patches, and removing the patches a
///         rejected extension declared, is the host's job; a rejected extension whose manifest parsed
///         still carries it for that. An extension that fails any check is still reported rather than
///         skipped silently, because "my extension does nothing" with no reason anywhere is the failure
///         this whole subsystem exists to avoid.
///     </para>
///     <para>
///         <b>This is not a sandbox.</b> Injected script runs with the same reach as the host's own gates —
///         it can read and change anything in Steam's front-end. The checks here are about identity and
///         collision, so one extension cannot impersonate another or quietly claim its patches. Treat
///         installing an extension as running its code, because that is what it is.
///     </para>
/// </remarks>
public static class SteamUiExtensionHost
{
    /// <summary>The extension API version this host implements.</summary>
    public const int ApiVersion = 1;

    /// <summary>The manifest file every extension package must contain.</summary>
    public const string ManifestFileName = "extension.steam-ui.json";

    /// <summary>The prefix the toolkit's own patches use, which no extension may claim.</summary>
    public const string ReservedPrefix = "steam-ui";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    /// <summary>Examines every package directory and reports what each one is.</summary>
    /// <param name="root">Directory holding one subdirectory per installed extension.</param>
    /// <param name="reservedPrefixes">
    ///     The host's own id prefixes (for example <c>wsgm</c>), refused like <see cref="ReservedPrefix" />
    ///     so an extension cannot claim an id or a patch the host uses.
    /// </param>
    /// <returns>
    ///     Every extension found, loaded or rejected, ordered by id with ordinal comparison. Packages are
    ///     examined in directory-name order, so the same install always gives the same winner of a
    ///     conflict. An absent or unreadable root is an empty list rather than an error: no extensions
    ///     installed is the normal case.
    /// </returns>
    public static IReadOnlyList<SteamUiExtension> Discover(
        string root,
        IReadOnlyCollection<string>? reservedPrefixes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string[] directories;
        try
        {
            directories = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SteamUiLog.Change(
                "steam.ui.extensions.root",
                $"Steam UI extensions could not be listed: {ex.Message}",
                true);
            return [];
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        List<SteamUiExtension> examined = [];
        HashSet<string> claimedIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> claimedPatches = new(StringComparer.Ordinal);

        List<string> reserved = [ReservedPrefix, .. reservedPrefixes ?? []];
        foreach (var directory in directories)
        {
            var extension = Examine(directory, reserved);
            if (extension.Loaded)
            {
                extension = ResolveConflicts(extension, claimedIds, claimedPatches);
            }

            examined.Add(extension);
        }

        // Stable, so two extensions with one id keep the directory order that decided the conflict.
        examined = [.. examined.OrderBy(static extension => extension.Id, StringComparer.Ordinal)];

        foreach (var extension in examined)
        {
            SteamUiLog.Change(
                "steam.ui.extension." + extension.Id,
                extension.Loaded
                    ? $"Steam UI extension {extension.Id} loaded."
                    : $"Steam UI extension {extension.Id} refused ({extension.Rejection}): "
                      + (extension.Detail ?? "no detail"),
                !extension.Loaded);
        }

        return examined;
    }

    private static SteamUiExtension ResolveConflicts(
        SteamUiExtension extension,
        HashSet<string> claimedIds,
        HashSet<string> claimedPatches)
    {
        // Check the complete claim set before committing any of it. A rejected extension must not
        // reserve its id or an earlier patch and thereby make a later, otherwise valid extension
        // look conflicting.
        if (claimedIds.Contains(extension.Id))
        {
            return Reject(
                extension.Id,
                SteamUiExtensionRejection.Conflict,
                "another installed extension already uses this id",
                extension.Manifest);
        }

        foreach (var patch in extension.Manifest!.Patches)
        {
            if (claimedPatches.Contains(patch))
            {
                return Reject(
                    extension.Id,
                    SteamUiExtensionRejection.Conflict,
                    $"patch '{patch}' is already claimed by another extension",
                    extension.Manifest);
            }
        }

        claimedIds.Add(extension.Id);
        foreach (var patch in extension.Manifest.Patches)
        {
            claimedPatches.Add(patch);
        }

        return extension;
    }

    private static SteamUiExtension Examine(string directory, IReadOnlyList<string> reservedPrefixes)
    {
        var fallbackId = Path.GetFileName(directory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, ManifestFileName);

        SteamUiExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SteamUiExtensionManifest>(
                File.ReadAllText(manifestPath), ManifestOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or NotSupportedException)
        {
            return Reject(fallbackId, SteamUiExtensionRejection.UnreadableManifest, ex.Message);
        }

        if (manifest is null
            || !IsSafeIdentifier(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Script)
            || manifest.Patches is null)
        {
            return Reject(
                fallbackId,
                SteamUiExtensionRejection.InvalidManifest,
                "id, name, version and script are required, and id must be a safe identifier");
        }

        var distinctPatches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in manifest.Patches)
        {
            if (!IsSafeIdentifier(patch) || !distinctPatches.Add(patch!))
            {
                return Reject(
                    manifest.Id,
                    SteamUiExtensionRejection.InvalidManifest,
                    "patch ids must be non-empty safe identifiers and may appear only once",
                    manifest);
            }
        }

        // The toolkit's own patches and the host's are not an extension's to claim.
        foreach (var prefix in reservedPrefixes)
        {
            if (Claims(manifest.Id, prefix) || manifest.Patches.Exists(patch => Claims(patch, prefix)))
            {
                return Reject(
                    manifest.Id,
                    SteamUiExtensionRejection.ReservedPrefix,
                    $"ids and patches under '{prefix}' belong to the host",
                    manifest);
            }
        }

        if (manifest.ApiVersion != ApiVersion)
        {
            return Reject(
                manifest.Id,
                SteamUiExtensionRejection.ApiVersionMismatch,
                $"built against extension API {manifest.ApiVersion}, this host implements {ApiVersion}",
                manifest);
        }

        // A patch this extension does not own is one it could use to displace another's work, so
        // scoping is checked before the script is even read.
        foreach (var patch in manifest.Patches)
        {
            if (!patch.StartsWith(manifest.Id + ".", StringComparison.Ordinal))
            {
                return Reject(
                    manifest.Id,
                    SteamUiExtensionRejection.UnscopedPatch,
                    $"patch '{patch}' must start with '{manifest.Id}.'",
                    manifest);
            }
        }

        if (!TryReadScript(directory, manifest.Script, out var script, out var scriptError))
        {
            return Reject(manifest.Id, SteamUiExtensionRejection.UnreadableScript, scriptError, manifest);
        }

        return new SteamUiExtension(
            manifest.Id, manifest, script, SteamUiExtensionRejection.None, null);
    }

    private static bool TryReadScript(
        string directory,
        string relative,
        out string? script,
        out string? error)
    {
        script = null;
        error = null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }

        // The script must be inside the package. Without this a manifest could name
        // ..\..\anything and have the host read and inject a file it never installed.
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = "the script path leaves the package directory";
            return false;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                error = "the declared script does not exist";
                return false;
            }

            script = File.ReadAllText(full, new UTF8Encoding(false, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DecoderFallbackException)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    private static SteamUiExtension Reject(
        string id,
        SteamUiExtensionRejection rejection,
        string? detail,
        SteamUiExtensionManifest? manifest = null)
    {
        return new SteamUiExtension(id, manifest, null, rejection, detail);
    }

    // The prefix itself, or anything under it as a dotted scope.
    private static bool Claims(string id, string prefix)
    {
        return string.Equals(id, prefix, StringComparison.Ordinal)
               || id.StartsWith(prefix + ".", StringComparison.Ordinal);
    }

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '.' or '-' or '_'))
            {
                return false;
            }
        }

        // A leading or trailing separator makes the patch-scope prefix ambiguous.
        return value[0] is not ('.' or '-' or '_')
               && value[^1] is not ('.' or '-' or '_');
    }
}
