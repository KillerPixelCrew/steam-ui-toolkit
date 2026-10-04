using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A library read that distinguishes a valid empty library from unavailable or incompatible Steam.</summary>
/// <param name="Games">The confirmed library games.</param>
/// <param name="Error">The reason a library could not be read, or null on success.</param>
public sealed record SteamLibraryReadResult(IReadOnlyList<SteamLibraryApp> Games, string? Error)
{
    /// <summary>Whether Steam returned a valid library, including a valid empty one.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>One user collection, which Steam renders as a library category.</summary>
/// <param name="Id">Steam's collection id (e.g. <c>uc-…</c>).</param>
/// <param name="Name">The display name.</param>
/// <param name="AppIds">The unsigned app ids currently in the collection.</param>
public sealed record SteamCollectionInfo(string Id, string Name, IReadOnlyList<uint> AppIds);

/// <summary>One game or non-Steam shortcut in the user's library.</summary>
/// <param name="AppId">The Steam app id, or a shortcut's generated id, unsigned.</param>
/// <param name="Name">The display name.</param>
/// <param name="Shortcut">
///     True for a non-Steam shortcut. Its generated id means nothing outside this machine and has no
///     store page.
/// </param>
public sealed record SteamLibraryApp(uint AppId, string Name, bool Shortcut = false);

/// <summary>One store tag (genre) present in the library.</summary>
/// <param name="TagId">Steam's numeric tag id.</param>
/// <param name="Name">The localized tag name.</param>
/// <param name="Count">How many library games carry it.</param>
public sealed record SteamStoreTag(int TagId, string Name, int Count);

/// <summary>Reads the user's library from Steam's own <c>collectionStore</c> and <c>appStore</c>.</summary>
/// <remarks>
///     Every read says whether it succeeded. A failed read never stands in for an empty library, so a
///     caller can keep its last good answer instead of treating "Steam did not answer" as "no games".
/// </remarks>
public sealed class SteamLibraryData
{
    private const string CollectionsExpression =
        "(()=>{try{const cs=collectionStore;" +
        "const cols=(cs.userCollections||[]).map(c=>({id:c.id,name:c.displayName," +
        "appids:(c.allApps||c.visibleApps||[]).map(a=>a.appid>>>0)}));" +
        "return JSON.stringify({ok:true,collections:cols});}" +
        "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    // Shortcuts come from the all-apps collection, because the type-games collection excludes them.
    // Steam keeps a shortcut's id as a signed number, so every id leaves the page unsigned.
    internal const string GamesExpression =
        "(()=>{try{const cs=collectionStore;" +
        "const g=cs.GetCollection('type-games');" +
        "const games=(g&&(g.allApps||g.visibleApps))||[];" +
        "const ids=new Set(games.map(a=>a.appid>>>0));" +
        "const ac=cs.allAppsCollection;" +
        "const all=(ac&&(ac.allApps||ac.visibleApps))||games;" +
        "const out=[];const seen=new Set();" +
        "for(const a of all){const id=a.appid>>>0;" +
        "const sc=typeof a.BIsShortcut==='function'?!!a.BIsShortcut():id>=2147483648;" +
        "if(!ids.has(id)&&!sc)continue;" +
        "if(seen.has(id))continue;seen.add(id);" +
        "out.push({id,name:a.display_name||String(id),sc:sc});}" +
        "return JSON.stringify({ok:true,apps:out});}" +
        "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private const string TagsExpression =
        "(()=>{try{const cs=collectionStore,as=appStore;" +
        "const g=cs.GetCollection('type-games');" +
        "const apps=(g&&(g.allApps||g.visibleApps))||[];" +
        "const m=as.m_mapStoreTagLocalization||{};const byTag={};" +
        "for(const a of apps)for(const t of (a.store_tag||[])){const nm=m[t];if(!nm)continue;" +
        "(byTag[t]=byTag[t]||{id:t,name:nm,count:0}).count++;}" +
        "const out=Object.values(byTag).sort((a,b)=>b.count-a.count);" +
        "return JSON.stringify({ok:true,tags:out});}" +
        "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    private readonly SteamClient _client;

