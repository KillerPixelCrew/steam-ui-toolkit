using System.Text.Json;
using System.Runtime.CompilerServices;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>One backend for every surface, recording what reached it in a readable form.</summary>
internal sealed class RecordingBackend :
    ISteamAudioBackend,
    ISteamNativeSettingsBackend,
    ISteamQuickAccessRowsBackend,
    ISteamLightingZonesBackend,
    ISteamNetworkBackend,
    ISteamBluetoothBackend,
    ISteamBrightnessBackend,
    ISteamPerformanceBackend,
    ISteamFrameLimitBackend,
    ISteamNavigationPanelBackend,
    ISteamLibraryBadgeBackend,
    ISteamHomeCarouselBackend,
    ISteamScreensaverBackend,
    ISteamStorageBackend,
    ISteamExtensionsTabBackend,
    ISteamGameContextMenuBackend,
    ISteamPowerMenuBackend,
    ISteamPanelFoldsBackend
{
    internal List<string> Calls { get; } = [];

    /// <summary>The cancellation token each call received, in call order.</summary>
    internal List<CancellationToken> Tokens { get; } = [];

    internal Dictionary<string, SteamUiCommandResult> Results { get; } = new(StringComparer.Ordinal);

    public Task<SteamUiCommandResult> SetDefaultDeviceAsync(string deviceId, bool input,
        CancellationToken cancellationToken)
    {
        return Record($"default {deviceId} {(input ? "input" : "output")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetVolumeAsync(int percent, bool input, CancellationToken cancellationToken)
    {
        return Record($"volume {percent} {(input ? "input" : "output")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetDiscoveringAsync(bool discovering, CancellationToken cancellationToken)
    {
        return Record($"discover {discovering}", cancellationToken);
    }

    public Task<SteamUiCommandResult> PairAsync(string deviceId, CancellationToken cancellationToken)
    {
        return Record($"pair {deviceId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> CancelPairAsync(string deviceId, CancellationToken cancellationToken)
    {
        return Record($"cancel {deviceId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        return Record($"connect {deviceId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> DisconnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        return Record($"disconnect {deviceId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        return Record($"forget {deviceId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetBrightnessAsync(int percent, CancellationToken cancellationToken)
    {
        return Record($"brightness {percent}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetLightingColorAsync(string zone, int color, CancellationToken cancellationToken)
    {
        return Record($"color {zone} {color:X6}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ConfigureAsync(
        string id,
        string key,
        JsonElement value,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        return Record($"configure {id} {key} {value} {expectedRevision}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetFrameLimitAsync(int fps, string correlationId,
        CancellationToken cancellationToken)
    {
        return Record($"frame {fps} {correlationId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetRefreshRateAsync(int hz, CancellationToken cancellationToken)
    {
        return Record($"refresh {hz}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ActivateAsync(uint appId, string id, CancellationToken cancellationToken)
    {
        return Record($"game-menu {appId} {id}", cancellationToken, "gameContext.ActivateAsync");
    }

    public Task<SteamUiCommandResult> ReportAsync(SteamHomeCarouselReport report, CancellationToken cancellationToken)
    {
        return Record($"home carousel {report.Items}", cancellationToken, "homeCarousel.ReportAsync");
    }

    public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
    {
        return Record($"home layout {(bigArt ? "big art" : "normal")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> RecheckAsync(uint appId, CancellationToken cancellationToken)
    {
        return Record($"recheck {appId}", cancellationToken);
    }

    Task<SteamUiCommandResult> ISteamNavigationPanelBackend.ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return Record($"navigation {id}", cancellationToken, "navigation.ActivateAsync");
    }

    Task<SteamUiCommandResult> ISteamExtensionsTabBackend.ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return Record($"extension {id}", cancellationToken, "extensions.ActivateAsync");
    }

    public Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken)
    {
        return Record("scan on", cancellationToken);
    }

    public Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken)
    {
        return Record("scan off", cancellationToken);
    }

    Task<SteamUiCommandResult> ISteamNetworkBackend.ConnectAsync(
        string ssid,
        string? password,
        CancellationToken cancellationToken)
    {
        return Record($"wifi join {ssid} {(password is null ? "open" : "keyed")}", cancellationToken);
    }

    Task<SteamUiCommandResult> ISteamNetworkBackend.DisconnectAsync(CancellationToken cancellationToken)
    {
        return Record("wifi leave", cancellationToken);
    }

    Task<SteamUiCommandResult> ISteamNetworkBackend.ForgetAsync(string ssid, CancellationToken cancellationToken)
    {
        return Record($"wifi forget {ssid}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ApplyAsync(SteamPerformanceDelta delta, string correlationId,
        CancellationToken cancellationToken)
    {
        return Record(
            $"perf {delta.SteamAppId} " + string.Join(",", delta.Recognized.Select(c => $"{c.Kind}={c.Value}")),
            cancellationToken);
    }

    public Task<SteamUiCommandResult> SwitchToDesktopAsync(CancellationToken cancellationToken)
    {
        return Record("switch-to-desktop", cancellationToken);
    }

    public Task<SteamUiCommandResult> ReportAsync(SteamScreensaverReport report, CancellationToken cancellationToken)
    {
        return Record(
            $"screensaver report {report.PluggedInSeconds} {report.BatterySeconds?.ToString() ?? "-"} {report.Battery}",
            cancellationToken, "screensaver.ReportAsync");
    }

    public Task<SteamUiCommandResult> SetTimeoutAsync(string row, int seconds, CancellationToken cancellationToken)
    {
        return Record($"timeout {row} {seconds}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken)
    {
        return Record($"setting {key} {value.GetRawText()}", cancellationToken);
    }

    public Task<SteamUiCommandResult> AdoptAsync(uint driveId, string label, bool validate,
        CancellationToken cancellationToken)
    {
        return Record($"adopt {driveId} '{label}' validate={validate}", cancellationToken);
    }

    public Task<SteamUiCommandResult> EjectAsync(uint blockDeviceId, uint driveId, CancellationToken cancellationToken)
    {
        return Record($"eject {blockDeviceId}/{driveId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> FormatAsync(uint driveId, CancellationToken cancellationToken)
    {
        return Record($"format {driveId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken)
    {
        return Record("trimall", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetFoldedAsync(string id, bool folded, CancellationToken cancellationToken)
    {
        return Record($"{id} {folded}", cancellationToken);
    }

    private Task<SteamUiCommandResult> Record(string call, CancellationToken cancellationToken,
        [CallerMemberName] string operation = "")
    {
        Calls.Add(call);
        Tokens.Add(cancellationToken);
        return Task.FromResult(Results.GetValueOrDefault(operation, SteamUiCommandResult.Applied));
    }
}
