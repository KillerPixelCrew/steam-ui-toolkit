using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Outcome of bringing one host-owned collection in step.</summary>
/// <param name="Outcome">Whether the change was never sent, may have run, was refused or completed.</param>
/// <param name="Id">
///     The collection's id afterwards: the one it had, a new one when it had to be created, or null
///     when it was deleted or never needed. A collection created before a later step failed still
///     carries its id here, so the host keeps owning it instead of creating a second one.
/// </param>
/// <param name="Count">How many apps the collection holds afterwards.</param>
/// <param name="Error">Why it was not sent or not applied, or Steam's own error. Null on success.</param>
public readonly record struct SteamCollectionSyncResult(
    SteamClientWriteOutcome Outcome,
    string? Id,
    int Count,
    string? Error)
{
    /// <summary>Whether Steam answered that the change completed.</summary>
    public bool Succeeded => Outcome == SteamClientWriteOutcome.Applied;
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
///         Writes share the client's write lane with <see cref="SteamApps" />: both change the client's
///         library, and a shortcut added a moment ago has to be in the library before a collection can
///         hold it. App ids are matched unsigned, since the client keeps a shortcut's id as a signed
///         number.
///     </para>
/// </remarks>
public sealed class SteamCollections
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly SteamClient _client;

    internal SteamCollections(SteamClient client)
    {
        _client = client;
    }

    /// <summary>Brings one collection in step: created when missing, apps added and taken back, saved.</summary>
    /// <param name="existingId">The id the host recorded for it, or null when it has none yet.</param>
    /// <param name="name">The name a newly created collection gets.</param>
    /// <param name="add">The apps that belong in it.</param>
    /// <param name="remove">The apps the host put in before and takes back now.</param>
    /// <param name="deleteWhenEmpty">Whether a collection left empty is deleted.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome. Never throws for an unreachable target or a cancellation.</returns>
    public async Task<SteamCollectionSyncResult> SyncAsync(
        string? existingId,
        string name,
        IReadOnlyCollection<uint> add,
        IReadOnlyCollection<uint> remove,
        bool deleteWhenEmpty,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var expression = SyncScript(existingId, name, add, remove, deleteWhenEmpty);
        var result = await _client.WriteAsync(expression, Budget, cancellationToken).ConfigureAwait(false);
        return ParseSync(result);
    }

    /// <summary>The script <see cref="SyncAsync" /> runs. Pure, for tests.</summary>
    /// <remarks>
    ///     Once a collection has been created and saved, every reply carries its id, a failure included:
    ///     <c>col</c> is declared outside the <c>try</c> so the shared catch can still name it.
    /// </remarks>
    internal static string SyncScript(
        string? existingId,
        string name,
        IReadOnlyCollection<uint> add,
        IReadOnlyCollection<uint> remove,
        bool deleteWhenEmpty)
    {
        return "(async()=>{let col=null;try{" +
               "const cs=window.collectionStore,ac=cs&&cs.allAppsCollection;" +
               "const all=ac&&(ac.allApps||ac.visibleApps);" +
               "if(!all||typeof cs.NewUnsavedCollection!=='function')" +
               "return JSON.stringify({ok:false,err:'Steam has not finished loading its library.'});" +
               "const byId=new Map();for(const a of all)byId.set(a.appid>>>0,a);" +
               "const idOf=a=>a.appid>>>0;" +
               "const want=" + Ids(add) + ",drop=new Set(" + Ids(remove) + ");" +
               "const existing=" + (string.IsNullOrEmpty(existingId) ? "null" : SteamCef.JsString(existingId)) + ";" +
               "col=existing&&(cs.userCollections||[]).find(c=>c.id===existing)||null;" +
               "if(!col){const first=want.map(id=>byId.get(id)).filter(Boolean);" +
               "if(!first.length)return JSON.stringify({ok:true,id:null,count:0});" +
               "const made=cs.NewUnsavedCollection(" + SteamCef.JsString(name) + ",undefined,first);" +
               "await made.Save();col=made;}" +
               "const apps=()=>col.allApps||col.visibleApps||[];" +
               "const has=new Set(apps().map(idOf));" +
               "const toAdd=want.filter(id=>!has.has(id)).map(id=>byId.get(id)).filter(Boolean);" +
               "const toRemove=apps().filter(a=>drop.has(idOf(a)));" +
               "const dd=typeof col.AsDragDropCollection==='function'?col.AsDragDropCollection():null;" +
               "if((toAdd.length||toRemove.length)&&!dd)" +
               "return JSON.stringify({ok:false,err:'Steam does not let this collection be changed.',id:col.id});" +
               "if(toAdd.length)dd.AddApps(toAdd);if(toRemove.length)dd.RemoveApps(toRemove);" +
               "if(toAdd.length||toRemove.length)await col.Save();" +
               "if(" + (deleteWhenEmpty ? "true" : "false") + "&&apps().length===0){" +
               "const del=typeof col.AsDeletableCollection==='function'?col.AsDeletableCollection():null;" +
               "if(del)await del.Delete();else await cs.DeleteCollection(col);" +
               "return JSON.stringify({ok:true,id:null,count:0});}" +
               "return JSON.stringify({ok:true,id:col.id,count:apps().length});" +
               "}catch(e){const m=e&&e.message;" +
               "return JSON.stringify({ok:false,err:m?String(m):String(e),id:col&&col.id||undefined});}})()";
    }

    /// <summary>Maps the reply of <see cref="SyncScript" /> to a result. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamCollectionSyncResult ParseSync(SteamUiEvaluationResult result)
    {
        if (result.Dispatch != SteamUiDispatch.Answered)
        {
            return new SteamCollectionSyncResult(
                SteamClientScript.Unread(result.Dispatch), null, 0, result.Error ?? "Steam did not answer.");
        }

        if (result.Error is not null)
        {
            return new SteamCollectionSyncResult(SteamClientWriteOutcome.Rejected, null, 0, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamCollectionSyncResult(SteamClientWriteOutcome.Unknown, null, 0, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            // The id is read whether or not the sync succeeded: a collection made before a later step
            // failed is still the host's.
            var id = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("id", out var idProperty)
                     && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : null;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamCollectionSyncResult(SteamClientWriteOutcome.Rejected, id, 0,
                    SteamClientScript.RefusalOf(root) ?? "Steam rejected the change.");
            }

            var count = root.TryGetProperty("count", out var countProperty)
                        && countProperty.ValueKind == JsonValueKind.Number
                        && countProperty.TryGetInt32(out var value)
                ? value
                : 0;
            return new SteamCollectionSyncResult(SteamClientWriteOutcome.Applied, id, count, null);
        }
        catch (JsonException ex)
        {
            return new SteamCollectionSyncResult(
                SteamClientWriteOutcome.Unknown, null, 0, $"Steam's reply was unreadable: {ex.Message}");
        }
    }

    private static string Ids(IReadOnlyCollection<uint> ids)
    {
        return "[" + string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";
    }
}
