using System.Text.RegularExpressions;

namespace SyncEvaluationSuite;

/// <summary>
/// A C# port of an upstream check: a file in the suite directory whose header names the check it
/// was translated from, and the upstream commit it was translated at.
/// </summary>
internal sealed record Port(string File, string UpstreamPath, string Commit, IReadOnlyList<string> DisplayNames, string? Gap)
{
    public string Directory => UpstreamPath.Split('/')[1];
}

internal static partial class Ports
{
    /// <summary>Every port under the suite directory, by upstream path.</summary>
    public static IReadOnlyDictionary<string, Port> Scan(string suiteDirectory, List<string> problems)
    {
        var ports = new Dictionary<string, Port>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(suiteDirectory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var header = HeaderPattern().Match(text);
            if (!header.Success)
            {
                continue;
            }

            var relative = Path.GetRelativePath(suiteDirectory, file).Replace('\\', '/');
            var commit = CommitPattern().Match(text, header.Index, Math.Min(400, text.Length - header.Index));
            if (!commit.Success)
            {
                problems.Add($"{relative}: its 'Translated from' header names no upstream commit.");
                continue;
            }

            var port = new Port(
                relative,
                header.Groups[1].Value,
                commit.Groups[1].Value,
                DisplayNamePattern().Matches(text).Select(match => match.Groups[1].Value).ToArray(),
                GapPattern().Match(text) is { Success: true } gap ? gap.Groups[1].Value : null);

            if (!ports.TryAdd(port.UpstreamPath, port))
            {
                problems.Add($"{relative}: {ports[port.UpstreamPath].File} is already the port of {port.UpstreamPath}.");
            }
        }

        return ports;
    }

    /// <summary>The file a new port of an upstream check would usually go in.</summary>
    public static string SuggestedFile(UpstreamCheck check, IEnumerable<Port> ports)
    {
        var folder = ports.Where(port => port.Directory == check.Directory)
            .Select(port => port.File.Split('/')[0])
            .GroupBy(name => name, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? Pascal(check.Directory.Split("__"));
        var name = Path.GetFileNameWithoutExtension(check.Path)["check_".Length..];
        return $"{folder}/{Pascal(name.Split('_'))}.cs";
    }

    private static string Pascal(IEnumerable<string> words)
        => string.Concat(words.Where(word => word.Length > 0).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    [GeneratedRegex(@"^// Translated from (behavioral/\S+\.py)", RegexOptions.Multiline)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"\bcommit ([0-9a-f]{7,40})\b")]
    private static partial Regex CommitPattern();

    [GeneratedRegex("""\[DisplayName\("([^"]+)"\)\]""")]
    private static partial Regex DisplayNamePattern();

    [GeneratedRegex(@"\[KnownFidelityGap\(FidelityGap\.(\w+)")]
    private static partial Regex GapPattern();
}
