using System;
using System.IO;

namespace SteamUiToolkit;

/// <summary>Supplies the toolkit's module resolver to standalone feature expressions.</summary>
/// <remarks>The returned function resolves a literal module id only when its factory exists.
/// Its <c>resolve(tokens)</c> method inspects source and requires exactly one match before loading
/// exports. <c>count(tokens)</c> and <c>findUnique(tokens)</c> never execute factories. Missing,
/// ambiguous and failed resolutions throw diagnostic errors. Factory presence does not prove
/// dependency readiness; the host must also enforce its startup attachment policy.</remarks>
public static class SteamUiModuleResolver
{
    private static readonly string Source = ReadSource();

    /// <summary>Builds an expression that creates a resolver in the current Steam document.</summary>
    /// <param name="scope">Diagnostic label, encoded as a JavaScript string.</param>
    /// <returns>A JavaScript expression using the same source as the injected toolkit bridge.</returns>
    public static string CreateExpression(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return $"({Source})({SteamCef.JsString(scope)})";
    }

    private static string ReadSource()
    {
        using var stream = typeof(SteamUiModuleResolver).Assembly.GetManifestResourceStream(
            "SteamUiToolkit.ModuleResolver.js")
            ?? throw new InvalidOperationException("The Steam module resolver resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
