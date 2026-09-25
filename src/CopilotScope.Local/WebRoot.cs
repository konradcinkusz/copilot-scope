namespace CopilotScope.Local;

/// <summary>
/// The dashboard's static files. They are not inside the binary: only the dashboard's own
/// publish produces <c>_framework/blazor.web.js</c>, so packaging copies that <c>wwwroot</c> next
/// to the executable (scripts/package-native.sh).
/// </summary>
internal static class WebRoot
{
    public const string Variable = "COPILOTSCOPE_WEBROOT";

    /// <summary><c>--webroot</c>, then <c>COPILOTSCOPE_WEBROOT</c>, then <c>wwwroot</c> beside the
    /// binary — the layout of the release archive.</summary>
    public static string Resolve(string? option) =>
        Path.GetFullPath(
            !string.IsNullOrWhiteSpace(option) ? option
            : Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnvironment ? fromEnvironment
            : Path.Combine(AppContext.BaseDirectory, "wwwroot"));

    /// <summary>
    /// Whether the dashboard can work from this directory. Without <c>blazor.web.js</c> the pages
    /// still render — once, on the server — and then no control on them ever responds, while
    /// every health check stays green. That has shipped once already (see Dockerfile.dashboard),
    /// which is why its absence is checked for rather than trusted.
    /// </summary>
    public static bool IsComplete(string root) =>
        File.Exists(Path.Combine(root, "_framework", "blazor.web.js"))
        && File.Exists(Path.Combine(root, "app.css"));

    public static string MissingPage(string root) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><title>CopilotScope — dashboard files missing</title>
        <style>body{font:16px/1.5 system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem}code{background:#8882;padding:.1em .3em;border-radius:3px}</style>
        </head>
        <body>
        <h1>The dashboard's files are missing</h1>
        <p>CopilotScope is running and collecting telemetry, but it cannot find the dashboard's static
        files in <code>{{System.Net.WebUtility.HtmlEncode(root)}}</code>, so this page cannot work.</p>
        <p>Reinstall CopilotScope, or start it with <code>--webroot &lt;directory&gt;</code> pointing at the
        <code>wwwroot</code> folder of the release archive.</p>
        </body>
        </html>
        """;
}
