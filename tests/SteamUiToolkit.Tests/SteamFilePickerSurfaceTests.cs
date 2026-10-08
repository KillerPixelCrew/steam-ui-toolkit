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
}
