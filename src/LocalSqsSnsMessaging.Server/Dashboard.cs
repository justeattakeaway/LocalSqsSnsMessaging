using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace LocalSqsSnsMessaging.Server;

internal static class Dashboard
{
    private static readonly Assembly Assembly = typeof(Dashboard).Assembly;

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource '{name}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IResult ServeHtml() => Results.Content(ReadResource("dashboard.html"), "text/html");
    public static IResult ServeCss() => Results.Content(ReadResource("dashboard.css"), "text/css");
    public static IResult ServeJs() => Results.Content(ReadResource("dashboard.js"), "application/javascript");
}
