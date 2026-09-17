using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Result of a live library add through Steam's own client.</summary>
public enum SteamLibraryAddStatus
{
    /// <summary>Steam adopted the folder as a new library.</summary>
    Added,

    /// <summary>The folder already carries a Steam library (nothing to do).</summary>
    AlreadyPresent,

    /// <summary>Steam actively refused the folder; <c>Detail</c> is its reason.</summary>
    Rejected,

    /// <summary>The debug channel could not be reached, so no live add happened.</summary>
    Unavailable
}

/// <summary>Outcome of a live library add.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryAddResult(SteamLibraryAddStatus Status, string? Detail);

/// <summary>Result of removing a live Steam library.</summary>
public enum SteamLibraryRemoveStatus
{
    /// <summary>Steam removed the library and will persist the change.</summary>
    Removed,

    /// <summary>No registration exists at the path.</summary>
    NotPresent,

    /// <summary>Steam actively refused the removal; <c>Detail</c> is its reason.</summary>
    Rejected,

    /// <summary>The debug channel could not be reached, so no live removal happened.</summary>
    Unavailable
}

/// <summary>Outcome of removing a live library.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryRemoveResult(SteamLibraryRemoveStatus Status, string? Detail);

/// <summary>Result of relabeling a live Steam library.</summary>
public enum SteamLibraryLabelStatus
{
    /// <summary>Steam accepted the new label.</summary>
    Applied,

    /// <summary>No registration exists at the path.</summary>
    NotPresent,

    /// <summary>Steam actively refused; <c>Detail</c> is its reason.</summary>
    Rejected,

    /// <summary>The debug channel could not be reached, so nothing changed.</summary>
    Unavailable
}

/// <summary>Outcome of relabeling a live library.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryLabelResult(SteamLibraryLabelStatus Status, string? Detail);

/// <summary>
///     Adds, removes and relabels Steam library folders in the running client through
///     <c>SteamClient.InstallFolder</c>, so Steam adopts, persists, mounts and scans a folder on its own
///     thread with no restart.
/// </summary>
/// <remarks>
///     <para>
///         Steam keys an install folder by its path and never deduplicates the list. A removable volume
///         that goes away leaves its registration behind, unmounted, still carrying the app list and
///         capacity it last had. <c>AddInstallFolder</c> for the same path does not adopt that entry: it
///         appends a second one, and the client then shows the old volume's games with the new volume's
///         capacity until Steam restarts (live-verified 2026-08-20). Every call here therefore selects
///         all registrations at a path, never only the first.
///     </para>
///     <para>
///         <c>nFolderIndex</c> is a stable id, not an array position. Removing one entry does not
///         renumber the others (live-measured 2026-08-23), so several removals from one
///         <c>GetInstallFolders</c> snapshot are correct in order.
///     </para>
/// </remarks>
public static class SteamInstallFolders
{
    // The script's twin of NormalizePath. The two must agree, or a stale registration survives the
    // purge and the duplicate-library defect returns.
    private const string NormalizePathJs =
        @"const norm=p=>String(p||'').replace(/\//g,'\\')"
        + @".replace(/\\+$/,'').toLowerCase();";

