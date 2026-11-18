using SyncEvaluationSuite;

// Keeps tests/InMemoryAws.Tests/AwsEvaluationSuite/UPSTREAM.md in step with AWS's emulator
// evaluation suite and the ports. See the "Keeping up with upstream" section of that directory's README.
try
{
    return Sync.Run(Options.Parse(args));
}
catch (SyncException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 2;
}

namespace SyncEvaluationSuite
{
    internal sealed record Options(string? Ref, bool Check, string? Report, string Upstream, bool Offline, string? RepositoryRoot)
    {
        public const string Usage = """
            Usage: dotnet run --project tools/SyncEvaluationSuite -- [options]

              (no options)      Refresh UPSTREAM.md at its pinned commit, from the ports as they are now
              --ref <ref>       Move the pin to an upstream branch, tag or commit (e.g. main)
              --check           Write nothing; exit 1 if UPSTREAM.md is out of date or anything is left to do
              --report <path>   Where to write the work list (default artifacts/evaluation-suite/drift.md)
              --upstream <url>  Another clone of the suite to read (default the GitHub repository)
              --offline         Use the cached clone without fetching
              --root <path>     The repository root (default: found from the current directory)
            """;

        public static Options Parse(string[] args)
        {
            string? reference = null, report = null, root = null;
            var upstream = Snapshot.GitHubUrl;
            bool check = false, offline = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new SyncException($"{args[i]} needs a value.\n{Usage}");
                switch (args[i])
                {
                    case "--ref": reference = Value(); break;
                    case "--check": check = true; break;
                    case "--report": report = Value(); break;
                    case "--upstream": upstream = Value(); break;
                    case "--offline": offline = true; break;
                    case "--root": root = Value(); break;
                    default: throw new SyncException($"Unknown option '{args[i]}'.\n{Usage}");
                }
            }

