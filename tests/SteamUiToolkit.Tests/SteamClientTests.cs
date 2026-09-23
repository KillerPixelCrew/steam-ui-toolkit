namespace SteamUiToolkit.Tests;

/// <summary>One-shot calls into Steam's client API: script shape, reply parsing and refusals.</summary>
public sealed class SteamClientTests
{
    // ---- Shared write replies ----

    [Fact]
    public void WriteReplyDistinguishesUnreachableFromRefused()
    {
        var unreachable = SteamClientScript.ParseWrite(CefEvalResult.Unreachable("port closed"));
        Assert.False(unreachable.Reachable);
        Assert.False(unreachable.Succeeded);
        Assert.Equal("port closed", unreachable.Error);

        var refused = SteamClientScript.ParseWrite(CefEvalResult.Ok("""{"ok":false,"err":"bad id"}"""));
        Assert.True(refused.Reachable);
        Assert.False(refused.Accepted);
        Assert.Equal("bad id", refused.Error);

        var accepted = SteamClientScript.ParseWrite(CefEvalResult.Ok("""{"ok":true}"""));
        Assert.True(accepted.Succeeded);
        Assert.Null(accepted.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void WriteReplyWithoutAnOkObjectIsARefusal(string? value)
    {
        var result = SteamClientScript.ParseWrite(CefEvalResult.Ok(value));

        Assert.True(result.Reachable);
        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrEmpty(result.Error));
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
        var result = SteamApps.ParseDetails(CefEvalResult.Ok(
            """
            {"ok":true,"launch":"-novid","exe":"\"C:\\G\\g.exe\"","args":"-x","dir":"\"C:\\G\\\"","install":"D:\\Lib\\G"}
            """));

        Assert.True(result.Reachable);
        Assert.Equal(
            new SteamAppDetails("-novid", "\"C:\\G\\g.exe\"", "-x", "\"C:\\G\\\"", "D:\\Lib\\G"),
            result.Details);
    }

    [Fact]
    public void AppDetailsRefusalKeepsSteamsReason()
    {
        var result = SteamApps.ParseDetails(CefEvalResult.Ok("""{"ok":false,"err":"Steam has no details"}"""));

        Assert.True(result.Reachable);
        Assert.Null(result.Details);
        Assert.Equal("Steam has no details", result.Error);
    }

    [Fact]
    public void AddShortcutReplyCarriesTheGeneratedId()
    {
        var result = SteamApps.ParseAddShortcut(CefEvalResult.Ok("""{"ok":true,"value":"2147483650"}"""));

        Assert.True(result.Succeeded);
        Assert.Equal(2147483650u, result.AppId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AddShortcutRefusesAnIdOutsideTheShortcutRange()
    {
        // A store id here would mean the reply did not describe the entry that was just created,
        // and every caller keys its own record on this value.
        var result = SteamApps.ParseAddShortcut(CefEvalResult.Ok("""{"ok":true,"value":"440"}"""));

        Assert.True(result.Reachable);
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
        var result = SteamApps.ParseAddShortcut(CefEvalResult.Ok(reply));

        Assert.True(result.Reachable);
        Assert.Equal(0u, result.AppId);
        Assert.Equal("Steam reported an unreadable shortcut id.", result.Error);
    }

    [Fact]
    public void AddShortcutDistinguishesUnreachableFromRefused()
    {
        var unreachable = SteamApps.ParseAddShortcut(CefEvalResult.Unreachable("port closed"));
        Assert.False(unreachable.Reachable);
        Assert.Equal("port closed", unreachable.Error);

        var refused = SteamApps.ParseAddShortcut(
            CefEvalResult.Ok("""{"ok":false,"err":"This Steam client does not expose AddShortcut."}"""));
        Assert.True(refused.Reachable);
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
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SteamApps.RemoveShortcutAsync(appId));
    }

    [Fact]
    public async Task AppDetailsReadReleasesItsSubscriptionAndUsesTheProbeTransport()
    {
        var transport = new FakeSteamUiTransport
        {
            EvaluationValue = """{"ok":true,"exe":"C:\\g.exe"}"""
        };
        var probe = new SteamRunningAppsProbe(transport);

        var result = await probe.ReadDetailsAsync(2147483650u);

        Assert.Equal("C:\\g.exe", result.Details?.ShortcutExe);
        Assert.Equal(string.Empty, result.Details?.InstallFolder);
        var expression = Assert.Single(transport.Expressions);
        Assert.Contains("RegisterForAppDetails(2147483650,", expression);
        Assert.Contains("h.unregister()", expression);
    }

    [Fact]
    public async Task AnEmptyArtworkImageIsRefusedBeforeSteamIsContacted()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SteamApps.SetCustomArtworkAsync(
            440,
            SteamArtworkSlot.Hero,
            ReadOnlyMemory<byte>.Empty,
            SteamArtworkFormat.Png));
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
    [InlineData("""{"ok":false,"message":"DriveAlreadyHasLibrary"}""", SteamLibraryAddStatus.AlreadyPresent,
        "DriveAlreadyHasLibrary")]
    [InlineData("""{"ok":false,"result":8}""", SteamLibraryAddStatus.Rejected, "EResult 8")]
    [InlineData(null, SteamLibraryAddStatus.Unavailable, "No response from Steam.")]
    public void AddRepliesMapToStatuses(string? reply, SteamLibraryAddStatus status, string? detail)
    {
        Assert.Equal(new SteamLibraryAddResult(status, detail), SteamInstallFolders.InterpretAdd(reply));
    }

    [Fact]
    public void RemoveAndLabelReportAnAbsentPath()
    {
        const string absent = """{"ok":true,"absent":true}""";

        Assert.Equal(SteamLibraryRemoveStatus.NotPresent, SteamInstallFolders.InterpretRemove(absent).Status);
        Assert.Equal(SteamLibraryLabelStatus.NotPresent, SteamInstallFolders.InterpretLabel(absent).Status);
        Assert.Equal(SteamLibraryRemoveStatus.Removed,
            SteamInstallFolders.InterpretRemove("""{"ok":true,"removed":1}""").Status);
        Assert.Equal(SteamLibraryLabelStatus.Applied,
            SteamInstallFolders.InterpretLabel("""{"ok":true}""").Status);
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
        Assert.Equal(3280350, overview.Value.AppId);
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
        Assert.Equal(0, overview.Value.AppId);
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
        var collections = SteamLibraryData.ParseCollections(
            """{"ok":true,"collections":[{"id":"uc-1","name":"Fav","appids":[10,"x",20]}]}""");

        var collection = Assert.Single(collections);
        Assert.Equal("uc-1", collection.Id);
        Assert.Equal("Fav", collection.Name);
        Assert.Equal([10L, 20L], collection.AppIds);
    }

    [Fact]
    public void GamesParseSortsByNameAndFlagsShortcuts()
    {
        var games = SteamLibraryData.ParseGames(
            """{"ok":true,"apps":[{"id":20,"name":"beta"},{"id":"bad","name":"skip"},{"id":2147483650,"name":"Alpha","sc":true}]}""");

        Assert.Equal(
            [new SteamLibraryApp(2147483650, "Alpha", true), new SteamLibraryApp(20, "beta")],
            games);
    }

    [Fact]
    public void TagsParseDropsUnnamedTags()
    {
        var tags = SteamLibraryData.ParseTags(
            """{"ok":true,"tags":[{"id":19,"name":"Action","count":4},{"id":7,"name":""}]}""");

        Assert.Equal([new SteamStoreTag(19, "Action", 4)], tags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"ok":false,"err":"no store"}""")]
    [InlineData("""{"ok":true,"apps":{}}""")]
    [InlineData("""{"ok":true,"apps":[{"name":"missing id"}]}""")]
    public void LibraryReadsDegradeToAnEmptyList(string? json)
    {
        Assert.Empty(SteamLibraryData.ParseGames(json));
    }

    // ---- SteamCurrentPage ----

    [Fact]
    public void CurrentPageNamesTheSignalThatFoundTheApp()
    {
        Assert.Equal(
            new SteamCurrentApp(440, "focus"),
            SteamCurrentPage.Parse(CefEvalResult.Ok("""{"id":440,"src":"focus"}""")));
        Assert.Equal(
            new SteamCurrentApp(440, "in-page"),
            SteamCurrentPage.Parse(CefEvalResult.Ok("""{"id":440}""")));
        Assert.Equal(
            new SteamCurrentApp(0, "none"),
            SteamCurrentPage.Parse(CefEvalResult.Unreachable("closed")));
        Assert.Equal(
            new SteamCurrentApp(0, "none"),
            SteamCurrentPage.Parse(CefEvalResult.Ok("""{"id":"440"}""")));
    }

    // ---- SteamRunningAppsProbe ----

    [Fact]
    public void RunningAppsReadingCarriesTheObserverAndOrderedEvents()
    {
        var observation = SteamRunningAppsProbe.ParseObservation(CefEvalResult.Ok(
            """
            {"ok":true,"observer":"o1","ids":[1,0,2],"generation":7,"sequence":9,"complete":true,
             "events":[{"s":8,"id":5,"r":true,"t":1700000000000},{"s":8,"id":6,"r":true},{"s":9,"id":5,"r":false}]}
            """));

        Assert.True(observation.Reachable);
        Assert.Equal("o1", observation.ObserverId);
        Assert.Equal([1u, 2u], observation.AppIds);
        Assert.Equal(7, observation.SourceGeneration);
        Assert.Equal(9, observation.Sequence);
        Assert.True(observation.EventsComplete);
        Assert.Collection(
            observation.Events!,
            started =>
            {
                Assert.Equal((8L, 5u, true), (started.Sequence, started.AppId, started.Running));
                Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), started.Timestamp);
            },
            stopped => Assert.Equal((9L, 5u, false), (stopped.Sequence, stopped.AppId, stopped.Running)));
    }

    [Fact]
    public void ADisabledTransportNamesNoAppInsteadOfFailing()
    {
        var disabled = SteamRunningAppsProbe.ParseObservation(
            CefEvalResult.Unreachable("Steam CEF integration disabled in settings."));
        var failed = SteamRunningAppsProbe.ParseObservation(CefEvalResult.Unreachable("socket closed"));

        Assert.True(disabled.Reachable);
        Assert.Empty(disabled.AppIds);
        Assert.Null(disabled.Diagnostic);
        Assert.Null(disabled.ObserverId);
        Assert.False(failed.Reachable);
        Assert.Equal("socket closed", failed.Diagnostic);
    }

    [Fact]
    public void RunningAppsObserverRejectionIsUnreachable()
    {
        var observation = SteamRunningAppsProbe.ParseObservation(
            CefEvalResult.Ok("""{"ok":false,"err":"GameSessions missing"}"""));

        Assert.False(observation.Reachable);
        Assert.Equal("GameSessions missing", observation.Diagnostic);
    }

    [Fact]
    public void RunningAppsScriptRequestsEventsOnlyWhenAsked()
    {
        var withoutEvents = SteamRunningAppsProbe.BuildObserveExpression(null);
        var withEvents = SteamRunningAppsProbe.BuildObserveExpression(12);

        Assert.Contains("const ev=[];const complete=true;", withoutEvents);
        Assert.Contains("const a=12;const ev=R.log.filter(x=>x.s>a);", withEvents);
        Assert.Contains($"if(R.log.length>{SteamRunningAppsProbe.EventLogCapacity})R.log.shift();", withEvents);
        Assert.Contains($".slice(0,{SteamRunningAppsProbe.MaxReportedApps})", withEvents);
    }

    [Fact]
    public async Task RunningAppsLeaseRemovesTheObserverAndReleasesTheTransport()
    {
        var transport = new FakeSteamUiTransport();
        var probe = new SteamRunningAppsProbe(transport);

        var lease = await probe.SubscribeAsync();
        transport.EvaluationValue = """{"ok":true,"observer":"o1","ids":[42],"generation":1}""";
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
                Assert.Contains("window.__steamUiRunningApps_v2", observe);
                Assert.Contains("window.__wsgm.runningAppsV1", observe);
                Assert.Contains("window.__steamUiRunningApps_v1", observe);
            },
            remove => Assert.Contains("R.dispose();delete window.__steamUiRunningApps_v2", remove));
    }

    [Fact]
    public async Task ANegativeEventSequenceIsRefused()
    {
        var probe = new SteamRunningAppsProbe(new FakeSteamUiTransport());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => probe.ObserveAsync(-1));
    }

    // ---- SteamAppLifetimeTracker ----

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static SteamRunningAppsObservation Reading(
        string observer,
        long sequence,
        uint[] running,
        SteamAppLifetimeEvent[]? events = null,
        bool complete = true)
    {
        return new SteamRunningAppsObservation(true, running, 1, null, observer, sequence, events ?? [], complete);
    }

    private static SteamAppLifetimeEvent Event(long sequence, uint appId, bool running)
    {
        return new SteamAppLifetimeEvent(sequence, appId, running, Now.AddSeconds(sequence));
    }

    [Fact]
    public void TheFirstReadingReportsRunningAppsAsResynchronizedStarts()
    {
        var tracker = new SteamAppLifetimeTracker();
        Assert.Null(tracker.EventsAfter);

        var update = tracker.Apply(Reading("o1", 4, [10, 20]), Now);

        Assert.True(update.AvailabilityChanged);
        Assert.True(update.Available);
        Assert.Equal(
            [new SteamAppLifetimeTracker.Change(10, true, Now, true), new SteamAppLifetimeTracker.Change(20, true, Now, true)],
            update.Changes);
        Assert.Equal(4, tracker.EventsAfter);
    }

    [Fact]
    public void AQuickStartAndStopBetweenReadingsRaisesBothInOrder()
    {
        var tracker = new SteamAppLifetimeTracker();
        tracker.Apply(Reading("o1", 4, []), Now);

        var update = tracker.Apply(
            Reading("o1", 7, [30], [Event(5, 20, true), Event(6, 20, false), Event(7, 30, true)]),
            Now);

        Assert.False(update.AvailabilityChanged);
        Assert.Equal(
            [
                new SteamAppLifetimeTracker.Change(20, true, Now.AddSeconds(5), false),
                new SteamAppLifetimeTracker.Change(20, false, Now.AddSeconds(6), false),
                new SteamAppLifetimeTracker.Change(30, true, Now.AddSeconds(7), false)
            ],
            update.Changes);
        Assert.Equal([30u], tracker.Running);
        Assert.Equal(7, tracker.EventsAfter);
    }

    [Fact]
    public void AlreadySeenAndRedundantEventsRaiseNothing()
    {
        var tracker = new SteamAppLifetimeTracker();
        tracker.Apply(Reading("o1", 4, [10]), Now);

        var update = tracker.Apply(
            Reading("o1", 6, [10], [Event(4, 10, false), Event(5, 10, true), Event(6, 99, false)]),
            Now);

        Assert.Empty(update.Changes);
    }

    [Fact]
    public void AReplacedObserverOrTruncatedLogResynchronizesFromTheRunningSet()
    {
        var tracker = new SteamAppLifetimeTracker();
        tracker.Apply(Reading("o1", 4, [10, 20]), Now);

        var replaced = tracker.Apply(Reading("o2", 1, [20, 30], [Event(1, 99, true)]), Now);
        Assert.Equal(
            [new SteamAppLifetimeTracker.Change(30, true, Now, true), new SteamAppLifetimeTracker.Change(10, false, Now, true)],
            replaced.Changes);
        Assert.Equal(1, tracker.EventsAfter);

        var truncated = tracker.Apply(Reading("o2", 90, [30], [Event(90, 30, true)], complete: false), Now);
        Assert.Equal([new SteamAppLifetimeTracker.Change(20, false, Now, true)], truncated.Changes);
        Assert.Equal(90, tracker.EventsAfter);
    }

    [Fact]
    public void AnOutageKeepsTheRunningSetAndRaisesNoStops()
    {
        var tracker = new SteamAppLifetimeTracker();
        tracker.Apply(Reading("o1", 4, [10]), Now);

        var lost = tracker.Apply(new SteamRunningAppsObservation(false, [], 0, "socket closed"), Now);
        var stillLost = tracker.Apply(new SteamRunningAppsObservation(true, [], 0, null), Now);
        var back = tracker.Apply(Reading("o1", 4, [10]), Now);

        Assert.Equal((true, false, "socket closed"), (lost.AvailabilityChanged, lost.Available, lost.Diagnostic));
        Assert.Empty(lost.Changes);
        Assert.False(stillLost.AvailabilityChanged);
        Assert.Equal([10u], tracker.Running);
        Assert.True(back.AvailabilityChanged);
        Assert.Empty(back.Changes);
    }

    [Fact]
    public void AFullReadingNeverProvesAStop()
    {
        var tracker = new SteamAppLifetimeTracker();
        tracker.Apply(Reading("o1", 1, [1000]), Now);
        var full = Enumerable.Range(1, SteamRunningAppsProbe.MaxReportedApps).Select(i => (uint)i).ToArray();

        var update = tracker.Apply(Reading("o2", 1, full), Now);

        Assert.DoesNotContain(update.Changes, change => !change.Running);
        Assert.Contains(1000u, tracker.Running);
    }

    // ---- SteamAppLifetimeMonitor ----

    [Fact]
    public async Task TheMonitorRaisesEventsFromTheLogAndSurvivesAFailingHandler()
    {
        var transport = new FakeSteamUiTransport();
        var readings = new Queue<string>(
        [
            """{"ok":true,"observer":"o1","ids":[],"sequence":0,"events":[]}""",
            """{"ok":true,"observer":"o1","ids":[],"sequence":2,"events":[{"s":1,"id":440,"r":true},{"s":2,"id":440,"r":false}]}"""
        ]);
        var requests = new List<string>();
        transport.OnEvaluate = evaluation =>
        {
            lock (requests)
            {
                requests.Add(evaluation.Expression);
                var value = evaluation.Expression.Contains("R.dispose();delete window.")
                    ? """{"ok":true}"""
                    : readings.Count > 0
                        ? readings.Dequeue()
                        : """{"ok":true,"observer":"o1","ids":[],"sequence":2,"events":[]}""";
                return Task.FromResult(transport.Reply(value));
            }
        };
        var seen = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new SteamAppLifetimeMonitor(transport, TimeSpan.FromMilliseconds(250));
        monitor.AppStarted += (_, _) => throw new InvalidOperationException("handler failure");
        monitor.AppStarted += (_, e) => seen.Add($"start {e.AppId} {e.Resynchronized}");
        monitor.AppStopped += (_, e) =>
        {
            seen.Add($"stop {e.AppId} {e.IsShortcut}");
            done.TrySetResult();
        };
        monitor.AvailabilityChanged += (_, e) => seen.Add($"available {e.Available}");

        monitor.Start();
        monitor.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["available True", "start 440 False", "stop 440 False"], seen);
        Assert.True(monitor.IsAvailable);
        Assert.Empty(monitor.RunningApps);
        lock (requests)
        {
            Assert.Contains("const ev=[];", requests[0]);
            Assert.Contains("const a=0;", requests[1]);
        }
    }

    [Fact]
    public async Task AStartedMonitorCannotBeRestartedAfterDisposal()
    {
        var monitor = new SteamAppLifetimeMonitor(new FakeSteamUiTransport());
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(monitor.Start);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SteamAppLifetimeMonitor(new FakeSteamUiTransport(), TimeSpan.FromMilliseconds(10)));
    }
}
