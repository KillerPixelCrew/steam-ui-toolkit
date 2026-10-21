using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading;
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
/// <param name="Link">Whether it is a junction or symbolic link, which opens where it points.</param>
public sealed record SteamFileEntry(string Name, string Path, bool Folder, bool Link = false);

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

/// <summary>The answer to a choice: the path as the file system knows it now, or why it cannot be chosen.</summary>
/// <param name="Path">The chosen path in its ordinary full form, or the path as asked when refused.</param>
/// <param name="Folder">Whether the chosen item is a folder.</param>
/// <param name="Error">Why the item cannot be chosen, or null when <paramref name="Path" /> may be returned.</param>
public sealed record SteamFileSelection(string Path, bool Folder, string? Error);

/// <summary>What a picker lets the user choose.</summary>
public enum SteamFileKind
{
    /// <summary>A file; folders are only entered.</summary>
    File,

    /// <summary>A folder; files are not listed.</summary>
    Folder,

    /// <summary>A file or the folder on screen.</summary>
    Any
}

/// <summary>
///     A folder and file picker for pages drawn inside Steam, which has no picker a page can open.
/// </summary>
/// <remarks>
///     <para>
///         The injected side draws the picker as a Steam modal (<c>showSteamFilePicker</c> in
///         <c>file-picker.ts</c>); this answers its three questions: the places to start from, one
///         folder's listing, and whether a chosen path can still be returned. It lists names only: it
///         never opens, reads or writes a file, and the page decides what to do with the path the user
///         chose. Hidden and system entries are left out, as Explorer leaves them out by default.
///     </para>
///     <para>
///         Paths are accepted only in their full ordinary form or the <c>\\?\</c> form of it; a device
///         path (<c>\\.\</c>, <c>\\?\GLOBALROOT</c>, a volume GUID, <c>CON</c>) is refused, never
///         opened. Junctions and symbolic links are listed and marked as links; nothing is followed
///         below the folder the user is looking at, and a chosen path is returned as the user saw it.
///     </para>
///     <para>
///         The commands run on a worker, so a drive that stops answering never holds up the bridge.
///         A request the page gave up on (navigation, close or its request timeout sends a cancel)
///         returns at once and is not answered; the worker's late result is dropped. The page drops a
///         listing that answers after it asked for another one, so the folder on screen is always the
///         one it asked for last. A listing is answered whole, however many entries the folder holds;
///         the bridge delivers it in parts and the page shows it in pages.
///     </para>
///     <para>
///         A Windows dialog is no substitute: it opens behind Big Picture and cannot be driven with a
///         controller.
///     </para>
/// </remarks>
public static partial class SteamFilePickerSurface
{
    /// <summary>The patch id the picker's commands are addressed to. No patch is installed for it.</summary>
    public const string PatchId = "steam-ui.file-picker";

    /// <summary>Shown when a path is not a full ordinary path to a folder or file.</summary>
    public const string NotAPathError = "That is not a full path.";

    /// <summary>Shown when a path names a device rather than a folder or file.</summary>
    public const string DevicePathError = "Device paths cannot be browsed.";

    /// <summary>Shown when a folder's drive is gone, such as a removed SD card.</summary>
    public const string DriveGoneError = "This drive is no longer available.";

    /// <summary>Shown when a folder is gone.</summary>
    public const string FolderGoneError = "This folder no longer exists.";

    /// <summary>Shown when a file is gone.</summary>
    public const string FileGoneError = "This file no longer exists.";

    /// <summary>Shown when a folder refuses the user.</summary>
    public const string AccessDeniedError = "This folder cannot be opened.";

    /// <summary>Shown when a file was chosen where a folder is wanted.</summary>
    public const string NotAFolderError = "Choose a folder, not a file.";

    /// <summary>Shown when a folder was chosen where a file is wanted.</summary>
    public const string NotAFileError = "Choose a file, not a folder.";

    /// <summary>Shown when a chosen file's type is not one the page asked for.</summary>
    public const string WrongTypeError = "That file type cannot be chosen here.";

    /// <summary>The exact command vocabulary the picker sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["listPlaces", "listFolder", "resolve"];

