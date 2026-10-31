using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamUiBrowseCommandsTests
{
    private const string PatchId = "host.store";

    [Fact]
    public void TheVocabularyIsTheOneTheRendererSends()
    {
        Assert.Equal("setTab", SteamUiBrowseCommands.SetTab);
        Assert.Equal("browse", SteamUiBrowseCommands.Browse);
        Assert.Equal("more", SteamUiBrowseCommands.More);
        Assert.Equal("open", SteamUiBrowseCommands.Open);
        Assert.Equal("closeDetail", SteamUiBrowseCommands.CloseDetail);
        Assert.Equal("dismiss", SteamUiBrowseCommands.Dismiss);
    }

    [Fact]
    public void OnlyTheBoundStepsAreDeclaredInVocabularyOrder()
    {
        var commands = SteamUiModuleBuilder.BrowseCommands(PatchId, new SteamUiBrowseBindings
        {
            Subject = "store",
            More = Answer,
            Dismiss = Answer
        });

        Assert.Equal(["more", "dismiss"], commands.Select(command => command.Command));
        Assert.All(commands, command => Assert.Equal(PatchId, command.PatchId));
    }

    [Fact]
    public void TheQueryFollowsTheTabCommand()
    {
        var commands = SteamUiModuleBuilder.BrowseCommands(
            PatchId,
            new SteamUiBrowseBindings
            {
                Subject = "store",
                Tabs = ["browse", "installed"],
                SetTab = (_, _) => Task.FromResult(SteamUiCommandResult.Applied),
                More = Answer,
                Open = (_, _) => Task.FromResult(SteamUiCommandResult.Applied),
                CloseDetail = Answer
            },
            (JsonElement payload, out string search) =>
                SteamUiPayload.TryReadOnlyOptionalString(payload, "search", out search),
            (_, _) => Task.FromResult(SteamUiCommandResult.Applied));

        Assert.Equal(["setTab", "browse", "more", "open", "closeDetail"], commands.Select(command => command.Command));
    }

    [Fact]
    public async Task ATabMustBeOneOfThePagesAndNothingElse()
    {
        List<string> shown = [];
        var set = Set(new SteamUiBrowseBindings
        {
            Subject = "themes",
            Tabs = ["browse", "installed"],
            SetTab = (tab, _) =>
            {
                shown.Add(tab);
                return Task.FromResult(SteamUiCommandResult.Applied);
            }
        });

        Assert.True((await SurfaceDispatch.DispatchAsync(set, PatchId, "setTab", """{"tab":"installed"}""")).Succeeded);
        var unknown = await SurfaceDispatch.DispatchAsync(set, PatchId, "setTab", """{"tab":"profiles"}""");
        var extra = await SurfaceDispatch.DispatchAsync(set, PatchId, "setTab", """{"tab":"browse","x":1}""");

        Assert.Equal(["installed"], shown);
        Assert.False(unknown.Succeeded);
        Assert.Equal("The themes tab payload is invalid.", unknown.Error);
        Assert.False(extra.Succeeded);
    }

    [Fact]
    public async Task AnItemIsOpenedByANonBlankIdAlone()
    {
        List<string> opened = [];
        var set = Set(new SteamUiBrowseBindings
        {
            Subject = "animations",
            Open = (id, _) =>
            {
                opened.Add(id);
                return Task.FromResult(SteamUiCommandResult.Applied);
            }
        });

        Assert.True((await SurfaceDispatch.DispatchAsync(set, PatchId, "open", """{"id":"boot-1"}""")).Succeeded);
        var blank = await SurfaceDispatch.DispatchAsync(set, PatchId, "open", """{"id":" "}""");

        Assert.Equal(["boot-1"], opened);
        Assert.Equal("The animations item payload is invalid.", blank.Error);
    }

    [Fact]
    public async Task TheQueryIsReadByThePagesOwnReader()
    {
        List<string> searched = [];
        SteamUiModuleSet set = new(
        [
            new SteamUiModule("store", commands: SteamUiModuleBuilder.BrowseCommands(
                PatchId,
                new SteamUiBrowseBindings { Subject = "sound pack" },
                (JsonElement payload, out string search) =>
                    SteamUiPayload.TryReadOnlyOptionalString(payload, "search", out search),
                (search, _) =>
                {
                    searched.Add(search);
                    return Task.FromResult(SteamUiCommandResult.Applied);
                }))
        ]);

        Assert.True((await SurfaceDispatch.DispatchAsync(set, PatchId, "browse", """{"search":"chime"}""")).Succeeded);
        var paged = await SurfaceDispatch.DispatchAsync(set, PatchId, "browse", """{"page":2,"search":""}""");

        Assert.Equal(["chime"], searched);
        Assert.Equal("The sound pack browse payload is invalid.", paged.Error);
    }

    [Fact]
    public void ATabCommandWithoutTabsIsADeclarationError()
    {
        Assert.Throws<ArgumentException>(() => SteamUiModuleBuilder.BrowseCommands(PatchId, new SteamUiBrowseBindings
        {
            Subject = "store",
            SetTab = (_, _) => Task.FromResult(SteamUiCommandResult.Applied)
        }));
    }

    private static Task<SteamUiCommandResult> Answer(CancellationToken cancellationToken)
    {
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private static SteamUiModuleSet Set(SteamUiBrowseBindings bindings)
    {
        return new SteamUiModuleSet([new SteamUiModule("store", commands: SteamUiModuleBuilder.BrowseCommands(PatchId, bindings))]);
    }
}
