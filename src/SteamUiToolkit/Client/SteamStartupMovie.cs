using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Steam's own startup movie choice, as its client settings hold it.</summary>
/// <param name="MovieId">
///     <c>startup_movie_id</c>: the Points Shop item, or empty for a local movie or none.
/// </param>
/// <param name="LocalPath"><c>startup_movie_local_path</c>: the movie Steam plays, or empty for its default.</param>
/// <param name="Shuffle"><c>startup_movie_shuffle</c>: whether Steam picks one of its movies at each start.</param>
public sealed record SteamStartupMovieChoice(string MovieId, string LocalPath, bool Shuffle)
{
    /// <summary>Whether Steam plays its default movie, which is when it asks for the override at all.</summary>
    public bool IsDefault => MovieId.Length == 0 && LocalPath.Length == 0 && !Shuffle;
}

/// <summary>Outcome of setting Steam's own startup movie choice aside or putting it back.</summary>
/// <param name="Outcome">Whether the change was never sent, may have run, was refused or completed.</param>
/// <param name="Choice">
///     What was set aside or put back, or null when there was nothing to change: Steam already played
///     its default when setting aside, or holds a newer choice of the user's when putting back. A
///     set-aside that failed after its first write still carries the choice Steam held, so the caller can
///     keep it and give it back later.
/// </param>
/// <param name="Error">Why it was not sent or not applied, or Steam's own error. Null on success.</param>
public readonly record struct SteamStartupMovieResult(
    SteamClientWriteOutcome Outcome,
    SteamStartupMovieChoice? Choice,
    string? Error)
{
    /// <summary>Whether Steam answered that the change completed.</summary>
    public bool Succeeded => Outcome == SteamClientWriteOutcome.Applied;
}

/// <summary>Sets Steam's own startup movie choice aside so the override plays, and puts it back.</summary>
/// <remarks>
///     <para>
///         Steam's client HEAD-requests <c>/uioverrides/movies/&lt;name&gt;</c> for its startup movie, and
///         then lets the choice on Settings &gt; Customization replace whatever that answered: a Points
///         Shop or local movie in <c>startup_movie_local_path</c>, or one of them at random under
///         <c>startup_movie_shuffle</c>. An override only plays while that choice is Steam's default.
///     </para>
///     <para>
///         Both calls write through the client settings store's own setter, the one Steam's Customization
///         page uses (<c>GetClientSetting(name)[1]</c>, which sends <c>SteamClient.Settings.SetSetting</c>).
///         Setting aside answers what Steam held, for the caller to keep; putting back writes it only
///         while Steam still holds the default, so a choice the user made since stays theirs. Mapped from
///         the September 2026 client bundle on 2026-09-28.
///     </para>
///     <para>
///         Steam's settings store can lag its window by a few seconds after a start, so both scripts wait
///         for it, polling every 250 ms for up to five seconds, before they answer that it has not loaded.
///     </para>
/// </remarks>
public sealed class SteamStartupMovie
{
    private const int SettingsWaitMs = 5_000;
    private const int SettingsPollMs = 250;

    // The three settings, read as Steam holds them: an unset item id reads as "0" or empty.
    private static readonly string ReadChoice =
        "let s=window.settingsStore,c=s&&s.clientSettings;" +
        "for(let waited=0;(!c||typeof s.GetClientSetting!=='function')&&waited<" +
        SettingsWaitMs.ToString(CultureInfo.InvariantCulture) + ";waited+=" +
        SettingsPollMs.ToString(CultureInfo.InvariantCulture) + "){" +
        "await new Promise(r=>setTimeout(r," + SettingsPollMs.ToString(CultureInfo.InvariantCulture) + "));" +
        "s=window.settingsStore;c=s&&s.clientSettings;}" +
        "if(!c||typeof s.GetClientSetting!=='function')" +
        "return JSON.stringify({ok:false,err:'Steam has not loaded its settings yet.'});" +
        "const id=String(c.startup_movie_id??'');" +
        "const now={movieId:id==='0'?'':id,localPath:String(c.startup_movie_local_path??'')," +
        "shuffle:c.startup_movie_shuffle===true};" +
        "const plain=v=>!v.movieId&&!v.localPath&&!v.shuffle;" +
        "const set=async v=>{await s.GetClientSetting('startup_movie_id')[1](v.movieId);" +
        "await s.GetClientSetting('startup_movie_local_path')[1](v.localPath);" +
        "await s.GetClientSetting('startup_movie_shuffle')[1](v.shuffle);};";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly SteamClient _client;

