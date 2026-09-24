using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamUiToolkit;

/// <summary>The wire shapes every surface publishes, serialized without reflection.</summary>
/// <remarks>
///     CamelCase because that is what the injected validators read; the performance state's inner
///     objects override it with Valve's snake_case field names explicitly. Element and nested record
///     types are generated from the roots listed here; only the token list the probes serialize directly
///     is named on its own.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamAudioState))]
[JsonSerializable(typeof(SteamAudioFormatState))]
[JsonSerializable(typeof(SteamNetworkState))]
[JsonSerializable(typeof(SteamBluetoothState))]
[JsonSerializable(typeof(SteamBrightnessState))]
[JsonSerializable(typeof(SteamPowerLimitState))]
[JsonSerializable(typeof(SteamPerformanceState))]
[JsonSerializable(typeof(SteamFrameLimitState))]
[JsonSerializable(typeof(SteamVariableRefreshState))]
[JsonSerializable(typeof(SteamResolutionState))]
[JsonSerializable(typeof(SteamPowerProfileState))]
[JsonSerializable(typeof(SteamHybridCoreState))]
[JsonSerializable(typeof(SteamPowerPresetState))]
[JsonSerializable(typeof(SteamAutoTdpState))]
[JsonSerializable(typeof(SteamControllerTargetState))]
[JsonSerializable(typeof(SteamDeviceControlsState))]
[JsonSerializable(typeof(SteamNavigationPanelState))]
[JsonSerializable(typeof(SteamLibraryBadgeState))]
[JsonSerializable(typeof(SteamHomeCarouselState))]
[JsonSerializable(typeof(SteamPageState))]
[JsonSerializable(typeof(SteamExtensionsTabState))]
[JsonSerializable(typeof(SteamExtensionsTabAction))]
[JsonSerializable(typeof(SteamExtensionsTabSetting))]
[JsonSerializable(typeof(SteamGameContextMenuState))]
[JsonSerializable(typeof(SteamStorageState))]
[JsonSerializable(typeof(SteamScreensaverState))]
[JsonSerializable(typeof(IReadOnlyList<SteamSettingsPage>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class SteamSurfaceJsonContext : JsonSerializerContext;
