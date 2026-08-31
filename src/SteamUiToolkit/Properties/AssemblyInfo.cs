using System.Runtime.CompilerServices;

// The debug-port ownership check and the listener table it reads are internal on purpose: they are
// how this library decides it is talking to Steam and not to a squatter, and neither is something a
// consumer should call or depend on the shape of. They are still worth testing directly, because
// the alternative is proving that logic only against a live Steam client.
[assembly: InternalsVisibleTo("SteamUiToolkit.Tests")]
