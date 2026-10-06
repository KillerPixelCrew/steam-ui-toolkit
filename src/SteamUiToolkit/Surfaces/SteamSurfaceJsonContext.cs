using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamUiToolkit;

/// <summary>The wire shapes every surface publishes, serialized without reflection.</summary>
/// <remarks>
///     CamelCase because that is what the injected validators read; the performance state's inner
///     objects override it with Valve's snake_case field names explicitly. Element and nested record
///     types are generated from the roots listed here; a type a surface serializes on its own, outside
///     a state, is listed too: the extension-tab entries, the file picker's answers, the settings pages
///     and the probes' token list.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamAudioState))]
[JsonSerializable(typeof(SteamAudioFormatState))]
[JsonSerializable(typeof(SteamSettingsQuickAccessState))]
[JsonSerializable(typeof(SteamNativeSettingsState))]
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
[JsonSerializable(typeof(SteamCpuBoostState))]
[JsonSerializable(typeof(SteamPowerPresetState))]
[JsonSerializable(typeof(SteamAutoTdpState))]
[JsonSerializable(typeof(SteamControllerTargetState))]
[JsonSerializable(typeof(SteamDeviceControlsState))]
[JsonSerializable(typeof(SteamNavigationPanelState))]
[JsonSerializable(typeof(SteamLibraryBadgeState))]
[JsonSerializable(typeof(SteamHomeCarouselState))]
[JsonSerializable(typeof(SteamPageState))]
[JsonSerializable(typeof(SteamThemeState))]
[JsonSerializable(typeof(SteamSoundOverrideState))]
[JsonSerializable(typeof(SteamSoundOverrideStatus))]
[JsonSerializable(typeof(SteamExtensionsTabState))]
[JsonSerializable(typeof(SteamExtensionsTabAction))]
[JsonSerializable(typeof(SteamExtensionsTabSetting))]
[JsonSerializable(typeof(SteamPanelFoldsState))]
[JsonSerializable(typeof(SteamQuickAccessLayout))]
[JsonSerializable(typeof(SteamGameContextMenuState))]
[JsonSerializable(typeof(SteamPowerMenuState))]
[JsonSerializable(typeof(SteamStorageState))]
[JsonSerializable(typeof(SteamScreensaverState))]
[JsonSerializable(typeof(SteamFilePlaces))]
[JsonSerializable(typeof(SteamFileListing))]
[JsonSerializable(typeof(IReadOnlyList<SteamSettingsPage>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class SteamSurfaceJsonContext : JsonSerializerContext;
