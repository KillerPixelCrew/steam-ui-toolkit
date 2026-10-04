namespace SteamUiToolkit.Tests;

/// <summary>One-shot calls into Steam's client API: script shape, reply parsing and refusals.</summary>
public sealed class SteamClientTests
{
    // ---- Shared write replies ----

    [Fact]
    public void WriteReplyDistinguishesNotSentUnknownRefusedAndApplied()
    {
        var notSent = SteamClientScript.ParseWrite(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.NotSent, "port closed"));
        Assert.Equal(SteamClientWriteOutcome.NotSent, notSent.Outcome);
        Assert.False(notSent.Succeeded);
        Assert.Equal("port closed", notSent.Error);

        var closed = SteamClientScript.ParseWrite(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.Closed, PersistentSteamUiTransport.DefaultClosedReason));
        Assert.Equal(SteamClientWriteOutcome.NotSent, closed.Outcome);

        var unknown = SteamClientScript.ParseWrite(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.Unanswered, "timed out"));
        Assert.Equal(SteamClientWriteOutcome.Unknown, unknown.Outcome);

        var refused = SteamClientScript.ParseWrite(FakeSteamUiTransport.Answer("""{"ok":false,"err":"bad id"}"""));
        Assert.Equal(SteamClientWriteOutcome.Rejected, refused.Outcome);
        Assert.Equal("bad id", refused.Error);

        var thrown = SteamClientScript.ParseWrite(FakeSteamUiTransport.Answer(null, "Steam UI JavaScript exception"));
        Assert.Equal(SteamClientWriteOutcome.Rejected, thrown.Outcome);

        var applied = SteamClientScript.ParseWrite(FakeSteamUiTransport.Answer("""{"ok":true}"""));
        Assert.True(applied.Succeeded);
        Assert.Null(applied.Error);
    }

    [Theory]
    [InlineData(null, SteamClientWriteOutcome.Unknown)]
    [InlineData("not json", SteamClientWriteOutcome.Unknown)]
    [InlineData("[1]", SteamClientWriteOutcome.Rejected)]
    public void WriteReplyWithoutAnOkObjectIsNotApplied(string? value, SteamClientWriteOutcome expected)
    {
        // A script that ran and left no readable answer may have changed something, so it is unknown,
        // never a refusal a caller could take as "nothing happened".
        var result = SteamClientScript.ParseWrite(FakeSteamUiTransport.Answer(value));

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void ARefusalNamesSteamsResultCodeWhenItThrewOne()
    {
        var refused = SteamClientScript.ParseWrite(FakeSteamUiTransport.Answer("""{"ok":false,"result":8}"""));

        Assert.Equal("EResult 8", refused.Error);
    }

    // ---- SteamApps ----

    [Theory]
    [InlineData(440L, 440u)]
    [InlineData(-1L, 4294967295u)]
    [InlineData(-2147483648L, 2147483648u)]
    public void StoredAppIdsNormalizeToUnsigned(long stored, uint expected)
    {
        Assert.Equal(expected, SteamApps.NormalizeAppId(stored));
    }

    [Theory]
    [InlineData(440u, false)]
    [InlineData(2147483647u, false)]
    [InlineData(2147483648u, true)]
    [InlineData(4294967295u, true)]
    public void ShortcutIdsStartAtTheHighBit(uint appId, bool expected)
    {
        Assert.Equal(expected, SteamApps.IsShortcutAppId(appId));
    }

    [Fact]
    public void AppDetailsReplyCarriesEveryField()
    {
        var result = SteamApps.ParseDetails(FakeSteamUiTransport.Answer(
            """
            {"ok":true,"launch":"-novid","exe":"\"C:\\G\\g.exe\"","args":"-x","dir":"\"C:\\G\\\"","install":"D:\\Lib\\G"}
            """));

        Assert.True(result.Succeeded);
        Assert.Equal(
            new SteamAppDetails("-novid", "\"C:\\G\\g.exe\"", "-x", "\"C:\\G\\\"", "D:\\Lib\\G"),
            result.Value);
    }

    [Fact]
    public void AppDetailsRefusalKeepsSteamsReason()
    {
        var result = SteamApps.ParseDetails(FakeSteamUiTransport.Answer("""{"ok":false,"err":"Steam has no details"}"""));

        Assert.Equal(SteamUiDispatch.Answered, result.Dispatch);
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Equal("Steam has no details", result.Error);
    }

    [Fact]
    public void AddShortcutReplyCarriesTheConfirmedId()
    {
        var result = SteamApps.ParseAddShortcut(
            FakeSteamUiTransport.Answer("""{"ok":true,"value":"2147483650","confirmed":true,"mismatch":""}"""));

        Assert.True(result.Succeeded);
        Assert.True(result.Confirmed);
        Assert.Equal(2147483650u, result.AppId);
        Assert.Null(result.Error);
        Assert.Null(result.Mismatch);
    }

    [Fact]
    public void AnAddTheLibraryDidNotConfirmKeepsItsIdAndItsReason()
    {
        // The entry may exist; the caller records it as unconfirmed and never writes to the id.
        var result = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer(
            """{"ok":true,"value":"2147483650","confirmed":false,"err":"Steam reported no new entry after creating this shortcut."}"""));

        Assert.True(result.Succeeded);
        Assert.False(result.Confirmed);
        Assert.Equal(2147483650u, result.AppId);
        Assert.Equal("Steam reported no new entry after creating this shortcut.", result.Error);
    }

    [Fact]
    public void AForeignEntryAnAddWithoutAnIdDidNotAdoptIsUnknown()
    {
        var result = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer(
            """{"ok":false,"unconfirmed":true,"err":"left alone"}"""));

        Assert.Equal(SteamClientWriteOutcome.Unknown, result.Outcome);
        Assert.Equal(0u, result.AppId);
        Assert.False(result.Confirmed);
        Assert.Equal("left alone", result.Error);
    }

    [Fact]
    public void AnAddWithoutAnIdAdoptsOnlyTheEntryItAskedFor()
    {
        var script = SteamApps.AddShortcutScript("Game", "\"C:\\G\\g.exe\"", "C:\\G", "");

        Assert.Contains("if(!returned&&gained.length===1){", script, StringComparison.Ordinal);
        Assert.Contains("toLowerCase()!==exe.toLowerCase()||gn!==name", script, StringComparison.Ordinal);
        Assert.Contains("unconfirmed:true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldThatDidNotReadBackIsReportedWithAConfirmedAdd()
    {
        var result = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer(
            """{"ok":true,"value":"2147483650","confirmed":true,"mismatch":"Steam holds a different name than was written."}"""));

        Assert.True(result.Confirmed);
        Assert.Equal("Steam holds a different name than was written.", result.Mismatch);
    }

    [Fact]
    public void AShortcutListIsReadWhole()
    {
        var result = SteamApps.ParseShortcuts(FakeSteamUiTransport.Answer(
            """{"ok":true,"shortcuts":[{"id":"2147483650","name":"Game","exe":"\"C:\\G\\g.exe\"","dir":"C:\\G","args":"-x"}]}"""));

        Assert.True(result.Succeeded);
        var shortcut = Assert.Single(result.Value!);
        Assert.Equal(new SteamShortcut(2147483650u, "Game", "\"C:\\G\\g.exe\"", "C:\\G", "-x"), shortcut);
    }

    [Fact]
    public void AnEmptyLibraryIsNotAFailedRead()
    {
        var empty = SteamApps.ParseShortcuts(FakeSteamUiTransport.Answer("""{"ok":true,"shortcuts":[]}"""));
        var failed = SteamApps.ParseShortcuts(
            FakeSteamUiTransport.Answer("""{"ok":false,"err":"Steam did not return the details for shortcut 2147483650."}"""));
        var notSent = SteamApps.ParseShortcuts(FakeSteamUiTransport.Unanswered(SteamUiDispatch.NotSent, "port closed"));

        Assert.True(empty.Succeeded);
        Assert.Empty(empty.Value!);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Value);
        Assert.Contains("2147483650", failed.Error, StringComparison.Ordinal);
        Assert.Equal(SteamUiDispatch.NotSent, notSent.Dispatch);
        Assert.Null(notSent.Value);
    }

    [Fact]
    public void AddShortcutRefusesAnIdOutsideTheShortcutRange()
    {
        // A store id here would mean the reply did not describe the entry that was just created,
        // and every caller keys its own record on this value.
        var result = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer("""{"ok":true,"value":"440"}"""));

        Assert.Equal(SteamClientWriteOutcome.Unknown, result.Outcome);
        Assert.Equal(0u, result.AppId);
        Assert.Contains("440", result.Error);
    }

    [Theory]
    [InlineData("""{"ok":true,"value":""}""")]
    [InlineData("""{"ok":true,"value":"-2147483646"}""")]
    [InlineData("""{"ok":true}""")]
    public void AddShortcutRefusesAnUnreadableId(string reply)
    {
        // The id crosses as a decimal string because a shortcut id read back as a JSON number is
        // negative. A signed spelling is therefore a reply this library did not produce.
        var result = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Answer(reply));

        Assert.Equal(SteamClientWriteOutcome.Unknown, result.Outcome);
        Assert.Equal(0u, result.AppId);
        Assert.Equal("Steam reported an unreadable shortcut id.", result.Error);
    }

    [Fact]
    public void AddShortcutDistinguishesNotSentUnknownAndRefused()
    {
        var notSent = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Unanswered(SteamUiDispatch.NotSent, "port closed"));
        Assert.Equal(SteamClientWriteOutcome.NotSent, notSent.Outcome);
        Assert.Equal("port closed", notSent.Error);

        var unknown = SteamApps.ParseAddShortcut(FakeSteamUiTransport.Unanswered(SteamUiDispatch.Unanswered, "timed out"));
        Assert.Equal(SteamClientWriteOutcome.Unknown, unknown.Outcome);

        var refused = SteamApps.ParseAddShortcut(
            FakeSteamUiTransport.Answer("""{"ok":false,"err":"This Steam client does not expose AddShortcut."}"""));
        Assert.Equal(SteamClientWriteOutcome.Rejected, refused.Outcome);
        Assert.False(refused.Succeeded);
        Assert.Equal("This Steam client does not expose AddShortcut.", refused.Error);
    }

    [Theory]
    [InlineData(440u)]
    [InlineData(2147483647u)]
    public async Task RemovingAStoreAppIdIsRefusedBeforeSteamIsReached(uint appId)
    {
        // Deleting a library entry cannot be undone by this call, and a store title has no shortcut
        // entry to delete, so the guard is an argument check rather than a Steam-side refusal.
        var client = new SteamClient(new FakeSteamUiTransport());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Apps.RemoveShortcutAsync(appId));
    }

    [Fact]
    public async Task AppDetailsReadReleasesItsSubscriptionAndUsesTheClientTransport()
    {
        var transport = new FakeSteamUiTransport
        {
            EvaluationValue = """{"ok":true,"exe":"C:\\g.exe"}"""
        };
        var client = new SteamClient(transport);

        var result = await client.RunningApps.ReadDetailsAsync(2147483650u);

        Assert.Equal("C:\\g.exe", result.Value?.ShortcutExe);
        Assert.Equal(string.Empty, result.Value?.InstallFolder);
        var expression = Assert.Single(transport.Expressions);
        Assert.Contains("RegisterForAppDetails(id,", expression);
        Assert.Contains(")(2147483650)", expression);
        Assert.Contains("h.unregister()", expression);
    }

    [Fact]
    public async Task AnEmptyArtworkImageIsRefusedBeforeSteamIsContacted()
    {
        var client = new SteamClient(new FakeSteamUiTransport());

        await Assert.ThrowsAsync<ArgumentException>(() => client.Apps.SetCustomArtworkAsync(
            440,
            SteamArtworkSlot.Hero,
            ReadOnlyMemory<byte>.Empty,
            SteamArtworkFormat.Png));
    }

    [Fact]
    public async Task AClientThatThrowsReportsNotSentInsteadOfThrowing()
    {
        var transport = new FakeSteamUiTransport
        {
            OnEvaluate = _ => throw new ObjectDisposedException("transport")
        };
        var client = new SteamClient(transport);

        var result = await client.Apps.SetLaunchOptionsAsync(440, "-novid");

        Assert.Equal(SteamClientWriteOutcome.NotSent, result.Outcome);
    }

    // ---- SteamInstallFolders ----

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("E:\\SteamLibrary\\", "e:\\steamlibrary")]
    [InlineData("E:/SteamLibrary//", "e:\\steamlibrary")]
    public void LibraryPathsNormalizeLikeTheInjectedScript(string? path, string expected)
    {
        Assert.Equal(expected, SteamInstallFolders.NormalizePath(path));
    }

    [Fact]
    public void AddKeepsAMountedRegistrationUnlessTheCallerReplacesIt()
    {
        var adopt = SteamInstallFolders.BuildAddExpression("E:\\Lib", null, false);
        var replace = SteamInstallFolders.BuildAddExpression("E:\\Lib", "Card", true);

        Assert.Contains("const live=same.find(x=>x.bIsMounted);", adopt);
        Assert.Contains("const l=null;", adopt);
        Assert.Contains("const live=null;", replace);
        Assert.Contains("const l=\"Card\";", replace);
        Assert.Contains("\"E:\\\\Lib\"", replace);
    }

    [Fact]
    public void RemovalSelectsEveryRegistrationAtThePath()
    {
        var expression = SteamInstallFolders.BuildRemoveExpression("E:\\Lib");

        Assert.Contains("for(const f of same){await SteamClient.InstallFolder.RemoveInstallFolder", expression);
        Assert.DoesNotContain("same[0]", expression);
    }

    [Theory]
    [InlineData("""{"ok":true,"purged":0,"existing":false}""", SteamLibraryAddStatus.Added, null)]
    [InlineData("""{"ok":true,"purged":2,"existing":true}""", SteamLibraryAddStatus.AlreadyPresent, "AlreadyMounted")]
    [InlineData("""{"ok":false,"err":"DriveAlreadyHasLibrary"}""", SteamLibraryAddStatus.AlreadyPresent,
        "DriveAlreadyHasLibrary")]
    [InlineData("""{"ok":false,"result":8}""", SteamLibraryAddStatus.Rejected, "EResult 8")]
    [InlineData("""{"ok":true,"purged":0,"purgeFailed":1,"existing":false}""", SteamLibraryAddStatus.Partial,
        "1 stale registration(s) at the same path could not be removed.")]
    [InlineData(null, SteamLibraryAddStatus.Unknown, "No response from Steam.")]
    public void AddRepliesMapToStatuses(string? reply, SteamLibraryAddStatus status, string? detail)
    {
        Assert.Equal(
            new SteamLibraryAddResult(status, detail),
            SteamInstallFolders.InterpretAdd(FakeSteamUiTransport.Answer(reply)));
    }

    [Fact]
    public void AnAddThatNeverReachedSteamIsNotSentAndOneThatLostItsAnswerIsUnknown()
    {
        Assert.Equal(
            SteamLibraryAddStatus.NotSent,
            SteamInstallFolders.InterpretAdd(FakeSteamUiTransport.Unanswered(SteamUiDispatch.Closed, "closed")).Status);
        Assert.Equal(
            SteamLibraryAddStatus.Unknown,
            SteamInstallFolders.InterpretAdd(FakeSteamUiTransport.Unanswered(SteamUiDispatch.Unanswered, "lost")).Status);
    }

    [Fact]
    public void RemoveAndLabelReportAnAbsentPath()
    {
        var absent = FakeSteamUiTransport.Answer("""{"ok":true,"absent":true}""");

        Assert.Equal(SteamLibraryRemoveStatus.NotPresent, SteamInstallFolders.InterpretRemove(absent).Status);
        Assert.Equal(SteamLibraryLabelStatus.NotPresent, SteamInstallFolders.InterpretLabel(absent).Status);
        Assert.Equal(SteamLibraryRemoveStatus.Removed,
            SteamInstallFolders.InterpretRemove(FakeSteamUiTransport.Answer("""{"ok":true,"removed":1}""")).Status);
        Assert.Equal(SteamLibraryLabelStatus.Applied,
            SteamInstallFolders.InterpretLabel(FakeSteamUiTransport.Answer("""{"ok":true}""")).Status);
    }

    // ---- SteamDownloadActivity ----

    [Fact]
    public void DownloadParseReadsAnActiveDownload()
    {
        var overview = SteamDownloadActivity.Parse(
            """{"state":"Downloading","paused":false,"appid":3280350,"bps":24162405}""");

        Assert.NotNull(overview);
        Assert.True(overview.Value.Active);
        Assert.Equal("Downloading", overview.Value.State);
        Assert.Equal(3280350u, overview.Value.AppId);
        Assert.Equal(24162405, overview.Value.NetworkBytesPerSecond);
    }

    [Fact]
    public void DownloadParseTreatsStateNoneAndAPausedQueueAsInactive()
    {
        var idle = SteamDownloadActivity.Parse("""{"state":"None","paused":false,"appid":0,"bps":0}""");
        var paused = SteamDownloadActivity.Parse("""{"state":"Downloading","paused":true,"appid":42,"bps":0}""");

        Assert.False(idle?.Active);
        Assert.False(paused?.Active);
        Assert.True(paused?.Paused);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"err":"timeout"}""")]
    [InlineData("""{"state":"None","paused":"no"}""")]
    public void DownloadParseReturnsNullForUnusablePayloads(string? json)
    {
        Assert.Null(SteamDownloadActivity.Parse(json));
    }

    [Fact]
    public void DownloadParseDefaultsMissingFieldsToInactive()
    {
        var overview = SteamDownloadActivity.Parse("{}");

        Assert.NotNull(overview);
        Assert.False(overview.Value.Active);
        Assert.Equal("", overview.Value.State);
        Assert.Equal(0u, overview.Value.AppId);
    }

    [Theory]
    [InlineData("Downloading", false, true)]
    [InlineData("Starting", false, true)]
    [InlineData("Stopping", false, true)]
    [InlineData("Downloading", true, false)]
    [InlineData("None", false, false)]
    [InlineData("", false, false)]
    public void DownloadIsActiveRequiresARealUnpausedState(string state, bool paused, bool expected)
    {
        Assert.Equal(expected, SteamDownloadActivity.IsActive(state, paused));
    }

    // ---- SteamLibraryData ----

    [Fact]
    public void CollectionsParseKeepsNumericAppIdsOnly()
    {
        var collections = SteamLibraryData.ParseCollections(FakeSteamUiTransport.Answer(
            """{"ok":true,"collections":[{"id":"uc-1","name":"Fav","appids":[10,"x",20]}]}"""));

        Assert.True(collections.Succeeded);
        var collection = Assert.Single(collections.Value!);
        Assert.Equal("uc-1", collection.Id);
        Assert.Equal("Fav", collection.Name);
        Assert.Equal([10u, 20u], collection.AppIds);
    }

    [Fact]
    public void GamesParseSortsByNameAndFlagsShortcuts()
    {
        var games = SteamLibraryData.ParseReadGames(FakeSteamUiTransport.Answer(
            """{"ok":true,"apps":[{"id":20,"name":"beta"},{"id":2147483650,"name":"Alpha","sc":true}]}"""));

        Assert.True(games.Succeeded);
        Assert.Equal(
            [new SteamLibraryApp(2147483650, "Alpha", true), new SteamLibraryApp(20, "beta")],
            games.Games);
    }

    [Fact]
    public void TagsParseDropsUnnamedTags()
    {
        var tags = SteamLibraryData.ParseTags(FakeSteamUiTransport.Answer(
            """{"ok":true,"tags":[{"id":19,"name":"Action","count":4},{"id":7,"name":""}]}"""));

        Assert.Equal([new SteamStoreTag(19, "Action", 4)], tags.Value!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"ok":false,"err":"no store"}""")]
    [InlineData("""{"ok":true,"apps":{}}""")]
    [InlineData("""{"ok":true,"apps":[{"name":"missing id"}]}""")]
    public void AFailedLibraryReadIsNotAnEmptyLibrary(string? json)
    {
        var games = SteamLibraryData.ParseReadGames(FakeSteamUiTransport.Answer(json));

        Assert.False(games.Succeeded);
        Assert.Empty(games.Games);
    }

    // ---- SteamCurrentPage ----

    [Fact]
    public void CurrentPageNamesTheSignalThatFoundTheApp()
    {
        Assert.Equal(
            new SteamCurrentApp(440, "focus"),
            SteamCurrentPage.Parse(FakeSteamUiTransport.Answer("""{"id":440,"src":"focus"}""")).Value);
        Assert.Equal(
            new SteamCurrentApp(440, "in-page"),
            SteamCurrentPage.Parse(FakeSteamUiTransport.Answer("""{"id":440}""")).Value);
        Assert.False(SteamCurrentPage.Parse(FakeSteamUiTransport.Unanswered(SteamUiDispatch.Closed, "closed")).Succeeded);
        Assert.False(SteamCurrentPage.Parse(FakeSteamUiTransport.Answer("""{"id":"440"}""")).Succeeded);
    }

    // ---- SteamRunningAppsProbe ----

    [Fact]
    public void RunningAppsReadingCarriesTheValidIdsAndTheGeneration()
    {
        var observation = SteamRunningAppsProbe.ParseObservation(
            FakeSteamUiTransport.Answer("""{"ok":true,"ids":[1,0,2],"generation":7}"""));

        Assert.True(observation.Reachable);
        Assert.Equal([1u, 2u], observation.AppIds);
        Assert.Equal(7, observation.SourceGeneration);
        Assert.Null(observation.Diagnostic);
    }

    [Fact]
    public void AClosedTransportNamesNoAppInsteadOfFailing()
    {
        var closed = SteamRunningAppsProbe.ParseObservation(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.Closed, "Steam UI transport held."));
        var failed = SteamRunningAppsProbe.ParseObservation(
            FakeSteamUiTransport.Unanswered(SteamUiDispatch.NotSent, "socket closed"));

        Assert.True(closed.Reachable);
        Assert.Empty(closed.AppIds);
        Assert.Null(closed.Diagnostic);
        Assert.False(failed.Reachable);
        Assert.Equal("socket closed", failed.Diagnostic);
    }

    [Fact]
    public void RunningAppsObserverRejectionIsUnreachable()
    {
        var observation = SteamRunningAppsProbe.ParseObservation(
            FakeSteamUiTransport.Answer("""{"ok":false,"err":"GameSessions missing"}"""));

        Assert.False(observation.Reachable);
        Assert.Equal("GameSessions missing", observation.Diagnostic);
    }

    [Fact]
    public void TheObserverIsPublishedOnlyAfterSteamAcceptedTheRegistration()
    {
        var expression = SteamRunningAppsProbe.ObserveExpression;

        Assert.True(
            expression.IndexOf("RegisterForAppLifetimeNotifications", StringComparison.Ordinal)
            < expression.IndexOf("window.__steamUiRunningApps=R;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningAppsLeaseRemovesTheObserverAndReleasesTheTransport()
    {
        var transport = new FakeSteamUiTransport();
        var probe = new SteamClient(transport).RunningApps;

        var lease = await probe.SubscribeAsync();
        transport.EvaluationValue = """{"ok":true,"ids":[42],"generation":1}""";
        var observation = await probe.ObserveAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal([42u], observation.AppIds);
        Assert.Equal(1, transport.ReleasedSubscriptions);
        Assert.Collection(
            transport.Expressions,
            observe =>
            {
                Assert.Contains("RegisterForAppLifetimeNotifications", observe);
                Assert.Contains("window.__steamUiRunningApps", observe);
            },
            remove => Assert.Contains("R.dispose();delete window.__steamUiRunningApps;", remove));
    }

    // ---- Collections ----

    [Fact]
    public void ACollectionIsOwnedByItsIdAndChangedByDifference()
    {
        var script = SteamCollections.SyncScript("uc-1", "Epic \"Games\"", [3000000001u, 440u], [3000000002u], true);

        Assert.Contains("const existing=\"uc-1\";", script, StringComparison.Ordinal);
        Assert.Contains("find(c=>c.id===existing)", script, StringComparison.Ordinal);
        Assert.Contains("const want=[3000000001,440],drop=new Set([3000000002])", script, StringComparison.Ordinal);
        Assert.Contains("NewUnsavedCollection(\"Epic \\u0022Games\\u0022\"", script, StringComparison.Ordinal);
        Assert.Contains("a.appid>>>0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName===", script, StringComparison.Ordinal);
        Assert.Contains("if(true&&apps().length===0)", script, StringComparison.Ordinal);
        Assert.Contains("const existing=null;", SteamCollections.SyncScript(null, "X", [], [], false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACollectionReplyCarriesItsIdAndCountOrTheRefusal()
    {
        var synced = SteamCollections.ParseSync(FakeSteamUiTransport.Answer("""{"ok":true,"id":"uc-9","count":4}"""));
        Assert.Equal((true, "uc-9", 4), (synced.Succeeded, synced.Id, synced.Count));

        var deleted = SteamCollections.ParseSync(FakeSteamUiTransport.Answer("""{"ok":true,"id":null,"count":0}"""));
        Assert.True(deleted.Succeeded);
        Assert.Null(deleted.Id);

        var refused = SteamCollections.ParseSync(FakeSteamUiTransport.Answer("""{"ok":false,"err":"not loaded"}"""));
        Assert.Equal((SteamClientWriteOutcome.Rejected, "not loaded"), (refused.Outcome, refused.Error));

        Assert.Equal(
            SteamClientWriteOutcome.NotSent,
            SteamCollections.ParseSync(FakeSteamUiTransport.Unanswered(SteamUiDispatch.Closed, "closed")).Outcome);
    }

    [Fact]
    public void ACollectionCreatedBeforeALaterStepFailedKeepsItsId()
    {
        var partial = SteamCollections.ParseSync(
            FakeSteamUiTransport.Answer("""{"ok":false,"err":"Save failed","id":"uc-7"}"""));

        Assert.Equal(SteamClientWriteOutcome.Rejected, partial.Outcome);
        Assert.Equal("uc-7", partial.Id);
    }
}