    /// <summary>Creates the startup-movie ownership façade without opening a connection.</summary>
    /// <param name="client">Owning client; borrowed for dispatch and lifetime, never disposed by this façade.</param>
    internal SteamStartupMovie(SteamClient client)
    {
        _client = client;
    }

    /// <summary>Puts Steam on its default startup movie and answers the choice it held.</summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome, with the choice set aside. Never throws for an unreachable target.</returns>
    public async Task<SteamStartupMovieResult> SetAsideAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.ReadAsync(SetAsideScript(), Budget, cancellationToken).ConfigureAwait(false);
        return Parse(result);
    }

    /// <summary>Gives Steam back a choice set aside, unless the user has chosen anew since.</summary>
    /// <param name="choice">What <see cref="SetAsideAsync" /> answered.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome, with the choice put back or null when Steam's newer one stayed.</returns>
    public async Task<SteamStartupMovieResult> RestoreAsync(
        SteamStartupMovieChoice choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var result = await _client.ReadAsync(RestoreScript(choice), Budget, cancellationToken).ConfigureAwait(false);
        return Parse(result);
    }

    /// <summary>The script <see cref="SetAsideAsync" /> runs.</summary>
    /// <remarks>
    ///     The first setter changes Steam's choice, so every failure after it answers the choice Steam
    ///     held; the caller would otherwise lose the user's movie for good.
    /// </remarks>
    /// <returns>An async expression clearing a nondefault choice and preserving it in the reply, even after a partial failure.</returns>
    internal static string SetAsideScript()
    {
        return SteamClientScript.Read(
            ReadChoice +
            "if(plain(now))return JSON.stringify({ok:true,choice:null});" +
            "try{await set({movieId:'',localPath:'',shuffle:false});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e),choice:now});}" +
            "return JSON.stringify({ok:true,choice:now});");
    }

    /// <summary>The script <see cref="RestoreAsync" /> runs.</summary>
    /// <param name="choice">Previously saved choice; its strings are encoded as JavaScript literals.</param>
    /// <returns>An async expression restoring the saved choice only while Steam still has its default choice.</returns>
    internal static string RestoreScript(SteamStartupMovieChoice choice)
    {
        return SteamClientScript.Read(
            ReadChoice +
            "const back={movieId:" + SteamCef.JsString(choice.MovieId) +
            ",localPath:" + SteamCef.JsString(choice.LocalPath) +
            ",shuffle:" + (choice.Shuffle ? "true" : "false") + "};" +
            "if(!plain(now)||plain(back))return JSON.stringify({ok:true,choice:null});" +
            "await set(back);" +
            "return JSON.stringify({ok:true,choice:back});");
    }

    /// <summary>Maps the reply of either script to a result.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>The outcome and any choice carried by the reply, including a choice preserved on failure.</returns>
    internal static SteamStartupMovieResult Parse(SteamUiEvaluationResult result)
    {
        if (result.Dispatch != SteamUiDispatch.Answered)
        {
            return new SteamStartupMovieResult(
                SteamClientScript.Unread(result.Dispatch), null, result.Error ?? "Steam did not answer.");
        }

        if (result.Error is not null)
        {
            return new SteamStartupMovieResult(SteamClientWriteOutcome.Rejected, null, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamStartupMovieResult(SteamClientWriteOutcome.Unknown, null, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            // Read whether or not the change completed: a set-aside that failed after its first write
            // still answers what Steam held.
            SteamStartupMovieChoice? choice = null;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choice", out var held)
                && held.ValueKind == JsonValueKind.Object)
            {
                choice = new SteamStartupMovieChoice(
                    SteamClientScript.StringOf(held, "movieId"),
                    SteamClientScript.StringOf(held, "localPath"),
                    held.TryGetProperty("shuffle", out var shuffle) && shuffle.ValueKind == JsonValueKind.True);
            }

            return SteamClientScript.IsOk(root)
                ? new SteamStartupMovieResult(SteamClientWriteOutcome.Applied, choice, null)
                : new SteamStartupMovieResult(SteamClientWriteOutcome.Rejected, choice,
                    SteamClientScript.RefusalOf(root) ?? "Steam rejected the change.");
        }
        catch (JsonException ex)
        {
            return new SteamStartupMovieResult(
                SteamClientWriteOutcome.Unknown, null, $"Steam's reply was unreadable: {ex.Message}");
        }
    }
}
