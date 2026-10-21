using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamFilePickerSurfaceTests
{
    [Fact]
    public void AllFilesFilterIncludesUnknownExtensionsWhileEmptyStillListsOnlyFolders()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "child"));
        File.WriteAllText(Path.Combine(directory.Root, "bios.bin"), "");
        File.WriteAllText(Path.Combine(directory.Root, "extensionless"), "");
        Assert.All(SteamFilePickerSurface.ListFolder(directory.Root, []).Entries, item => Assert.True(item.Folder));
        var all = SteamFilePickerSurface.ListFolder(directory.Root, [".*"]);
        Assert.Equal(3, all.Entries.Count);
        Assert.Contains(all.Entries, item => item.Name == "extensionless" && !item.Folder);
        Assert.Single(SteamFilePickerSurface.ListFolder(directory.Root, [".BIN"]).Entries, item => !item.Folder);
    }

    [Fact]
    public void ListingPutsSortedFoldersBeforeMatchingFilesAndSkipsHiddenEntries()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "zeta"));
        Directory.CreateDirectory(Path.Combine(directory.Root, "Alpha"));
        var hidden = Directory.CreateDirectory(Path.Combine(directory.Root, "hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(directory.Root, "game.exe"), "");
        File.WriteAllText(Path.Combine(directory.Root, "notes.txt"), "");
        var system = Path.Combine(directory.Root, "desktop.ini");
        File.WriteAllText(system, "");
        File.SetAttributes(system, FileAttributes.System | FileAttributes.Hidden);

        var listing = SteamFilePickerSurface.ListFolder(directory.Root, [".exe", ".ini"]);

        Assert.Null(listing.Error);
        Assert.Equal(directory.Root, listing.Path);
        Assert.Equal(Path.GetDirectoryName(directory.Root), listing.Parent);
        Assert.Equal(["Alpha", "zeta", "game.exe"], listing.Entries.Select(item => item.Name));
        Assert.Equal([true, true, false], listing.Entries.Select(item => item.Folder));
    }

    [Fact]
    public void ExtendedAndSlashedPathsListAsTheirOrdinaryForm()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "roms"));
        var expected = Path.Combine(directory.Root, "roms");

        Assert.Equal(expected, SteamFilePickerSurface.ListFolder(@"\\?\" + expected, []).Path);
        Assert.Equal(expected, SteamFilePickerSurface.ListFolder(expected.Replace('\\', '/'), []).Path);
        Assert.Equal(expected, SteamFilePickerSurface.ListFolder(expected + @"\", []).Path);
        Assert.Equal(expected, SteamFilePickerSurface.ListFolder(Path.Combine(directory.Root, "other", "..", "roms"), []).Path);
        Assert.Equal(expected, SteamFilePickerSurface.ListFolder(expected, []).Path);
        Assert.Empty(SteamFilePickerSurface.ListFolder(expected, []).Entries);
    }

    [Theory]
    [InlineData(@"\\.\C:\")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\pipe\steam")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\")]
    [InlineData(@"\\?\Volume{6d4a0a2e-0000-0000-0000-100000000000}\")]
    [InlineData(@"//./C:/")]
    [InlineData(@"\\?\UNC\?\share")]
    [InlineData(@"C:\CON")]
    [InlineData(@"C:\Games\NUL.txt")]
    public void DevicePathsAreRefusedBeforeAnythingIsOpened(string path)
    {
        Assert.False(SteamFilePickerSurface.TryNormalize(path, out _, out var error));
        Assert.Equal(SteamFilePickerSurface.DevicePathError, error);
        var listing = SteamFilePickerSurface.ListFolder(path, [".*"]);
        Assert.Equal(SteamFilePickerSurface.DevicePathError, listing.Error);
        Assert.Empty(listing.Entries);
        Assert.Equal(SteamFilePickerSurface.DevicePathError,
            SteamFilePickerSurface.Resolve(path, SteamFileKind.Any, [".*"]).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Games")]
    [InlineData(@"C:Games")]
    [InlineData(@"\Games")]
    [InlineData(@"..\Games")]
    [InlineData("C:\\Games\u0000")]
    [InlineData(@"\\server")]
    public void PartialPathsAreRefusedInsteadOfResolvedAgainstTheWorkingDirectory(string path)
    {
        Assert.False(SteamFilePickerSurface.TryNormalize(path, out _, out var error));
        Assert.Equal(SteamFilePickerSurface.NotAPathError, error);
        Assert.Equal(SteamFilePickerSurface.NotAPathError, SteamFilePickerSurface.ListFolder(path, []).Error);
    }

    [Theory]
    [InlineData(@"C:\Games", @"C:\Games")]
    [InlineData(@"\\?\C:\Games\", @"C:\Games")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\?\C:\", @"C:\")]
    [InlineData(@"\\?\UNC\server\share\roms", @"\\server\share\roms")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"c:/games/./roms", @"c:\games\roms")]
    public void FullPathsNormalizeToTheirOrdinaryForm(string path, string expected)
    {
        Assert.True(SteamFilePickerSurface.TryNormalize(path, out var full, out var error));
        Assert.Null(error);
        Assert.Equal(expected, full);
    }

    [Fact]
    public void DownloadsPlaceIsTheKnownFolderWindowsReportsNotTheProfileGuess()
    {
        var expected = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
        Assert.False(string.IsNullOrEmpty(expected), "Windows reports a Downloads folder for the test user");

        var places = SteamFilePickerSurface.ListPlaces().Places;

        var downloads = Assert.Single(places, place => place.Name == "Downloads");
        Assert.Equal(expected, downloads.Path);
        Assert.Equal("folder", downloads.Kind);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Assert.Single(places, place => place.Name == "Documents").Path);
        Assert.All(places.TakeWhile(place => place.Kind == "drive"), place => Assert.True(Directory.Exists(place.Path)));
        Assert.Contains(places, place => place.Kind == "drive");
        Assert.DoesNotContain(places, place => place.Kind == "folder" && !Directory.Exists(place.Path));
    }

    [Fact]
    public void LargeFolderIsAnsweredWholeInNameOrder()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 3000; index++)
        {
            File.WriteAllText(Path.Combine(directory.Root, $"rom-{index:0000}.chd"), "");
        }

        for (var index = 0; index < 50; index++)
        {
            Directory.CreateDirectory(Path.Combine(directory.Root, $"set-{index:00}"));
        }

        var listing = SteamFilePickerSurface.ListFolder(directory.Root, [".chd"]);

        Assert.Null(listing.Error);
        Assert.Equal(3050, listing.Entries.Count);
        Assert.Equal(50, listing.Entries.TakeWhile(item => item.Folder).Count());
        Assert.Equal("rom-0000.chd", listing.Entries[50].Name);
        Assert.Equal("rom-2999.chd", listing.Entries[^1].Name);
        // The bridge delivers past 256 KiB characters in parts; a listing this size crosses that
        // and is split there, never cut short here.
        var json = JsonSerializer.Serialize(listing, SteamSurfaceJsonContext.Default.SteamFileListing);
        Assert.True(json.Length > 256 * 1024, "a large listing crosses the part size and is split, not capped");
    }

    [Fact]
    public void InaccessibleFolderReportsAccessDeniedWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        var locked = Directory.CreateDirectory(Path.Combine(directory.Root, "locked"));
        File.WriteAllText(Path.Combine(locked.FullName, "secret.bin"), "");
        var identity = WindowsIdentity.GetCurrent().User!;
        var security = locked.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ListDirectory, AccessControlType.Deny));
        locked.SetAccessControl(security);
        try
        {
            var listing = SteamFilePickerSurface.ListFolder(locked.FullName, [".*"]);

            Assert.Equal(SteamFilePickerSurface.AccessDeniedError, listing.Error);
            Assert.Empty(listing.Entries);
            Assert.Equal(locked.FullName, listing.Path);
            Assert.Equal(directory.Root, listing.Parent);

            // The locked folder is still listed by its parent, so the user sees it and gets the
            // refusal on entering it rather than a folder that silently vanished.
            Assert.Contains(SteamFilePickerSurface.ListFolder(directory.Root, []).Entries, item => item.Name == "locked");
        }
        finally
        {
            security.RemoveAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ListDirectory, AccessControlType.Deny));
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void DisappearedFolderReportsThatItIsGoneAndKeepsItsParent()
    {
        using var directory = new TemporaryDirectory();
        var gone = Path.Combine(directory.Root, "gone");
        Directory.CreateDirectory(gone);
        Directory.Delete(gone);

        var listing = SteamFilePickerSurface.ListFolder(gone, [".*"]);

        Assert.Equal(SteamFilePickerSurface.FolderGoneError, listing.Error);
        Assert.Equal(directory.Root, listing.Parent);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void FolderOnAMissingDriveReportsTheDriveNotTheFolder()
    {
        var letter = Enumerable.Range('D', 23)
            .Select(code => (char)code)
            .First(code => !Directory.Exists($"{code}:\\"));

        var listing = SteamFilePickerSurface.ListFolder($@"{letter}:\DCIM\roms", [".*"]);

        Assert.Equal(SteamFilePickerSurface.DriveGoneError, listing.Error);
        Assert.Equal(SteamFilePickerSurface.DriveGoneError,
            SteamFilePickerSurface.Resolve($@"{letter}:\DCIM\roms\game.chd", SteamFileKind.File, [".chd"]).Error);
        Assert.Equal(SteamFilePickerSurface.DriveGoneError,
            SteamFilePickerSurface.Resolve($@"{letter}:\DCIM", SteamFileKind.Folder, []).Error);
    }

    [Fact]
    public void LinksAreListedAsLinksAndNotFollowedBelowTheFolder()
    {
        using var directory = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(directory.Root, "target"));
        File.WriteAllText(Path.Combine(target.FullName, "inside.bin"), "");
        var link = Path.Combine(directory.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need developer mode or a privilege this test runner may not have.
            return;
        }

        var listing = SteamFilePickerSurface.ListFolder(directory.Root, [".*"]);

        Assert.Equal(["link", "target"], listing.Entries.Select(item => item.Name));
        Assert.True(listing.Entries[0].Link);
        Assert.False(listing.Entries[1].Link);
        Assert.Equal(link, listing.Entries[0].Path);
        // Entering the link lists what it points to, under the path the user chose.
        var inside = SteamFilePickerSurface.ListFolder(link, [".*"]);
        Assert.Null(inside.Error);
        Assert.Equal(link, inside.Path);
        Assert.Equal(Path.Combine(link, "inside.bin"), Assert.Single(inside.Entries).Path);
    }

    [Fact]
    public void CancelledListingStopsAndIsNotAnswered()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(Path.Combine(directory.Root, $"set-{index:00}"));
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SteamFilePickerSurface.ListFolder(directory.Root, [".*"], cancelled.Token));
    }

    [Fact]
    public void FileSelectionIsRevalidatedAgainstKindTypeAndExistence()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "Game.EXE");
        File.WriteAllText(exe, "");
        var folder = Directory.CreateDirectory(Path.Combine(directory.Root, "saves")).FullName;

        var chosen = SteamFilePickerSurface.Resolve(@"\\?\" + exe, SteamFileKind.File, [".exe", ".bat"]);
        Assert.Null(chosen.Error);
        Assert.Equal(exe, chosen.Path);
        Assert.False(chosen.Folder);

        Assert.Equal(SteamFilePickerSurface.WrongTypeError,
            SteamFilePickerSurface.Resolve(exe, SteamFileKind.File, [".bat"]).Error);
        Assert.Null(SteamFilePickerSurface.Resolve(exe, SteamFileKind.File, [".*"]).Error);
        Assert.Null(SteamFilePickerSurface.Resolve(exe, SteamFileKind.Any, [".exe"]).Error);
        Assert.Equal(SteamFilePickerSurface.NotAFolderError,
            SteamFilePickerSurface.Resolve(exe, SteamFileKind.Folder, []).Error);
        Assert.Equal(SteamFilePickerSurface.NotAFileError,
            SteamFilePickerSurface.Resolve(folder, SteamFileKind.File, [".exe"]).Error);

        File.Delete(exe);
        var deleted = SteamFilePickerSurface.Resolve(exe, SteamFileKind.File, [".exe"]);
        Assert.Equal(SteamFilePickerSurface.FileGoneError, deleted.Error);
        Assert.Equal(exe, deleted.Path);
    }

    [Fact]
    public void FolderSelectionIsRevalidatedAndReturnsTheOrdinaryPath()
    {
        using var directory = new TemporaryDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(directory.Root, "roms")).FullName;

        var chosen = SteamFilePickerSurface.Resolve(folder + @"\", SteamFileKind.Folder, []);
        Assert.Null(chosen.Error);
        Assert.Equal(folder, chosen.Path);
        Assert.True(chosen.Folder);
        Assert.True(SteamFilePickerSurface.Resolve(folder, SteamFileKind.Any, [".*"]).Folder);

        Directory.Delete(folder);
        Assert.Equal(SteamFilePickerSurface.FolderGoneError,
            SteamFilePickerSurface.Resolve(folder, SteamFileKind.Folder, []).Error);
        Assert.Equal(SteamFilePickerSurface.FileGoneError,
            SteamFilePickerSurface.Resolve(Path.Combine(folder, "x.exe"), SteamFileKind.File, [".exe"]).Error);
    }

    [Fact]
    public void FolderReplacedByAFileOfTheSameNameIsRefusedAsAFolder()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "entry");
        Directory.CreateDirectory(path);
        Directory.Delete(path);
        File.WriteAllText(path, "");

        Assert.Equal(SteamFilePickerSurface.NotAFolderError, SteamFilePickerSurface.ListFolder(path, []).Error);
        Assert.Equal(SteamFilePickerSurface.NotAFolderError,
            SteamFilePickerSurface.Resolve(path, SteamFileKind.Folder, []).Error);
    }

    [Theory]
    [InlineData(".*", true)]
    [InlineData(".bin", true)]
    [InlineData(".*.bin", false)]
    [InlineData("exe", false)]
    public void FileFiltersAcceptOnlyTheExplicitAllFilesWildcard(string extension, bool accepted)
    {
        using var payload =
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = @"C:\", extensions = new[] { extension } }));
        Assert.Equal(accepted, SteamFilePickerSurface.TryReadListFolder(payload.RootElement, out _));
    }

    [Theory]
    [InlineData("""{"path":"C:\\Games\\a.exe","kind":"file","extensions":[".exe"]}""", true)]
    [InlineData("""{"path":"C:\\Games","kind":"folder","extensions":[]}""", true)]
    [InlineData("""{"path":"C:\\Games","kind":"any","extensions":[".*"]}""", true)]
    [InlineData("""{"path":"C:\\Games","kind":"files","extensions":[]}""", false)]
    [InlineData("""{"path":"C:\\Games","extensions":[]}""", false)]
    [InlineData("""{"path":"C:\\Games","kind":"file","extensions":[],"extra":1}""", false)]
    [InlineData("""{"path":"","kind":"file","extensions":[]}""", false)]
    public void ResolvePayloadIsExact(string json, bool accepted)
    {
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(accepted, SteamFilePickerSurface.TryReadResolve(payload.RootElement, out var request));
        if (accepted)
        {
            Assert.StartsWith(@"C:\Games", request.Path);
        }
    }

    [Fact]
    public void ModuleDeclaresEveryCommandThePageSends()
    {
        var module = SteamFilePickerSurface.Module(() => true);
        Assert.Equal(["listPlaces", "listFolder", "resolve"], SteamFilePickerSurface.Commands);
        Assert.Equal(SteamFilePickerSurface.Commands, module.Commands.Select(command => command.Command));
        Assert.All(module.Commands, command => Assert.Equal(SteamFilePickerSurface.PatchId, command.PatchId));
    }

    [Fact]
    public async Task DisabledModuleRefusesWithoutTouchingTheFileSystem()
    {
        var module = SteamFilePickerSurface.Module(() => false);
        var resolve = module.Commands.Single(command => command.Command == "resolve");
        var request = SurfaceDispatch.Request(
            SteamFilePickerSurface.PatchId,
            "resolve",
            """{"path":"C:\\","kind":"folder","extensions":[]}""");

        var outcome = await resolve.Handle(request, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SteamUiCommandResult.Refused.Error, outcome.Error);
    }

    [Fact]
    public async Task ResolveCommandAnswersTheValidatedSelectionOffThePump()
    {
        using var directory = new TemporaryDirectory();
        var exe = Path.Combine(directory.Root, "run.bat");
        File.WriteAllText(exe, "");
        var module = SteamFilePickerSurface.Module(() => true);
        var resolve = module.Commands.Single(command => command.Command == "resolve");
        var request = SurfaceDispatch.Request(
            SteamFilePickerSurface.PatchId,
            "resolve",
            JsonSerializer.Serialize(new
            {
                path = @"\\?\" + exe,
                kind = "file",
                extensions = new[] { ".exe", ".bat" }
            }));

        var outcome = await resolve.Handle(request, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var answer = outcome.Payload!.Value;
        Assert.Equal(exe, answer.GetProperty("path").GetString());
        Assert.False(answer.GetProperty("folder").GetBoolean());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("error").ValueKind);
    }

    private static string? KnownFolder(Guid id)
    {
        var result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var buffer);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
