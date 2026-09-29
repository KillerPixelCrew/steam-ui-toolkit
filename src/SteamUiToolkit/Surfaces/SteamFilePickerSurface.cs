using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One place the picker offers to start from: a drive or a well-known folder.</summary>
/// <param name="Path">The absolute path.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Kind"><c>drive</c> or <c>folder</c>.</param>
/// <param name="Detail">A second line, such as the free space, or empty.</param>
public sealed record SteamFilePlace(string Path, string Name, string Kind, string Detail);

/// <summary>One item in a listed folder.</summary>
/// <param name="Name">Its file or folder name.</param>
/// <param name="Path">Its absolute path.</param>
/// <param name="Folder">Whether it is a folder.</param>
public sealed record SteamFileEntry(string Name, string Path, bool Folder);

/// <summary>What the picker shows for one folder.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Parent">Its parent, or empty at the root of a drive.</param>
/// <param name="Entries">Its subfolders, then the files that match, each sorted by name.</param>
/// <param name="Error">Why it could not be listed, or null.</param>
public sealed record SteamFileListing(
    string Path,
    string Parent,
    IReadOnlyList<SteamFileEntry> Entries,
    string? Error);

/// <summary>The places the picker starts from.</summary>
/// <param name="Places">Drives first, then the user's folders.</param>
public sealed record SteamFilePlaces(IReadOnlyList<SteamFilePlace> Places);

/// <summary>
///     A folder and file picker for pages drawn inside Steam, which has no picker a page can open.
/// </summary>
/// <remarks>
///     <para>
///         The injected side draws the picker as a Steam modal (<c>showSteamFilePicker</c> in
///         <c>file-picker.ts</c>); this answers its two questions. It lists names only: it never
///         opens, reads or writes a file, and the page decides what to do with the path the user
///         chose. Hidden and system entries are left out, as Explorer leaves them out by default.
///     </para>
///     <para>
///         A Windows dialog is no substitute: it opens behind Big Picture and cannot be driven with a
///         controller.
///     </para>
/// </remarks>
public static class SteamFilePickerSurface
{
    /// <summary>The patch id the picker's commands are addressed to. No patch is installed for it.</summary>
    public const string PatchId = "steam-ui.file-picker";

    /// <summary>The exact command vocabulary the picker sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["listPlaces", "listFolder"];

    /// <summary>The drives that are ready, then the user's desktop, documents and downloads.</summary>
    /// <returns>The places.</returns>
    public static SteamFilePlaces ListPlaces()
    {
        List<SteamFilePlace> places = [];
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var label = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : Kind(drive.DriveType);
                places.Add(new SteamFilePlace(
                    drive.RootDirectory.FullName,
                    $"{label} ({drive.Name.TrimEnd('\\')})",
                    "drive",
                    $"{Size(drive.AvailableFreeSpace)} free of {Size(drive.TotalSize)}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that stops answering is simply not offered.
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (name, path) in new[]
                 {
                     ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                     ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                     ("Downloads", profile.Length > 0 ? Path.Combine(profile, "Downloads") : string.Empty)
                 })
        {
            if (path.Length > 0 && Directory.Exists(path))
            {
                places.Add(new SteamFilePlace(path, name, "folder", string.Empty));
            }
        }

        return new SteamFilePlaces(places);
    }

    /// <summary>Lists one folder's subfolders and the files that match.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="extensions">The file types to include, each with its dot; empty for folders only.</param>
    /// <returns>The listing, with an error when the folder cannot be read.</returns>
    public static SteamFileListing ListFolder(string path, IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SteamFileListing(path, string.Empty, [], "That is not a folder path.");
        }

        var parent = Directory.GetParent(full)?.FullName ?? string.Empty;
        if (!Path.IsPathFullyQualified(full))
        {
            return new SteamFileListing(full, parent, [], "That is not a folder path.");
        }

        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false
        };
        try
        {
            DirectoryInfo directory = new(full);
            if (!directory.Exists)
            {
                return new SteamFileListing(full, parent, [], "This folder no longer exists.");
            }

            var folders = directory.EnumerateDirectories("*", options)
                .Select(entry => new SteamFileEntry(entry.Name, entry.FullName, true))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var files = extensions.Count == 0
                ? []
                : directory.EnumerateFiles("*", options)
                    .Where(entry => extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase))
                    .Select(entry => new SteamFileEntry(entry.Name, entry.FullName, false))
                    .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            return new SteamFileListing(full, parent, [.. folders, .. files], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new SteamFileListing(full, parent, [], ex is UnauthorizedAccessException
                ? "This folder cannot be opened."
                : ex.Message);
        }
    }

    /// <summary>Reads the exact <c>listFolder</c> payload: a path and a list of extensions.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="request">The path and extensions, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadListFolder(JsonElement payload, out (string Path, IReadOnlyList<string> Extensions) request)
    {
        request = (string.Empty, []);
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !payload.TryGetProperty("extensions", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> extensions = [];
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 1 } extension
                || !extension.StartsWith('.')
                || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            extensions.Add(extension);
        }

        request = (path, extensions);
        return true;
    }

    /// <summary>Declares the picker's commands as one module. It publishes nothing and patches nothing.</summary>
    /// <param name="enabled">Whether a page may browse the file system right now.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, string id = "file-picker")
    {
        ArgumentNullException.ThrowIfNull(enabled);
        return new SteamUiModule(
            id,
            commands:
            [
                SteamUiModuleBuilder.Command(PatchId, "listPlaces", _ => Task.FromResult(!enabled()
                    ? SteamUiCommandResult.Refused
                    : new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                        ListPlaces(), SteamSurfaceJsonContext.Default.SteamFilePlaces)))),
                SteamUiModuleBuilder.Command<(string Path, IReadOnlyList<string> Extensions)>(
                    PatchId,
                    "listFolder",
                    TryReadListFolder,
                    (request, cancellationToken) => !enabled()
                        ? Task.FromResult(SteamUiCommandResult.Refused)
                        : Task.Run(() => new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                                ListFolder(request.Path, request.Extensions),
                                SteamSurfaceJsonContext.Default.SteamFileListing)),
                            cancellationToken),
                    "The folder request is invalid.")
            ]);
    }

    private static string Kind(DriveType type)
    {
        return type switch
        {
            DriveType.Removable => "Removable drive",
            DriveType.Network => "Network drive",
            DriveType.CDRom => "Disc drive",
            _ => "Local disk"
        };
    }

    private static string Size(long bytes)
    {
        const double gigabyte = 1024d * 1024 * 1024;
        return bytes >= 1024 * gigabyte
            ? $"{bytes / (1024 * gigabyte):0.0} TB"
            : $"{bytes / gigabyte:0} GB";
    }
}
