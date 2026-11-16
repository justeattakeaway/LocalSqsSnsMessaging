using System.Text.RegularExpressions;

namespace SyncEvaluationSuite;

internal enum RowStatus
{
    Ported,
    KnownGap,
    NotPorted,
    New,
    Stale,
    Review,
    RemovedUpstream,
}

/// <summary>One row of the snapshot: an upstream check (or a port of one upstream no longer has) and what became of it.</summary>
internal sealed record Row(string Directory, string CheckId, UpstreamCheck? Check, Port? Port, string? Reason, RowStatus Status)
{
    public bool IsToDo => Status is RowStatus.New or RowStatus.Stale or RowStatus.Review or RowStatus.RemovedUpstream;
}

/// <summary>What the tool reads back from the snapshot: the pin, the tracked directories, and the hand-written reasons.</summary>
internal sealed record PreviousRow(string CheckId, string Status, string? Reason)
{
    public bool IsReview => Status.StartsWith("**review**", StringComparison.Ordinal);
}

internal sealed partial record Snapshot(string? Pin, IReadOnlyList<string> Directories, IReadOnlyDictionary<string, PreviousRow> Rows)
{
    public const string GitHubUrl = "https://github.com/aws/aws-emulator-evaluation-suite";

    public static Snapshot Parse(string text)
    {
        string? pin = null;
        var directories = new List<string>();
        var rows = new Dictionary<string, PreviousRow>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith(PinPrefix, StringComparison.Ordinal) && Sha().Match(line) is { Success: true } sha)
            {
                pin = sha.Value;
            }
            else if (line.StartsWith("## behavioral/", StringComparison.Ordinal))
            {
                directories.Add(line["## behavioral/".Length..].Trim());
            }
            else if (directories.Count > 0 && line.StartsWith("| `", StringComparison.Ordinal))
            {
                var cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
                if (cells.Length < 6)
                {
                    throw new SyncException($"Can't read this row of the snapshot: {line}");
                }

                var reason = cells[4] is "" or "—" || PortCell().IsMatch(cells[4]) ? null : cells[4];
                var checkId = cells[1].Trim('`');
                rows[checkId] = new PreviousRow(checkId, cells[3], reason);
            }
        }

        return new Snapshot(pin, directories.Distinct(StringComparer.Ordinal).ToArray(), rows);
    }

    public static string Render(string pin, IReadOnlyList<string> directories, IReadOnlyList<Row> rows)
    {
        var text = new StringBuilder();
        text.Append($$"""
            # Upstream checks

            Every check in the tracked directories of AWS's [emulator evaluation suite]({{GitHubUrl}}),
            and what became of it here. `tools/SyncEvaluationSuite` writes this file from upstream and the
            ports' headers; see [README.md](README.md#keeping-up-with-upstream) for how to update the suite.

            {{PinPrefix}} [`{{pin[..7]}}`]({{GitHubUrl}}/tree/{{pin}}) (`{{pin}}`)

            Edit by hand only:

            - **Reasons:** the last cell of a check that isn't ported. Write one into a `**new**` row to
              decide not to port it.
            - **Reviews:** set a `**review**` row (a check that isn't ported changed upstream) back to
              `not ported` once its reason still holds.
            - **Directories:** add a `## behavioral/<directory>` heading to start tracking one.

            Statuses: `ported`, `known gap: <FidelityGap>` (ported, skipped until fixed), `not ported`,
            and the ones to act on: `**new**` (port it or give a reason), `**stale**` (upstream changed the
            check since the commit in its port's header), `**review**` and `**removed upstream**` (delete
            the port). The *Last changed* column is the last upstream commit to change the check.

            ## Summary

            | Directory | Checks | Ported | Known gaps | Not ported | To do |
            |---|---:|---:|---:|---:|---:|

            """);

        var bySection = rows.GroupBy(row => row.Directory, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        int[] totals = new int[5];
        foreach (var directory in directories)
        {
            var counts = Counts(bySection.GetValueOrDefault(directory, []));
            for (var i = 0; i < counts.Length; i++)
            {
                totals[i] += counts[i];
            }

            text.Append(CultureInfo.InvariantCulture, $"| [`{directory}`](#behavioral{directory}) | {string.Join(" | ", counts)} |\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"| **Total** | {string.Join(" | ", totals.Select(total => $"**{total}**"))} |\n");

        text.Append("\n### Not ported, by reason\n\n| Reason | Checks |\n|---|---:|\n");
        foreach (var group in rows.Where(row => row.Reason is not null && row.Port is null && row.Check is not null)
                     .GroupBy(row => row.Reason!, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} |\n");
        }

        text.Append("\n### Known gaps, by root cause\n\n| `FidelityGap` | Checks |\n|---|---:|\n");
        foreach (var group in rows.Where(row => row.Port?.Gap is not null && row.Check is not null)
                     .GroupBy(row => row.Port!.Gap!, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"| `{group.Key}` | {group.Count()} |\n");
        }

        foreach (var directory in directories)
        {
            text.Append(CultureInfo.InvariantCulture, $"""

                ## behavioral/{directory}

                | Check | Last changed | Status | Port or reason |
                |---|---|---|---|

                """);
            foreach (var row in bySection.GetValueOrDefault(directory, []).OrderBy(row => row.CheckId, StringComparer.Ordinal))
            {
                var lastChanged = row.Check is { } check ? $"`{check.LastChanged[..7]}`" : "—";
                var portOrReason = row.Port is { } port ? $"`{port.File}`" : row.Reason ?? "";
                text.Append(CultureInfo.InvariantCulture, $"| `{row.CheckId}` | {lastChanged} | {StatusText(row)} | {portOrReason} |\n");
            }
        }

        return text.ToString();
    }

    /// <summary>The snapshot without its pin line, to tell whether anything but the pin would change.</summary>
    public static string WithoutPin(string text)
        => string.Join('\n', text.Split('\n').Where(line => !line.StartsWith(PinPrefix, StringComparison.Ordinal)));

    private const string PinPrefix = "Pinned commit:";

    private static int[] Counts(Row[] rows)
    {
        var upstream = rows.Where(row => row.Check is not null).ToArray();
        return
        [
            upstream.Length,
            upstream.Count(row => row.Port is not null),
            upstream.Count(row => row.Port?.Gap is not null),
            upstream.Count(row => row.Port is null && row.Reason is not null),
            rows.Count(row => row.IsToDo),
        ];
    }

    private static string StatusText(Row row) => row.Status switch
    {
        RowStatus.Ported => "ported",
        RowStatus.KnownGap => $"known gap: {row.Port!.Gap}",
        RowStatus.NotPorted => "not ported",
        RowStatus.New => "**new**",
        RowStatus.Stale => $"**stale**: ported at `{row.Port!.Commit[..7]}`",
        RowStatus.Review => "**review**: changed upstream",
        RowStatus.RemovedUpstream => "**removed upstream**",
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    [GeneratedRegex(@"^`[^`]+\.cs`$")]
    private static partial Regex PortCell();

    [GeneratedRegex("[0-9a-f]{40}")]
    private static partial Regex Sha();
}
