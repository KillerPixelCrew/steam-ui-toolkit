using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamUiToolkit;

/// <summary>The wire shapes every surface publishes, serialized without reflection.</summary>
/// <remarks>
/// CamelCase because that is what the injected validators read; the performance state's inner
/// objects override it with Valve's snake_case field names explicitly.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamAudioState))]
[JsonSerializable(typeof(SteamAudioDevice))]
[JsonSerializable(typeof(SteamNetworkState))]
[JsonSerializable(typeof(SteamNetworkAccessPoint))]
[JsonSerializable(typeof(SteamBluetoothState))]
[JsonSerializable(typeof(SteamBluetoothDevice))]
[JsonSerializable(typeof(SteamBrightnessState))]
[JsonSerializable(typeof(SteamPowerLimitState))]
[JsonSerializable(typeof(SteamPerformanceState))]
[JsonSerializable(typeof(SteamFrameLimitState))]
[JsonSerializable(typeof(SteamVariableRefreshState))]
[JsonSerializable(typeof(SteamResolutionState))]
[JsonSerializable(typeof(SteamAutoTdpState))]
[JsonSerializable(typeof(SteamControllerTargetState))]
[JsonSerializable(typeof(SteamControllerTargetOption))]
[JsonSerializable(typeof(SteamDeviceControlsState))]
[JsonSerializable(typeof(SteamDeviceRangeState))]
[JsonSerializable(typeof(SteamLightingZoneState))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class SteamSurfaceJsonContext : JsonSerializerContext;
