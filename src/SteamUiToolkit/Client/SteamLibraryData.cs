using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One user collection, which Steam renders as a library category.</summary>
/// <param name="Id">Steam's collection id (e.g. <c>uc-…</c>).</param>
/// <param name="Name">The display name.</param>
/// <param name="AppIds">The app ids currently in the collection.</param>
public sealed record SteamCollectionInfo(string Id, string Name, IReadOnlyList<long> AppIds);

/// <summary>One game or non-Steam shortcut in the user's library.</summary>
/// <param name="AppId">The Steam app id, or a shortcut's generated id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Shortcut">
///     True for a non-Steam shortcut. Its generated id means nothing outside this machine and has no
///     store page.
/// </param>
public sealed record SteamLibraryApp(long AppId, string Name, bool Shortcut = false);

/// <summary>One store tag (genre) present in the library.</summary>
/// <param name="TagId">Steam's numeric tag id.</param>
/// <param name="Name">The localized tag name.</param>
/// <param name="Count">How many library games carry it.</param>
public sealed record SteamStoreTag(int TagId, string Name, int Count);

/// <summary>Reads the user's library from Steam's own <c>collectionStore</c> and <c>appStore</c>.</summary>
/// <remarks>
///     Every read returns an empty list when Steam is unreachable or answers with an unexpected shape.
///     Call <see cref="IsLoadedAsync" /> first when an empty list must be told apart from a library that
///     has not finished loading.
/// </remarks>
public static class SteamLibraryData
{
    private const string LoadedExpression =
        "JSON.stringify(!!window.webpackChunksteamui&&!!window.collectionStore&&!!window.appStore)";

    private const string CollectionsExpression =
        "(()=>{try{const cs=collectionStore;" +
        "const cols=(cs.userCollections||[]).map(c=>({id:c.id,name:c.displayName," +
        "appids:(c.allApps||c.visibleApps||[]).map(a=>a.appid)}));" +
        "return JSON.stringify({ok:true,collections:cols});}" +
        "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    // Shortcuts come from the all-apps collection, because the type-games collection excludes them.
    private const string GamesExpression =
        "(()=>{try{const cs=collectionStore;" +
        "const g=cs.GetCollection('type-games');" +
        "const games=(g&&(g.allApps||g.visibleApps))||[];" +
        "const ids=new Set(games.map(a=>a.appid));" +
        "const ac=cs.allAppsCollection;" +
        "const all=(ac&&(ac.allApps||ac.visibleApps))||games;" +
        "const out=[];const seen=new Set();" +
        "for(const a of all){" +
        "const sc=typeof a.BIsShortcut==='function'?!!a.BIsShortcut():a.appid>=2147483648;" +
        "if(!ids.has(a.appid)&&!sc)continue;" +
        "if(seen.has(a.appid))continue;seen.add(a.appid);" +
        "out.push({id:a.appid,name:a.display_name||String(a.appid),sc:sc});}" +
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

    /// <summary>Whether Steam's webpack runtime and library stores exist yet.</summary>
    /// <param name="timeout">How long to wait for an answer.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="true" /> once both stores are present.</returns>
    /// <remarks>
    ///     Present is not the same as populated: a store that has just been created can still reject a
    ///     query, so a caller retrying on boot should also check its own read succeeded.
    /// </remarks>
    public static async Task<bool> IsLoadedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(LoadedExpression, timeout, cancellationToken)
            .ConfigureAwait(false);
        return result.Reachable && result.Value == "true";
    }

    /// <summary>Lists the user's collections and the app ids in each.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The collections, or an empty list.</returns>
    public static async Task<IReadOnlyList<SteamCollectionInfo>> ListCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(CollectionsExpression, cancellationToken).ConfigureAwait(false);
        return ParseCollections(value);
    }

    /// <summary>Lists the user's games and non-Steam shortcuts, sorted by name.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The apps, or an empty list.</returns>
    public static async Task<IReadOnlyList<SteamLibraryApp>> ListGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(GamesExpression, cancellationToken).ConfigureAwait(false);
        return ParseGames(value);
    }

    /// <summary>
    ///     Lists the store tags used by the library's games with their localized names and counts, most
    ///     used first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The tags, or an empty list.</returns>
    public static async Task<IReadOnlyList<SteamStoreTag>> ListStoreTagsAsync(
        CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(TagsExpression, cancellationToken).ConfigureAwait(false);
        return ParseTags(value);
    }

    /// <summary>Parses the collections payload. Pure, for tests.</summary>
    /// <param name="json">The payload, or null.</param>
    internal static IReadOnlyList<SteamCollectionInfo> ParseCollections(string? json)
    {
        return Parse(json, "collections", "Steam collections", static col =>
        {
            var appIds = new List<long>();
            foreach (var appId in col.GetProperty("appids").EnumerateArray())
            {
                if (appId.ValueKind == JsonValueKind.Number && appId.TryGetInt64(out var value))
                {
                    appIds.Add(value);
                }
            }

            return new SteamCollectionInfo(
                col.GetProperty("id").GetString() ?? "",
                col.GetProperty("name").GetString() ?? "",
                appIds);
        });
    }

    /// <summary>Parses the games payload and sorts it by name. Pure, for tests.</summary>
    /// <param name="json">The payload, or null.</param>
    internal static IReadOnlyList<SteamLibraryApp> ParseGames(string? json)
    {
        var list = Parse(json, "apps", "Steam games", static app =>
        {
            var id = app.GetProperty("id");
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var appId))
            {
                return null;
            }

            var shortcut = app.TryGetProperty("sc", out var sc) && sc.ValueKind == JsonValueKind.True;
            return new SteamLibraryApp(
                appId,
                app.GetProperty("name").GetString() ?? appId.ToString(CultureInfo.InvariantCulture),
                shortcut);
        });
        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Parses the tags payload. Pure, for tests.</summary>
    /// <param name="json">The payload, or null.</param>
    internal static IReadOnlyList<SteamStoreTag> ParseTags(string? json)
    {
        return Parse(json, "tags", "Steam tags", static tag =>
        {
            var id = tag.GetProperty("id");
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var tagId))
            {
                return null;
            }

            var name = tag.GetProperty("name").GetString() ?? "";
            var count = tag.TryGetProperty("count", out var c)
                        && c.ValueKind == JsonValueKind.Number
                        && c.TryGetInt32(out var value)
                ? value
                : 0;
            return name.Length > 0 ? new SteamStoreTag(tagId, name, count) : null;
        });
    }

    private static async Task<string?> ReadAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return result.Reachable ? result.Value : null;
    }

    private static List<T> Parse<T>(string? json, string property, string what, Func<JsonElement, T?> read)
        where T : class
    {
        var list = new List<T>();
        if (json is null)
        {
            return list;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root)
                || !root.TryGetProperty(property, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (read(item) is { } value)
                {
                    list.Add(value);
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            SteamUiLog.Warn($"{what} list parse failed: {ex.Message}");
            return [];
        }
    }
}