            return new Options(reference, check, report, upstream, offline, root);
        }
    }

    internal static class Sync
    {
        private const string SuitePath = "tests/InMemoryAws.Tests/AwsEvaluationSuite";

        public static int Run(Options options)
        {
            var root = options.RepositoryRoot ?? FindRoot();
            var suiteDirectory = Path.Combine(root, SuitePath);
            var snapshotPath = Path.Combine(suiteDirectory, "UPSTREAM.md");
            var workDirectory = Path.Combine(root, "artifacts", "evaluation-suite");
            var reportPath = options.Report ?? Path.Combine(workDirectory, "drift.md");

            var existing = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath) : throw new SyncException($"{snapshotPath} is missing.");
            var previous = Snapshot.Parse(existing);
            if (previous.Pin is null)
            {
                throw new SyncException($"{snapshotPath} has no 'Pinned commit:' line.");
            }

            var cacheName = options.Upstream == Snapshot.GitHubUrl ? "upstream.git" : $"upstream-{StableHash(options.Upstream)}.git";
            var upstream = UpstreamRepository.Open(Path.Combine(workDirectory, cacheName), options.Upstream, options.Offline);
            var pin = upstream.Resolve(options.Ref ?? previous.Pin);
            var previousPin = upstream.TryResolve(previous.Pin);

            var problems = new List<string>();
            var ports = Ports.Scan(suiteDirectory, problems);
            var checks = upstream.Checks(pin, previous.Directories);
            var rows = BuildRows(upstream, pin, previousPin, previous, checks, ports, problems);
            var newDirectories = NewDirectories(upstream, pin, previousPin, previous.Directories);

            if (problems.Count > 0)
            {
                throw new SyncException($"The ports and the snapshot disagree:\n  {string.Join("\n  ", problems)}");
            }

            var snapshot = Snapshot.Render(pin, previous.Directories, rows);
            var changed = Normalize(Snapshot.WithoutPin(snapshot)) != Normalize(Snapshot.WithoutPin(existing));
            var toDo = rows.Where(row => row.IsToDo).ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, DriftReport.Render(upstream, root, workDirectory, previous.Pin, pin, previousPin, rows, ports.Values, newDirectories));

            Console.WriteLine($"Upstream {previous.Pin[..7]} -> {pin[..7]}: {checks.Count} checks in {previous.Directories.Count} tracked directories.");
            foreach (var group in toDo.GroupBy(row => row.Status).OrderBy(group => group.Key))
            {
                Console.WriteLine($"  {group.Key}: {group.Count()} ({string.Join(", ", group.Take(5).Select(row => row.CheckId))}{(group.Count() > 5 ? ", ..." : "")})");
            }

            foreach (var directory in newDirectories)
            {
                Console.WriteLine($"  New upstream directory, not tracked: behavioral/{directory}");
            }

            Console.WriteLine($"Work list: {Path.GetRelativePath(root, Path.GetFullPath(reportPath))}");

            if (options.Check)
            {
                if (changed)
                {
                    Console.WriteLine("UPSTREAM.md is out of date: run the tool without --check and commit the result.");
                }

                var drift = changed || toDo.Length > 0 || newDirectories.Count > 0;
                Console.WriteLine(drift ? "Drift found." : "No drift.");
                return drift ? 1 : 0;
            }

            if (snapshot != existing)
            {
                File.WriteAllText(snapshotPath, snapshot);
                Console.WriteLine($"Wrote {Path.GetRelativePath(root, snapshotPath)}.");
            }
            else
            {
                Console.WriteLine("UPSTREAM.md is up to date.");
            }

            return 0;
        }

        private static List<Row> BuildRows(
            UpstreamRepository upstream,
            string pin,
            string? previousPin,
            Snapshot previous,
            IReadOnlyList<UpstreamCheck> checks,
            IReadOnlyDictionary<string, Port> ports,
            List<string> problems)
        {
            var tracked = previous.Directories.ToHashSet(StringComparer.Ordinal);
            var previousTree = previousPin is null || previousPin == pin ? null : upstream.Tree(previousPin);
            var rows = new List<Row>();

            foreach (var check in checks)
            {
                var previousRow = previous.Rows.GetValueOrDefault(check.CheckId);
                if (ports.TryGetValue(check.Path, out var port))
                {
                    if (port.DisplayNames is not [var displayName] || displayName != check.CheckId)
                    {
                        problems.Add($"{port.File}: expected one [DisplayName(\"{check.CheckId}\")], found [{string.Join(", ", port.DisplayNames)}].");
                    }

                    var portedFrom = upstream.TryResolve(port.Commit) is { } commit ? upstream.Tree(commit).GetValueOrDefault(check.Path) : null;
                    var status = portedFrom != check.Blob ? RowStatus.Stale
                        : port.Gap is not null ? RowStatus.KnownGap
                        : RowStatus.Ported;
                    rows.Add(new Row(check.Directory, check.CheckId, check, port, null, status));
                    continue;
                }

                var reason = previousRow?.Reason;
                var changedSincePin = previousTree is not null && previousTree.GetValueOrDefault(check.Path) is { } before && before != check.Blob;
                var notPorted = reason is null ? RowStatus.New
                    : previousRow!.IsReview || changedSincePin ? RowStatus.Review
                    : RowStatus.NotPorted;
                rows.Add(new Row(check.Directory, check.CheckId, check, null, reason, notPorted));
            }

            var upstreamPaths = checks.Select(check => check.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var port in ports.Values.Where(port => !upstreamPaths.Contains(port.UpstreamPath)))
            {
                if (!tracked.Contains(port.Directory))
                {
                    problems.Add($"{port.File}: ports {port.UpstreamPath}, but UPSTREAM.md has no '## behavioral/{port.Directory}' section.");
                    continue;
                }

                var checkId = port.DisplayNames.Count > 0 ? port.DisplayNames[0] : port.UpstreamPath;
                rows.Add(new Row(port.Directory, checkId, null, port, null, RowStatus.RemovedUpstream));
            }

            return rows;
        }

        /// <summary>Directories that appeared upstream since the last pin, for a service this library emulates.</summary>
        private static List<string> NewDirectories(UpstreamRepository upstream, string pin, string? previousPin, IReadOnlyList<string> tracked)
        {
            if (previousPin is null || previousPin == pin)
            {
                return [];
            }

            var before = upstream.Directories(previousPin);
            string[] services = ["sqs", "sns", "events", "eventbridgev2", "dynamodb", "dynamodbstreams"];
            return upstream.Directories(pin)
                .Where(directory => !before.Contains(directory) && !tracked.Contains(directory))
                .Where(directory => directory.Split("__").Intersect(services, StringComparer.Ordinal).Any())
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "InMemoryAws.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new SyncException("Run this inside the repository, or pass --root.");
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

        private static string StableHash(string text)
            => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }
}
