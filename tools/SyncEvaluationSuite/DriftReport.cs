namespace SyncEvaluationSuite;

/// <summary>
/// The work list for an update: each new, stale, changed or removed check with what to do about it,
/// the upstream diff, and a local copy of the upstream source to translate from.
/// </summary>
internal static class DriftReport
{
    public static string Render(
        UpstreamRepository upstream,
        string root,
        string workDirectory,
        string previousPinText,
        string pin,
        string? previousPin,
        IReadOnlyList<Row> rows,
        IEnumerable<Port> ports,
        IReadOnlyList<string> newDirectories)
    {
        var allPorts = ports.ToArray();
        var text = new StringBuilder();
        var toDo = rows.Count(row => row.IsToDo) + newDirectories.Count;
        text.Append(CultureInfo.InvariantCulture, $"""
            # Evaluation suite drift

            Upstream [`{previousPinText[..7]}`]({Snapshot.GitHubUrl}/tree/{previousPinText}) → [`{pin[..7]}`]({Snapshot.GitHubUrl}/tree/{pin}): {toDo} to do.


            """);

        if (toDo == 0)
        {
            text.Append("Nothing to do: every tracked check is ported at its upstream version, or has a reason not to be.\n");
            return text.ToString();
        }

        var renames = previousPin is null || previousPin == pin
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : upstream.Renames(previousPin, pin, rows.Select(row => row.Directory).Distinct(StringComparer.Ordinal).ToArray());
        var portsByPath = allPorts.ToDictionary(port => port.UpstreamPath, StringComparer.Ordinal);

        Section(text, rows, RowStatus.New, "New checks",
            "Port each one, following the conventions in README.md, or write a reason into its row in UPSTREAM.md.",
            row =>
            {
                var check = row.Check!;
                text.Append(CultureInfo.InvariantCulture, $"- Upstream: [{check.Path}]({Snapshot.GitHubUrl}/blob/{pin}/{check.Path}); local copy: `{LocalCopy(upstream, root, workDirectory, pin, check.Path)}`\n");
                text.Append(CultureInfo.InvariantCulture, $"- Suggested port: `{Ports.SuggestedFile(check, allPorts)}`\n");
                if (check.Operations.Count > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"- Calls: {string.Join(", ", check.Operations.Select(operation => $"`{operation}`"))}\n");
                }

                if (check.Labels.Count > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"- Labels: {string.Join(", ", check.Labels.Select(label => $"`{label}`"))}\n");
                }

                if (renames.TryGetValue(check.Path, out var oldPath))
                {
                    var oldPort = portsByPath.GetValueOrDefault(oldPath);
                    text.Append(CultureInfo.InvariantCulture, $"- Renamed from `{oldPath}`{(oldPort is null ? "" : $", ported as `{oldPort.File}`: move that port rather than writing a new one")}\n");
                    AppendDiff(text, upstream.Diff(previousPin!, pin, oldPath, check.Path));
                }
            });

        Section(text, rows, RowStatus.Stale, "Stale ports",
            $"Upstream changed these checks since the commit in the port's header. Apply the change to the port, then set the commit in its header to `{pin[..7]}`.",
            row =>
            {
                var check = row.Check!;
                text.Append(CultureInfo.InvariantCulture, $"- Port: `{row.Port!.File}`, ported at `{row.Port.Commit}`\n");
                text.Append(CultureInfo.InvariantCulture, $"- Upstream: [{check.Path}]({Snapshot.GitHubUrl}/blob/{pin}/{check.Path}); local copy: `{LocalCopy(upstream, root, workDirectory, pin, check.Path)}`\n");
                AppendDiff(text, upstream.TryResolve(row.Port.Commit) is { } from ? upstream.Diff(from, pin, check.Path) : null);
            });

        Section(text, rows, RowStatus.Review, "Checks not ported that changed upstream",
            "Check that the reason still holds (or port the check now), then set the row's status in UPSTREAM.md back to `not ported`.",
            row =>
            {
                var check = row.Check!;
                text.Append(CultureInfo.InvariantCulture, $"- Reason: {row.Reason}\n");
                text.Append(CultureInfo.InvariantCulture, $"- Upstream: [{check.Path}]({Snapshot.GitHubUrl}/blob/{pin}/{check.Path}); local copy: `{LocalCopy(upstream, root, workDirectory, pin, check.Path)}`\n");
                AppendDiff(text, previousPin is not null && previousPin != pin ? upstream.Diff(previousPin, pin, check.Path) : null);
            });

        Section(text, rows, RowStatus.RemovedUpstream, "Removed upstream",
            "Upstream no longer has these checks. Delete the port, unless a new check above is its rename.",
            row => text.Append(CultureInfo.InvariantCulture, $"- Port: `{row.Port!.File}` (was `{row.Port.UpstreamPath}`)\n"));

        if (newDirectories.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"## New upstream directories ({newDirectories.Count})\n\n");
            text.Append("These appeared upstream and involve a service this library emulates. To track one, add a `## behavioral/<directory>` heading to UPSTREAM.md and run the tool again.\n\n");
            foreach (var directory in newDirectories)
            {
                var count = upstream.Tree(pin).Keys.Count(path => path.StartsWith($"behavioral/{directory}/check_", StringComparison.Ordinal));
                text.Append(CultureInfo.InvariantCulture, $"- [`{directory}`]({Snapshot.GitHubUrl}/tree/{pin}/behavioral/{directory}): {count} check{(count == 1 ? "" : "s")}\n");
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, IReadOnlyList<Row> rows, RowStatus status, string title, string instruction, Action<Row> describe)
    {
        var selected = rows.Where(row => row.Status == status).OrderBy(row => row.CheckId, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"## {title} ({selected.Length})\n\n{instruction}\n\n");
        foreach (var row in selected)
        {
            text.Append(CultureInfo.InvariantCulture, $"### `{row.CheckId}`\n\n");
            describe(row);
            text.Append('\n');
        }
    }

    private static void AppendDiff(StringBuilder text, string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            text.Append("- No upstream diff available (the commit it was ported at is gone upstream, or this ran offline): compare the port with the local copy.\n");
            return;
        }

        text.Append("\n```diff\n").Append(diff.TrimEnd('\n')).Append("\n```\n");
    }

    /// <summary>Writes the check's source at the pin under the work directory, for translating from.</summary>
    private static string LocalCopy(UpstreamRepository upstream, string root, string workDirectory, string pin, string path)
    {
        var target = Path.Combine(workDirectory, "upstream", path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, upstream.Show(pin, path));
        }
        catch (SyncException)
        {
            return "(not available offline)";
        }

        return Path.GetRelativePath(root, target).Replace('\\', '/');
    }
}
