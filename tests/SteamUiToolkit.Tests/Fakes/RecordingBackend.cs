using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>One backend for every surface, recording what reached it in a readable form.</summary>
internal sealed class RecordingBackend :
    ISteamAudioBackend,
    ISteamNetworkBackend,
    ISteamBluetoothBackend,
    ISteamBrightnessBackend,
    ISteamPowerLimitBackend,
    ISteamPerformanceBackend,
    ISteamFrameLimitBackend,
    ISteamVariableRefreshBackend,
    ISteamResolutionBackend,
    ISteamAutoTdpBackend,
    ISteamControllerTargetBackend,
    ISteamDeviceControlsBackend,
    ISteamNavigationPanelBackend,
    ISteamLibraryBadgeBackend,
    ISteamHomeCarouselBackend,
    ISteamScreensaverBackend,
    ISteamStorageBackend,
    ISteamPowerProfileBackend,
    ISteamPowerPresetBackend,
    ISteamHybridCoreBackend,
    ISteamExtensionsTabBackend,
    ISteamGameContextMenuBackend
{
    internal List<string> Calls { get; } = [];

    /// <summary>The cancellation token each call received, in call order.</summary>
    internal List<CancellationToken> Tokens { get; } = [];

    public Task<SteamUiCommandResult> SetDefaultDeviceAsync(string deviceId, bool input,
        CancellationToken cancellationToken)
    {
        return Record($"default {deviceId} {(input ? "input" : "output")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetVolumeAsync(int percent, bool input, CancellationToken cancellationToken)
    {
        return Record($"volume {percent} {(input ? "input" : "output")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Record($"auto {enabled}", cancellationToken);
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

    public Task<SteamUiCommandResult> SetControllerTargetAsync(string target, CancellationToken cancellationToken)
    {
        return Record($"target {target}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetChargeLimitAsync(int percent, CancellationToken cancellationToken)
    {
        return Record($"charge {percent}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetLightingBrightnessAsync(int percent, CancellationToken cancellationToken)
    {
        return Record($"lighting {percent}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetLightingColorAsync(string zone, int color, CancellationToken cancellationToken)
    {
        return Record($"color {zone} {color:X6}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetFrameLimitAsync(int fps, SteamSettingPersistence persistence,
        string correlationId, CancellationToken cancellationToken)
    {
        return Record($"frame {fps} {persistence} {correlationId}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetRefreshRateAsync(int hz, CancellationToken cancellationToken)
    {
        return Record($"refresh {hz}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ReportAsync(SteamHomeCarouselReport report, CancellationToken cancellationToken)
    {
        return Record($"home carousel {report.Items}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetHybridCoresAsync(string option, CancellationToken cancellationToken)
    {
        return Record($"hybrid {option}", cancellationToken);
    }

    public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
    {
        return Record($"home layout {(bigArt ? "big art" : "normal")}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        return Record($"activate {id}", cancellationToken);
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

    public Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken)
    {
        return Record("scan on", cancellationToken);
    }

    public Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken)
    {
        return Record("scan off", cancellationToken);
    }

    public Task<SteamUiCommandResult> ApplyAsync(SteamPerformanceDelta delta, string correlationId,
        CancellationToken cancellationToken)
    {
        return Record(
            $"perf {delta.SteamAppId} " + string.Join(",", delta.Recognized.Select(c => $"{c.Kind}={c.Value}")),
            cancellationToken);
    }

    public Task<SteamUiCommandResult> SetPrimaryLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return Record($"limit {watts}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetBoostLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return Record($"boost {watts}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetAssignmentAsync(bool ac, string? option, CancellationToken cancellationToken)
    {
        return Record($"preset {(ac ? "ac" : "battery")} {option ?? "null"}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
    {
        return Record($"profile {option}", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetResolutionAsync(string option, CancellationToken cancellationToken)
    {
        return Record($"resolution {option}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ReportAsync(SteamScreensaverReport report, CancellationToken cancellationToken)
    {
        return Record(
            $"screensaver report {report.PluggedInSeconds} {report.BatterySeconds?.ToString() ?? "-"} {report.Battery}",
            cancellationToken);
    }

    public Task<SteamUiCommandResult> SetTimeoutAsync(string row, int seconds, CancellationToken cancellationToken)
    {
        return Record($"timeout {row} {seconds}", cancellationToken);
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

    public Task<SteamUiCommandResult> SetVariableRefreshRateAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Record($"vrr {enabled}", cancellationToken);
    }

    public Task<SteamUiCommandResult> ActivateAsync(uint appId, string id, CancellationToken cancellationToken)
    {
        return Record($"game-menu {appId} {id}", cancellationToken);
    }

    private Task<SteamUiCommandResult> Record(string call, CancellationToken cancellationToken)
    {
        Calls.Add(call);
        Tokens.Add(cancellationToken);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