    internal SteamLibraryData(SteamClient client)
    {
        _client = client;
    }

    /// <summary>Reads the user's collections and the app ids in each.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The collections, or why they could not be read.</returns>
    public async Task<SteamReadResult<IReadOnlyList<SteamCollectionInfo>>> ReadCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _client.ReadAsync(CollectionsExpression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseCollections(result);
    }

    /// <summary>Reads the user's games and non-Steam shortcuts, sorted by name.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The games or a specific failure.</returns>
    public async Task<SteamLibraryReadResult> ReadGamesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.ReadAsync(GamesExpression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseReadGames(result);
    }

    /// <summary>
    ///     Reads the store tags used by the library's games with their localized names and counts, most
    ///     used first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The tags, or why they could not be read.</returns>
    public async Task<SteamReadResult<IReadOnlyList<SteamStoreTag>>> ReadStoreTagsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _client.ReadAsync(TagsExpression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseTags(result);
    }

    /// <summary>Maps the games reply to a read. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamLibraryReadResult ParseReadGames(SteamUiEvaluationResult result)
    {
        var read = SteamClientScript.ParseRead<IReadOnlyList<SteamLibraryApp>>(result, "library", static root =>
        {
            if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Steam did not return a valid library.");
            }

            List<SteamLibraryApp> list = [];
            foreach (var app in apps.EnumerateArray())
            {
                if (!app.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                                                          || !id.TryGetUInt32(out var appId)
                                                          || !app.TryGetProperty("name", out var name)
                                                          || name.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("Steam returned an invalid game entry.");
                }

                var shortcut = app.TryGetProperty("sc", out var sc) && sc.ValueKind == JsonValueKind.True;
                list.Add(new SteamLibraryApp(
                    appId, name.GetString() ?? appId.ToString(CultureInfo.InvariantCulture), shortcut));
            }

            list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        });
        return read.Succeeded
            ? new SteamLibraryReadResult(read.Value ?? [], null)
            : new SteamLibraryReadResult([], read.Error ?? "Steam's library is unavailable.");
    }

    /// <summary>Maps the collections reply to a read. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamReadResult<IReadOnlyList<SteamCollectionInfo>> ParseCollections(
        SteamUiEvaluationResult result)
    {
        return SteamClientScript.ParseRead<IReadOnlyList<SteamCollectionInfo>>(result, "collections", static root =>
        {
            if (!root.TryGetProperty("collections", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Steam returned no collection list.");
            }

            List<SteamCollectionInfo> list = [];
            foreach (var col in items.EnumerateArray())
            {
                List<uint> appIds = [];
                foreach (var appId in col.GetProperty("appids").EnumerateArray())
                {
                    if (appId.ValueKind == JsonValueKind.Number && appId.TryGetUInt32(out var value))
                    {
                        appIds.Add(value);
                    }
                }

                list.Add(new SteamCollectionInfo(
                    col.GetProperty("id").GetString() ?? "",
                    col.GetProperty("name").GetString() ?? "",
                    appIds));
            }

            return list;
        });
    }

    /// <summary>Maps the tags reply to a read. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamReadResult<IReadOnlyList<SteamStoreTag>> ParseTags(SteamUiEvaluationResult result)
    {
        return SteamClientScript.ParseRead<IReadOnlyList<SteamStoreTag>>(result, "store tags", static root =>
        {
            if (!root.TryGetProperty("tags", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Steam returned no tag list.");
            }

            List<SteamStoreTag> list = [];
            foreach (var tag in items.EnumerateArray())
            {
                var id = tag.GetProperty("id");
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var tagId))
                {
                    continue;
                }

                var name = tag.GetProperty("name").GetString() ?? "";
                var count = tag.TryGetProperty("count", out var c)
                            && c.ValueKind == JsonValueKind.Number
                            && c.TryGetInt32(out var value)
                    ? value
                    : 0;
                if (name.Length > 0)
                {
                    list.Add(new SteamStoreTag(tagId, name, count));
                }
            }

            return list;
        });
    }
}