    // The user's folders in the order they are offered, by known-folder id: Windows answers where
    // each one is, which a redirected folder moves away from the profile.
    private static readonly (string Name, Guid Id)[] KnownFolders =
    [
        ("Desktop", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        ("Documents", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        ("Downloads", new Guid("374DE290-123F-4565-9164-39C4925E467B")),
        ("Pictures", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        ("Music", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43")),
        ("Videos", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"))
    ];

    // Win32 errors that mean the drive, not the folder, went away.
    private static readonly int[] DriveGoneErrors =
    [
        15, // ERROR_INVALID_DRIVE
        21, // ERROR_NOT_READY
        53, // ERROR_BAD_NETPATH
        64, // ERROR_NETNAME_DELETED
        1005, // ERROR_UNRECOGNIZED_VOLUME
        1112, // ERROR_NO_MEDIA_IN_DRIVE
        1167 // ERROR_DEVICE_NOT_CONNECTED
    ];

    /// <summary>The drives that are ready, then the user's known folders.</summary>
    /// <remarks>
    ///     Asked again each time the picker opens or refreshes, so a card inserted while Steam runs is
    ///     offered and one that was pulled is not. Never throws: a drive that stops answering while it
    ///     is asked is simply not offered.
    /// </remarks>
    /// <returns>The places.</returns>
    public static SteamFilePlaces ListPlaces()
    {
        List<SteamFilePlace> places = [];
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            drives = [];
        }

        foreach (var drive in drives)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // A drive that stops answering is simply not offered.
            }
        }

        foreach (var (name, id) in KnownFolders)
        {
            var path = KnownFolderPath(id);
            if (path is { Length: > 0 } && Directory.Exists(path))
            {
                places.Add(new SteamFilePlace(path, name, "folder", string.Empty));
            }
        }

        return new SteamFilePlaces(places);
    }

    /// <summary>Lists one folder's subfolders and the files that match.</summary>
    /// <remarks>
    ///     A path in the <c>\\?\X:\</c> or <c>\\?\UNC\server\share\</c> form is listed as its ordinary
    ///     form, so the listing and every path in it read the way the user knows them. A path that is
    ///     not a full ordinary path, or that names a device, is refused with an error and never opened.
    /// </remarks>
    /// <param name="path">The folder.</param>
    /// <param name="extensions">
    ///     File extensions with their dot; empty lists folders only, and <c>.*</c> includes every file
    ///     type.
    /// </param>
    /// <param name="cancellationToken">Stops the enumeration once the request was given up on.</param>
    /// <returns>The listing, with an error when the folder cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The request was cancelled while the folder was listed.</exception>
    public static SteamFileListing ListFolder(
        string path,
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(extensions);
        if (!TryNormalize(path, out var full, out var refusal))
        {
            return new SteamFileListing(path, string.Empty, [], refusal);
        }

        var parent = Directory.GetParent(full)?.FullName ?? string.Empty;
        // Not IgnoreInaccessible: without recursion the only thing opened is the folder itself, and
        // that option turns a folder the user may not open into an empty one instead of a refusal.
        EnumerationOptions options = new()
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false
        };
        try
        {
            DirectoryInfo directory = new(full);
            if (!directory.Exists)
            {
                return new SteamFileListing(full, parent, [], File.Exists(full) ? NotAFolderError : GoneError(full));
            }

            var folders = directory.EnumerateDirectories("*", options)
                .Select(entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new SteamFileEntry(entry.Name, entry.FullName, true, IsLink(entry));
                })
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var all = extensions.Contains(".*");
            var files = extensions.Count == 0
                ? []
                : directory.EnumerateFiles("*", options)
                    .Where(entry =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return all || Matches(entry.Extension, extensions);
                    })
                    .Select(entry => new SteamFileEntry(entry.Name, entry.FullName, false, IsLink(entry)))
                    .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            return new SteamFileListing(full, parent, [.. folders, .. files], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new SteamFileListing(full, parent, [], ReadError(full, ex));
        }
    }

    /// <summary>Checks that a path the user chose can still be returned as the kind the page asked for.</summary>
    /// <remarks>
    ///     The page lists a folder and then asks this before it answers, because the item can be gone
    ///     or changed by then: a file deleted, a card pulled, a folder replaced by a file of the same
    ///     name. Nothing is opened; the attributes and the name are all that are read.
    /// </remarks>
    /// <param name="path">The path the user chose.</param>
    /// <param name="kind">What the page accepts.</param>
    /// <param name="extensions">
    ///     The extensions the page offered, as for <see cref="ListFolder" />; a file must match one.
    /// </param>
    /// <returns>The full ordinary path and whether it is a folder, or the path as asked with an error.</returns>
    public static SteamFileSelection Resolve(string path, SteamFileKind kind, IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(extensions);
        if (!TryNormalize(path, out var full, out var refusal))
        {
            return new SteamFileSelection(path, false, refusal);
        }

        try
        {
            if (Directory.Exists(full))
            {
                return kind == SteamFileKind.File
                    ? new SteamFileSelection(full, true, NotAFileError)
                    : new SteamFileSelection(full, true, null);
            }

            if (File.Exists(full))
            {
                if (kind == SteamFileKind.Folder)
                {
                    return new SteamFileSelection(full, false, NotAFolderError);
                }

                return extensions.Contains(".*") || Matches(Path.GetExtension(full), extensions)
                    ? new SteamFileSelection(full, false, null)
                    : new SteamFileSelection(full, false, WrongTypeError);
            }

            if (kind == SteamFileKind.Folder)
            {
                return new SteamFileSelection(full, true, GoneError(full));
            }

            var gone = GoneError(full) == DriveGoneError ? DriveGoneError : FileGoneError;
            return new SteamFileSelection(full, false, gone);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new SteamFileSelection(full, false, ReadError(full, ex));
        }
    }

