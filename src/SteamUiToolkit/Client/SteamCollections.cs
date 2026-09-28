using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Outcome of bringing one host-owned collection in step.</summary>
/// <param name="Reachable">
///     Whether a validated Steam target ran the request. An unreachable client changed nothing.
/// </param>
/// <param name="Accepted">Whether Steam completed the change without throwing.</param>
/// <param name="Id">
///     The collection's id afterwards: the one it had, a new one when it had to be created, or null
///     when it was deleted or never needed.
/// </param>
/// <param name="Count">How many apps the collection holds afterwards.</param>
/// <param name="Error">Why the target was unreachable, or Steam's own error. Null on success.</param>
public readonly record struct SteamCollectionSyncResult(
    bool Reachable,
    bool Accepted,
    string? Id,
    int Count,
    string? Error)
{
    /// <summary>Whether Steam was reached and completed the change.</summary>
    public bool Succeeded => Reachable && Accepted;
}

/// <summary>Creates and maintains user collections through Steam's own <c>collectionStore</c>.</summary>
/// <remarks>
///     <para>
///         A user collection is what Steam shows as a library category, synced through the Steam
///         Cloud like any the user makes. The host owns a collection by the id this class answers, never
///         by its name: a collection of the same name the user made stays theirs, and a collection the
///         host made keeps its id when the user renames it.
///     </para>
///     <para>
///         Membership changes by difference, not by replacement. The host names the apps it adds and the
///         apps it takes back, so an app the user put into the collection stays there. A collection the
///         host made that ends empty is deleted when asked, since nothing of the user's is left in it.
///     </para>
///     <para>
///         Writes share <see cref="SteamApps" />' gate: both change the client's library, and a shortcut
///         added a moment ago has to be in the library before a collection can hold it. App ids are
///         matched unsigned, since the client keeps a shortcut's id as a signed number.
///     </para>
/// </remarks>
public static class SteamCollections
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>Brings one collection in step: created when missing, apps added and taken back, saved.</summary>
    /// <param name="existingId">The id the host recorded for it, or null when it has none yet.</param>
    /// <param name="name">The name a newly created collection gets.</param>
    /// <param name="add">The apps that belong in it.</param>
    /// <param name="remove">The apps the host put in before and takes back now.</param>
    /// <param name="deleteWhenEmpty">Whether a collection left empty is deleted.</param>
    /// <param name="transport">A specific transport, or null for the session's.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome. Never throws for an unreachable target.</returns>
    public static async Task<SteamCollectionSyncResult> SyncAsync(
        string? existingId,
        string name,
        IReadOnlyCollection<uint> add,
        IReadOnlyCollection<uint> remove,
        bool deleteWhenEmpty,
        ISteamUiTransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var expression = SyncScript(existingId, name, add, remove, deleteWhenEmpty);
        await SteamApps.Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        CefEvalResult result;
        try
        {
            result = await SteamClientScript.EvaluateAsync(
                    transport, SteamUiTargetRole.SharedJsContext, expression, Budget, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            SteamApps.Writes.Release();
        }

        return ParseSync(result);
    }

    /// <summary>The script <see cref="SyncAsync" /> runs. Pure, for tests.</summary>
    internal static string SyncScript(
        string? existingId,
        string name,
        IReadOnlyCollection<uint> add,
        IReadOnlyCollection<uint> remove,
        bool deleteWhenEmpty)
    {
        return SteamClientScript.Read(
            "const cs=window.collectionStore,ac=cs&&cs.allAppsCollection;" +
            "const all=ac&&(ac.allApps||ac.visibleApps);" +
            "if(!all||typeof cs.NewUnsavedCollection!=='function')" +
            "return JSON.stringify({ok:false,err:'Steam has not finished loading its library.'});" +
            "const byId=new Map();for(const a of all)byId.set(a.appid>>>0,a);" +
            "const idOf=a=>a.appid>>>0;" +
            "const want=" + Ids(add) + ",drop=new Set(" + Ids(remove) + ");" +
            "const existing=" + (string.IsNullOrEmpty(existingId) ? "null" : SteamCef.JsString(existingId)) + ";" +
            "let col=existing&&(cs.userCollections||[]).find(c=>c.id===existing)||null;" +
            "if(!col){const first=want.map(id=>byId.get(id)).filter(Boolean);" +
            "if(!first.length)return JSON.stringify({ok:true,id:null,count:0});" +
            "col=cs.NewUnsavedCollection(" + SteamCef.JsString(name) + ",undefined,first);await col.Save();}" +
            "const apps=()=>col.allApps||col.visibleApps||[];" +
            "const has=new Set(apps().map(idOf));" +
            "const toAdd=want.filter(id=>!has.has(id)).map(id=>byId.get(id)).filter(Boolean);" +
            "const toRemove=apps().filter(a=>drop.has(idOf(a)));" +
            "const dd=typeof col.AsDragDropCollection==='function'?col.AsDragDropCollection():null;" +
            "if((toAdd.length||toRemove.length)&&!dd)" +
            "return JSON.stringify({ok:false,err:'Steam does not let this collection be changed.'});" +
            "if(toAdd.length)dd.AddApps(toAdd);if(toRemove.length)dd.RemoveApps(toRemove);" +
            "if(toAdd.length||toRemove.length)await col.Save();" +
            "if(" + (deleteWhenEmpty ? "true" : "false") + "&&apps().length===0){" +
            "const del=typeof col.AsDeletableCollection==='function'?col.AsDeletableCollection():null;" +
            "if(del)await del.Delete();else await cs.DeleteCollection(col);" +
            "return JSON.stringify({ok:true,id:null,count:0});}" +
            "return JSON.stringify({ok:true,id:col.id,count:apps().length});");
    }

    /// <summary>Maps the reply of <see cref="SyncScript" /> to a result. Pure, for tests.</summary>
    internal static SteamCollectionSyncResult ParseSync(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamCollectionSyncResult(false, false, null, 0, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamCollectionSyncResult(true, false, null, 0, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamCollectionSyncResult(true, false, null, 0,
                    SteamClientScript.ErrorOf(root) ?? "Steam rejected the change.");
            }

            var id = root.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : null;
            var count = root.TryGetProperty("count", out var countProperty)
                        && countProperty.ValueKind == JsonValueKind.Number
                        && countProperty.TryGetInt32(out var value)
                ? value
                : 0;
            return new SteamCollectionSyncResult(true, true, id, count, null);
        }
        catch (JsonException ex)
        {
            return new SteamCollectionSyncResult(true, false, null, 0, ex.Message);
        }
    }

    private static string Ids(IReadOnlyCollection<uint> ids)
    {
        return "[" + string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";
    }
}
