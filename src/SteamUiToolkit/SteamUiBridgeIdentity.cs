namespace SteamUiToolkit;

/// <summary>
///     The names the injected side is reachable by: the window property carrying the bridge, and the
///     CDP binding it answers through.
/// </summary>
/// <remarks>
///     Stable names shared by bootstrap, probes and gate expressions. The suffixes avoid collisions
///     with Steam and other injected tools; changing a name can orphan claims from a prior asset.
/// </remarks>
public static class SteamUiBridgeIdentity
{
    /// <summary>The window property the injected bridge is published under.</summary>
    public const string Namespace = "__steamUi_v1_28d7c54a";

    /// <summary>The CDP binding name the injected side sends envelopes through.</summary>
    public const string BindingName = "__steamUiBridge_v1_7b24d11c";
}
