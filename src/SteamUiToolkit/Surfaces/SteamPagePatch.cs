namespace SteamUiToolkit;

/// <summary>The gate patch of a host's own page, declared with <c>registerSteamPage</c>.</summary>
/// <remarks>
///     A page's gate is the same patch for every host. Its probe is the page gate's own: it resolves
///     the page's <c>components(runtime)</c> and requires every name in its <c>required</c> list, the
///     one list the page declares, and never calls <c>prepare</c>. Install, verification and removal
///     go through the same gate.
/// </remarks>
public static class SteamPagePatch
{
    /// <summary>Declares the patch for one page.</summary>
    /// <param name="patchId">The page's patch and state identity.</param>
    /// <param name="gateName">The name the page's gate registers under.</param>
    /// <param name="fingerprint">The structural fingerprint reported on a positive probe.</param>
    /// <param name="subject">What the page is called in diagnostics.</param>
    /// <returns>The patch.</returns>
    public static ISteamUiPatch Create(
        string patchId,
        string gateName,
        string fingerprint,
        string subject)
    {
        return new SteamGatePatch(patchId, gateName, fingerprint, subject);
    }
}