    /// <summary>Reads the exact <c>listFolder</c> payload: a path and a list of extensions.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="request">The path and extensions, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadListFolder(JsonElement payload,
        out (string Path, IReadOnlyList<string> Extensions) request)
    {
        request = (string.Empty, []);
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !TryReadExtensions(payload, out var extensions))
        {
            return false;
        }

        request = (path, extensions);
        return true;
    }

    /// <summary>Reads the exact <c>resolve</c> payload: a path, the kind wanted and the extensions offered.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="request">The path, kind and extensions, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadResolve(JsonElement payload,
        out (string Path, SteamFileKind Kind, IReadOnlyList<string> Extensions) request)
    {
        request = (string.Empty, SteamFileKind.File, []);
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.TryReadNonBlankString(payload, "kind", out var kindText)
            || !TryReadExtensions(payload, out var extensions))
        {
            return false;
        }

        SteamFileKind kind;
        switch (kindText)
        {
            case "file":
                kind = SteamFileKind.File;
                break;
            case "folder":
                kind = SteamFileKind.Folder;
                break;
            case "any":
                kind = SteamFileKind.Any;
                break;
            default:
                return false;
        }

        request = (path, kind, extensions);
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
                // Off the bridge pump: enumerating drives blocks on a disconnected network share, and
                // the pump would hold every later request and cancel behind it.
                SteamUiModuleBuilder.Command(PatchId, "listPlaces", cancellationToken => !enabled()
                    ? Task.FromResult(SteamUiCommandResult.Refused)
                    : OnWorker(
                        () => new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                            ListPlaces(), SteamSurfaceJsonContext.Default.SteamFilePlaces)),
                        cancellationToken)),
                SteamUiModuleBuilder.Command<(string Path, IReadOnlyList<string> Extensions)>(
                    PatchId,
                    "listFolder",
                    TryReadListFolder,
                    (request, cancellationToken) => !enabled()
                        ? Task.FromResult(SteamUiCommandResult.Refused)
                        : OnWorker(
                            () => new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                                ListFolder(request.Path, request.Extensions, cancellationToken),
                                SteamSurfaceJsonContext.Default.SteamFileListing)),
                            cancellationToken),
                    "The folder request is invalid."),
                SteamUiModuleBuilder.Command<(string Path, SteamFileKind Kind, IReadOnlyList<string> Extensions)>(
                    PatchId,
                    "resolve",
                    TryReadResolve,
                    (request, cancellationToken) => !enabled()
                        ? Task.FromResult(SteamUiCommandResult.Refused)
                        : OnWorker(
                            () => new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                                Resolve(request.Path, request.Kind, request.Extensions),
                                SteamSurfaceJsonContext.Default.SteamFileSelection)),
                            cancellationToken),
                    "The selection request is invalid.")
            ]);
    }

    /// <summary>
    ///     Turns a path into the full ordinary form the picker lists, or says why it will not be opened.
    /// </summary>
    /// <remarks>
    ///     Accepted: <c>X:\folder</c>, <c>\\server\share\folder</c>, and the same behind <c>\\?\</c> or
    ///     <c>\\?\UNC\</c>. Refused: a relative, drive-relative or rooted-only path, any other
    ///     <c>\\?\</c> or <c>\\.\</c> path (<c>GLOBALROOT</c>, a volume GUID, a pipe, a physical drive),
    ///     a legacy device name such as <c>CON</c> in any segment, and control characters. Forward
    ///     slashes are accepted as separators. <c>..</c> and <c>.</c> segments are resolved.
    /// </remarks>
    /// <param name="path">The path as given.</param>
    /// <param name="full">The full ordinary path, when this returns true.</param>
    /// <param name="error">Why the path is refused, when this returns false.</param>
    /// <returns>Whether the path may be opened.</returns>
    public static bool TryNormalize(string path, out string full, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);
        full = path;
        error = null;
        if (path.Length == 0 || path.Any(char.IsControl))
        {
            error = NotAPathError;
            return false;
        }

        var plain = path.Replace('/', '\\');
        if (plain.StartsWith(@"\\?\", StringComparison.Ordinal) || plain.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            if (!TryPlainPath(plain, out plain))
            {
                error = DevicePathError;
                return false;
            }
        }
        else if (plain.StartsWith(@"\\", StringComparison.Ordinal) && plain.Length > 2 && plain[2] is '?' or '.')
        {
            // \\?.\ and \\.?\ are not UNC servers: Windows treats them as device prefixes too.
            error = DevicePathError;
            return false;
        }

        // A full path is X:\ or \\server\share; \\server alone is not a place, whatever GetFullPath
        // makes of it.
        if (!Path.IsPathFullyQualified(plain) || (plain.StartsWith(@"\\", StringComparison.Ordinal) && !HasUncShare(plain)))
        {
            error = NotAPathError;
            return false;
        }

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plain));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = NotAPathError;
            return false;
        }

        // A device prefix GetFullPath left in place, or a legacy device name (CON, NUL, COM1) in
        // any segment, which Windows opens as the device whatever folder it sits in.
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.StartsWith(@"\\?\", StringComparison.Ordinal)
            || full.Split('\\').Any(IsDeviceName))
        {
            full = path;
            error = DevicePathError;
            return false;
        }

        if (!Path.IsPathFullyQualified(full))
        {
            full = path;
            error = NotAPathError;
            return false;
        }

        return true;
    }

    // \\server\share, with a share: at least one character between the third and fourth separator.
    private static bool HasUncShare(string path)
    {
        var share = path.IndexOf('\\', 2);
        return share > 2 && share + 1 < path.Length && path[share + 1] != '\\';
    }

    // CON, PRN, AUX, NUL, COM0-9, LPT0-9 and the superscript COM/LPT digits, with or without an
    // extension, as Windows reserves them in every folder.
    private static bool IsDeviceName(string segment)
    {
        var name = segment.AsSpan().TrimEnd(" .");
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot].TrimEnd(' ');
        }

        if (name.Length is 3 && (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return name.Length is 4
               && (name[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                   || name[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
               && (char.IsAsciiDigit(name[3]) || name[3] is '\u00B9' or '\u00B2' or '\u00B3');
    }

    // The ordinary form of a \\?\ path: \\?\D:\Games is D:\Games and \\?\UNC\server\share is
    // \\server\share. Any other extended or device path has no ordinary form and is refused.
    private static bool TryPlainPath(string path, out string plain)
    {
        const string extended = @"\\?\";
        const string extendedUnc = @"\\?\UNC\";
        plain = path;
        if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
        {
            plain = @"\\" + path[extendedUnc.Length..];
            return plain.Length > 2 && plain[2] is not ('\\' or '?' or '.');
        }

        if (path.StartsWith(extended, StringComparison.Ordinal)
            && path.Length >= extended.Length + 3
            && char.IsAsciiLetter(path[extended.Length])
            && path[extended.Length + 1] == ':'
            && path[extended.Length + 2] == '\\')
        {
            plain = path[extended.Length..];
            return true;
        }

        return false;
    }

    // Runs a listing on a worker and stops waiting the moment the request is cancelled: a drive that
    // stops answering can hold its worker, never the caller. What the worker answers after that goes
    // nowhere.
    private static Task<SteamUiCommandResult> OnWorker(
        Func<SteamUiCommandResult> list,
        CancellationToken cancellationToken)
    {
        return Task.Run(list, cancellationToken).WaitAsync(cancellationToken);
    }

    private static bool TryReadExtensions(JsonElement payload, out IReadOnlyList<string> extensions)
    {
        extensions = [];
        if (!payload.TryGetProperty("extensions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> read = [];
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 1 } extension
                || !extension.StartsWith('.')
                || (extension != ".*" && extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return false;
            }

            read.Add(extension);
        }

        extensions = read;
        return true;
    }

    private static bool Matches(string extension, IReadOnlyCollection<string> extensions)
    {
        return extension.Length > 0 && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    // A junction or symbolic link, by the target Windows records for it. A cloud placeholder or a
    // deduplicated file is a reparse point too but not a link, and is listed as what it is.
    private static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Whether the folder or its drive is what went away.
    private static string GoneError(string full)
    {
        var root = Path.GetPathRoot(full);
        return root is { Length: > 0 } && !Directory.Exists(root) ? DriveGoneError : FolderGoneError;
    }

    private static string ReadError(string full, Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException or SecurityException => AccessDeniedError,
            DriveNotFoundException => DriveGoneError,
            DirectoryNotFoundException => GoneError(full),
            FileNotFoundException => FileGoneError,
            IOException io when DriveGoneErrors.Contains(io.HResult & 0xFFFF) => DriveGoneError,
            _ => $"This folder cannot be read. {ex.Message}"
        };
    }

    // Where Windows keeps a known folder, wherever it was redirected; null when Windows cannot say,
    // as for a folder the profile does not have.
    private static string? KnownFolderPath(Guid id)
    {
        var result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var buffer);
        try
        {
            var path = result == 0 ? Marshal.PtrToStringUni(buffer) : null;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            // The buffer is the caller's to free whether or not the call succeeded.
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);

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
