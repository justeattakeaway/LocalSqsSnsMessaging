using System.Diagnostics;
using System.Text.Json;

namespace SyncEvaluationSuite;

/// <summary>One upstream check file at a commit.</summary>
internal sealed record UpstreamCheck(
    string Directory,
    string CheckId,
    string Path,
    string Blob,
    string LastChanged,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Labels);

/// <summary>
/// A cached, blob-less bare clone of the evaluation suite: commits and trees are local, file contents
/// are fetched on demand, so cloning and comparing commits takes a second.
/// </summary>
internal sealed class UpstreamRepository
{
    private readonly string _gitDir;
    private readonly bool _offline;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _treeCache = new(StringComparer.Ordinal);

    private UpstreamRepository(string gitDir, bool offline)
    {
        _gitDir = gitDir;
        _offline = offline;
    }

    public static UpstreamRepository Open(string gitDir, string url, bool offline)
    {
        if (!Directory.Exists(gitDir))
        {
            if (offline)
            {
                throw new SyncException($"No upstream clone at {gitDir}, and --offline forbids cloning one.");
            }

            Console.WriteLine($"Cloning {url} (blob-less) into {gitDir}");
            Exec(null, "clone", "--quiet", "--bare", "--filter=blob:none", url, gitDir);
        }

        var repository = new UpstreamRepository(gitDir, offline);
        if (!offline)
        {
            repository.Git("remote", "set-url", "origin", url);
            repository.Git("fetch", "--quiet", "--tags", "--force", "origin", "+refs/heads/*:refs/heads/*");
        }

        return repository;
    }

    /// <summary>The full SHA of a branch, tag or commit; fetches a full SHA that is not yet local.</summary>
    public string Resolve(string reference)
    {
        if (TryResolve(reference) is { } sha)
        {
            return sha;
        }

        if (!_offline && reference.Length == 40 && reference.All(Uri.IsHexDigit))
        {
            TryGit("fetch", "--quiet", "origin", reference);
            if (TryResolve(reference) is { } fetched)
            {
                return fetched;
            }
        }

        throw new SyncException($"Can't find '{reference}' upstream.");
    }

    public string? TryResolve(string reference)
    {
        foreach (var candidate in new[] { $"refs/heads/{reference}", $"refs/tags/{reference}", reference })
        {
            if (TryGit("rev-parse", "--verify", "--quiet", $"{candidate}^{{commit}}") is { } sha)
            {
                return sha.Trim();
            }
        }

        return null;
    }

    /// <summary>Every file under <c>behavioral/</c> at a commit, by path, as its blob SHA.</summary>
    public IReadOnlyDictionary<string, string> Tree(string commit)
    {
        if (_treeCache.TryGetValue(commit, out var cached))
        {
            return cached;
        }

        var tree = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(Git("ls-tree", "-r", commit, "--", "behavioral/")))
        {
            // <mode> SP <type> SP <object> TAB <path>
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var fields = line[..tab].Split(' ');
            if (fields[1] == "blob")
            {
                tree[line[(tab + 1)..]] = fields[2];
            }
        }

        _treeCache[commit] = tree;
        return tree;
    }

    /// <summary>The directories under <c>behavioral/</c> at a commit.</summary>
    public IReadOnlySet<string> Directories(string commit)
        => Lines(Git("ls-tree", "-d", "--name-only", commit, "behavioral/"))
            .Select(path => path["behavioral/".Length..])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every <c>check_*.py</c> in the given directories at a commit.</summary>
    public IReadOnlyList<UpstreamCheck> Checks(string commit, IReadOnlyCollection<string> directories)
    {
        var manifest = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in Lines(Git("show", $"{commit}:manifest.jsonl")))
        {
            var entry = JsonDocument.Parse(line).RootElement;
            manifest[entry.GetProperty("path").GetString()!] = entry;
        }

        var lastChanged = LastChanged(commit, directories);
        var checks = new List<UpstreamCheck>();
        foreach (var (path, blob) in Tree(commit))
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || !directories.Contains(parts[1])
                || !parts[2].StartsWith("check_", StringComparison.Ordinal) || !parts[2].EndsWith(".py", StringComparison.Ordinal))
            {
                continue;
            }

            string checkId;
            IReadOnlyList<string> operations = [];
            IReadOnlyList<string> labels = [];
            if (manifest.TryGetValue(path, out var entry))
            {
                checkId = entry.GetProperty("check_id").GetString()!;
                operations = Strings(entry, "operations");
                labels = Strings(entry, "labels");
            }
            else
            {
                checkId = $"{parts[1]}.{parts[2]["check_".Length..^".py".Length].Replace('_', '-')}";
            }

            checks.Add(new UpstreamCheck(parts[1], checkId, path, blob, lastChanged[path], operations, labels));
        }

        return checks;
    }

    /// <summary>Checks renamed between two commits, new path to old path.</summary>
    public IReadOnlyDictionary<string, string> Renames(string from, string to, IReadOnlyCollection<string> directories)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = directories.Select(directory => $"behavioral/{directory}/").ToArray();
        foreach (var line in Lines(Git(["diff", "--name-status", "-M", from, to, "--", .. paths])))
        {
            var fields = line.Split('\t');
            if (fields[0].StartsWith('R'))
            {
                renames[fields[2]] = fields[1];
            }
        }

        return renames;
    }

    public string Show(string commit, string path) => Git("show", $"{commit}:{path}");

    /// <summary>The upstream diff of some paths between two commits, following renames among them.</summary>
    public string? Diff(string from, string to, params string[] paths)
        => TryGit(["diff", "--no-color", "-M", from, to, "--", .. paths]);

    private Dictionary<string, string> LastChanged(string commit, IReadOnlyCollection<string> directories)
    {
        var lastChanged = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = directories.Select(directory => $"behavioral/{directory}/").ToArray();
        var current = "";
        foreach (var line in Lines(Git(["log", "--format=commit %H", "--name-only", commit, "--", .. paths])))
        {
            if (line.StartsWith("commit ", StringComparison.Ordinal))
            {
                current = line["commit ".Length..];
            }
            else
            {
                lastChanged.TryAdd(line, current);
            }
        }

        return lastChanged;
    }

    private static string[] Strings(JsonElement entry, string property)
        => entry.TryGetProperty(property, out var values)
            ? values.EnumerateArray().Select(value => value.GetString()!).ToArray()
            : [];

    private static string[] Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string Git(params string[] arguments) => Exec(_gitDir, arguments);

    private string? TryGit(params string[] arguments)
    {
        var (exitCode, output, _) = Run(_gitDir, arguments);
        return exitCode == 0 ? output : null;
    }

    private static string Exec(string? gitDir, params string[] arguments)
    {
        var (exitCode, output, error) = Run(gitDir, arguments);
        if (exitCode != 0)
        {
            throw new SyncException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
        }

        return output;
    }

    private static (int ExitCode, string Output, string Error) Run(string? gitDir, string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (gitDir is not null)
        {
            startInfo.ArgumentList.Add($"--git-dir={gitDir}");
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new SyncException("Can't start git.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}

internal sealed class SyncException : Exception
{
    public SyncException()
    {
    }

    public SyncException(string message)
        : base(message)
    {
    }

    public SyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