    private const string ErrorReply =
        "catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Normalizes a library path for comparison exactly as the injected scripts do: forward slashes
    ///     become backslashes, trailing separators are dropped and case is folded.
    /// </summary>
    /// <param name="path">A library path as stored or as supplied.</param>
    /// <returns>The comparable form, or an empty string for an empty input.</returns>
    public static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>Adds a library folder to the running client and, on success, labels it.</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null or empty for none.</param>
    /// <param name="replaceExisting">
    ///     True when the caller has just created a new library at this path, which makes every earlier
    ///     registration there stale, including one Steam still reports as mounted (it keeps that flag
    ///     while the volume is present). All of them are removed first. False for an ordinary add, which
    ///     removes only unmounted leftovers and adopts a mounted registration instead of adding again:
    ///     Steam refuses a second add there with <c>NotWritableFolder</c>, and removing a live library
    ///     would drop its games from the UI for the length of a rescan.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live outcome.</returns>
    public static async Task<SteamLibraryAddResult> AddAsync(
        string libraryPath,
        string? label = null,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        var result = await SteamUiTransportSession.EvaluateAsync(
                BuildAddExpression(libraryPath, label, replaceExisting),
                Budget,
                cancellationToken)
            .ConfigureAwait(false);
        return !result.Reachable
            ? new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, result.Error)
            : InterpretAdd(result.Value);
    }

    /// <summary>Removes every live registration at a path.</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live outcome.</returns>
    /// <remarks>
    ///     The path is the only selector Steam's folder API offers. A caller that tracks a stronger
    ///     identity (a volume's content id, say) must resolve it to a path, and refuse when that path is
    ///     ambiguous, before calling this.
    /// </remarks>
    public static async Task<SteamLibraryRemoveResult> RemoveAllAtPathAsync(
        string libraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        var result = await SteamUiTransportSession.EvaluateAsync(
                BuildRemoveExpression(libraryPath),
                Budget,
                cancellationToken)
            .ConfigureAwait(false);
        return !result.Reachable
            ? new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, result.Error)
            : InterpretRemove(result.Value);
    }

    /// <summary>
    ///     Relabels the library at a path. A mounted registration wins over a leftover at the same path,
    ///     because Steam lists the leftover first.
    /// </summary>
    /// <param name="libraryPath">The library folder.</param>
    /// <param name="label">The new label.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live outcome.</returns>
    public static async Task<SteamLibraryLabelResult> SetLabelAsync(
        string libraryPath,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        ArgumentNullException.ThrowIfNull(label);
        var result = await SteamUiTransportSession.EvaluateAsync(
                BuildLabelExpression(libraryPath, label),
                Budget,
                cancellationToken)
            .ConfigureAwait(false);
        return !result.Reachable
            ? new SteamLibraryLabelResult(SteamLibraryLabelStatus.Unavailable, result.Error)
            : InterpretLabel(result.Value);
    }

    /// <summary>Builds the add script. Path and label are JSON-encoded so backslashes survive.</summary>
    /// <param name="libraryPath">The library folder.</param>
    /// <param name="label">The label, or null or empty for none.</param>
    /// <param name="replaceExisting">Whether a mounted registration at the path is stale too.</param>
    internal static string BuildAddExpression(string libraryPath, string? label, bool replaceExisting)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        var labelLiteral = string.IsNullOrEmpty(label) ? "null" : SteamCef.JsString(label);
        return "(async()=>{try{const path=" + pathLiteral + ";const l=" + labelLiteral + ";"
               + NormalizePathJs
               + "const target=norm(path);let purged=0;"
               + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
               + "const same=folders.filter(x=>norm(x.strFolderPath)===target);"
               + (replaceExisting ? "const live=null;" : "const live=same.find(x=>x.bIsMounted);")
               + "for(const f of same){if(f===live)continue;"
               + "try{await SteamClient.InstallFolder.RemoveInstallFolder(f.nFolderIndex);purged++;}catch(e){}}"
               + "const i=live?live.nFolderIndex:await SteamClient.InstallFolder.AddInstallFolder(path);"
               + "if(l!==null&&typeof i==='number'&&i>=0){"
               + "try{await SteamClient.InstallFolder.SetFolderLabel(i,l);}catch(e){}}"
               + "return JSON.stringify({ok:true,index:i,purged:purged,existing:!!live});}"
               + ErrorReply + "})()";
    }

    /// <summary>Builds the relabel script.</summary>
    /// <param name="libraryPath">The library folder.</param>
    /// <param name="label">The new label.</param>
    internal static string BuildLabelExpression(string libraryPath, string label)
    {
        return "(async()=>{try{const path=" + SteamCef.JsString(libraryPath) + ";" + NormalizePathJs
               + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
               + "const same=folders.filter(x=>norm(x.strFolderPath)===norm(path));"
               + "const folder=same.find(x=>x.bIsMounted)||same[0];"
               + "if(!folder)return JSON.stringify({ok:true,absent:true});"
               + "await SteamClient.InstallFolder.SetFolderLabel(folder.nFolderIndex,"
               + SteamCef.JsString(label) + ");"
               + "return JSON.stringify({ok:true});}" + ErrorReply + "})()";
    }

    /// <summary>Builds the removal script.</summary>
    /// <param name="libraryPath">The library folder.</param>
    internal static string BuildRemoveExpression(string libraryPath)
    {
        return "(async()=>{try{const path=" + SteamCef.JsString(libraryPath) + ";" + NormalizePathJs
               + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
               + "const same=folders.filter(x=>norm(x.strFolderPath)===norm(path));"
               + "if(!same.length)return JSON.stringify({ok:true,absent:true});"
               + "let removed=0;"
               + "for(const f of same){await SteamClient.InstallFolder.RemoveInstallFolder(f.nFolderIndex);removed++;}"
               + "return JSON.stringify({ok:true,removed:removed});}" + ErrorReply + "})()";
    }

    /// <summary>
    ///     Maps the add reply to a result. <c>DriveAlreadyHasLibrary</c> counts as already present; any
    ///     other refusal carries Steam's own reason code.
    /// </summary>
    /// <param name="jsonValue">The script's reply.</param>
    internal static SteamLibraryAddResult InterpretAdd(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (SteamClientScript.IsOk(root))
            {
                if (root.TryGetProperty("purged", out var purgedCount)
                    && purgedCount.ValueKind == JsonValueKind.Number
                    && purgedCount.TryGetInt32(out var purged) && purged > 0)
                {
                    // This line is what identifies a reader handing the same drive letter to a
                    // different volume.
                    SteamUiLog.Info($"Steam library add: purged {purged} stale registration(s) "
                                    + "at the same path first.");
                }

                if (root.TryGetProperty("existing", out var existing)
                    && existing.ValueKind == JsonValueKind.True)
                {
                    SteamUiLog.Info("Steam library already mounted at this path; adopted it.");
                    return new SteamLibraryAddResult(SteamLibraryAddStatus.AlreadyPresent, "AlreadyMounted");
                }

                SteamUiLog.Info("Steam library added to the live client.");
                return new SteamLibraryAddResult(SteamLibraryAddStatus.Added, null);
            }

            var message = RefusalOf(root);
            if (string.Equals(message, "DriveAlreadyHasLibrary", StringComparison.Ordinal))
            {
                return new SteamLibraryAddResult(SteamLibraryAddStatus.AlreadyPresent, message);
            }

            SteamUiLog.Warn($"Steam rejected the library add: {message ?? "unknown reason"}.");
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Rejected, message);
        }
        catch (JsonException ex)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, ex.Message);
        }
    }

    /// <summary>Maps the relabel reply to a result.</summary>
    /// <param name="jsonValue">The script's reply.</param>
    internal static SteamLibraryLabelResult InterpretLabel(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Unavailable, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (SteamClientScript.IsOk(root))
            {
                return new SteamLibraryLabelResult(
                    IsAbsent(root) ? SteamLibraryLabelStatus.NotPresent : SteamLibraryLabelStatus.Applied,
                    null);
            }

            var message = RefusalOf(root);
            SteamUiLog.Warn($"Steam rejected the library relabel: {message ?? "unknown reason"}.");
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Rejected, message);
        }
        catch (JsonException ex)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Unavailable, ex.Message);
        }
    }

    /// <summary>Maps the removal reply to a result.</summary>
    /// <param name="jsonValue">The script's reply.</param>
    internal static SteamLibraryRemoveResult InterpretRemove(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (SteamClientScript.IsOk(root))
            {
                return new SteamLibraryRemoveResult(
                    IsAbsent(root) ? SteamLibraryRemoveStatus.NotPresent : SteamLibraryRemoveStatus.Removed,
                    null);
            }

            var message = RefusalOf(root);
            SteamUiLog.Warn($"Steam rejected the library removal: {message ?? "unknown reason"}.");
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected, message);
        }
        catch (JsonException ex)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, ex.Message);
        }
    }

    private static bool IsAbsent(JsonElement root)
    {
        return root.TryGetProperty("absent", out var absent) && absent.ValueKind == JsonValueKind.True;
    }

    private static string? RefusalOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("message", out var reason) && reason.ValueKind == JsonValueKind.String)
        {
            return reason.GetString();
        }

        return root.TryGetProperty("result", out var resultCode)
            ? $"EResult {resultCode.GetRawText()}"
            : null;
    }
}
