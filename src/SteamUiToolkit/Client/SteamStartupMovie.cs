using System;
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
/// <param name="Reachable">Whether a validated Steam target ran the request. An unreachable client changed nothing.</param>
/// <param name="Accepted">Whether Steam completed the change without throwing.</param>
/// <param name="Choice">
///     What was set aside or put back, or null when there was nothing to change: Steam already played
///     its default when setting aside, or holds a newer choice of the user's when putting back.
/// </param>
/// <param name="Error">Why the target was unreachable, or Steam's own error. Null on success.</param>
public readonly record struct SteamStartupMovieResult(
    bool Reachable,
    bool Accepted,
    SteamStartupMovieChoice? Choice,
    string? Error)
{
    /// <summary>Whether Steam was reached and completed the change.</summary>
    public bool Succeeded => Reachable && Accepted;
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
/// </remarks>
public static class SteamStartupMovie
{
    // The three settings, read as Steam holds them: an unset item id reads as "0" or empty.
    private const string ReadChoice =
        "const s=window.settingsStore,c=s&&s.clientSettings;" +
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

    /// <summary>Puts Steam on its default startup movie and answers the choice it held.</summary>
    /// <param name="transport">A specific transport, or null for the session's.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome, with the choice set aside. Never throws for an unreachable target.</returns>
    public static async Task<SteamStartupMovieResult> SetAsideAsync(
        ISteamUiTransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SteamClientScript.EvaluateAsync(
                transport, SteamUiTargetRole.SharedJsContext, SetAsideScript(), Budget, cancellationToken)
            .ConfigureAwait(false);
        return Parse(result);
    }

    /// <summary>Gives Steam back a choice set aside, unless the user has chosen anew since.</summary>
    /// <param name="choice">What <see cref="SetAsideAsync" /> answered.</param>
    /// <param name="transport">A specific transport, or null for the session's.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The outcome, with the choice put back or null when Steam's newer one stayed.</returns>
    public static async Task<SteamStartupMovieResult> RestoreAsync(
        SteamStartupMovieChoice choice,
        ISteamUiTransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var result = await SteamClientScript.EvaluateAsync(
                transport, SteamUiTargetRole.SharedJsContext, RestoreScript(choice), Budget, cancellationToken)
            .ConfigureAwait(false);
        return Parse(result);
    }

    /// <summary>The script <see cref="SetAsideAsync" /> runs. Pure, for tests.</summary>
    internal static string SetAsideScript()
    {
        return SteamClientScript.Read(
            ReadChoice +
            "if(plain(now))return JSON.stringify({ok:true,choice:null});" +
            "await set({movieId:'',localPath:'',shuffle:false});" +
            "return JSON.stringify({ok:true,choice:now});");
    }

    /// <summary>The script <see cref="RestoreAsync" /> runs. Pure, for tests.</summary>
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

    /// <summary>Maps the reply of either script to a result. Pure, for tests.</summary>
    internal static SteamStartupMovieResult Parse(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamStartupMovieResult(false, false, null, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamStartupMovieResult(true, false, null, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamStartupMovieResult(true, false, null,
                    SteamClientScript.ErrorOf(root) ?? "Steam rejected the change.");
            }

            if (!root.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.Object)
            {
                return new SteamStartupMovieResult(true, true, null, null);
            }

            return new SteamStartupMovieResult(true, true,
                new SteamStartupMovieChoice(
                    SteamClientScript.StringOf(choice, "movieId"),
                    SteamClientScript.StringOf(choice, "localPath"),
                    choice.TryGetProperty("shuffle", out var shuffle) && shuffle.ValueKind == JsonValueKind.True),
                null);
        }
        catch (JsonException ex)
        {
            return new SteamStartupMovieResult(true, false, null, ex.Message);
        }
    }
}
