// Translated from behavioral/events/check_update_archive_pattern_is_mirrored_into_its_managed_rule_and_delete_removes_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-archive-pattern-is-mirrored-into-its-managed-rule-and-delete-removes-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateArchivePatternIsMirroredIntoItsManagedRuleAndDeleteRemovesIt
{
    private const string Check = "update-archive-pattern-is-mirrored-into-its-managed-rule-and-delete-removes-it";
    private const double RuleAppearTimeoutS = 60.0;
    private const double RuleConvergeTimeoutS = 60.0;
    private const double ArchiveDeleteTimeoutS = 180.0;

    /// <summary>
    /// UpdateArchive rewrites the pattern of the archive's managed rule (plus the replay exclusion),
    /// an empty pattern leaves only the exclusion, and DeleteArchive removes the rule.
    /// </summary>
    [Test]
    [DisplayName("events.update-archive-pattern-is-mirrored-into-its-managed-rule-and-delete-removes-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus, Tags = [new EventBridgeTag { Key = "sim2real", Value = Check }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var first = FidelityContext.UniqueName("sim2real.first.");
        var second = FidelityContext.UniqueName("sim2real.second.");
        var archive = FidelityContext.UniqueName("sim2real-arc-");
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive, EventSourceArn = busArn, EventPattern = $$"""{"source": ["{{first}}"]}""",
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, archive, missingOk: true));
        var managed = $"Events-Archive-{archive}";
        JsonObject Exclusion() => new() { ["replay-name"] = new JsonArray(new JsonObject { ["exists"] = false }) };

        var rule = await PollForRule(events, bus, managed);
        var firstWithExclusion = Merge(new JsonObject { ["source"] = new JsonArray(first) }, Exclusion());
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(rule.EventPattern), firstWithExclusion),
            "before any update the managed rule must mirror the archive pattern plus the replay " +
            $"exclusion, got '{rule.EventPattern}'");

        JsonObject NewPattern() => new() { ["source"] = new JsonArray(second), ["detail-type"] = new JsonArray("sim2real-archived") };
        await events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archive, EventPattern = NewPattern().ToJsonString() });
        var described = await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive });
        Ensure(
            described.EventPattern is not null && JsonNode.DeepEquals(JsonNode.Parse(described.EventPattern), NewPattern()),
            $"DescribeArchive after UpdateArchive reports {Quote(described.EventPattern)}, expected " +
            $"{NewPattern().ToJsonString()}");
        var updated = await PollRulePattern(events, bus, managed, Merge(NewPattern(), Exclusion()));
        Ensure(
            updated.State?.Value == "ENABLED",
            $"UpdateArchive left the managed rule in state '{updated.State?.Value}'");

        // An empty EventPattern clears the archive's pattern; DescribeArchive then omits the field.
        await events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archive, EventPattern = "" });
        var cleared = await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive });
        Ensure(
            cleared.EventPattern is null,
            "an empty EventPattern must clear the archive's pattern, but DescribeArchive still " +
            $"reports {Quote(cleared.EventPattern)}");
        await PollRulePattern(events, bus, managed, Exclusion());

        await DeleteArchiveAndWait(events, archive);
        var started = FidelityTime.StartNew();
        List<string>? remaining = null;
        while (started.Elapsed < TimeSpan.FromSeconds(RuleConvergeTimeoutS))
        {
            remaining = (await ListRules(events, bus, "Events-Archive-")).Select(r => r.Name).ToList();
            if (remaining.Count == 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(
            remaining is { Count: 0 },
            $"DeleteArchive must remove the archive's managed rule; bus '{bus}' still lists " +
            $"[{string.Join(", ", (remaining ?? []).Select(n => $"'{n}'"))}] {RuleConvergeTimeoutS:F1}s later");
        var (code, message) = await Error(() => events.DescribeRuleAsync(new DescribeRuleRequest { Name = managed, EventBusName = bus }));
        var expected = ("ResourceNotFoundException", $"Rule {managed} does not exist on EventBus {bus}.");
        Ensure(
            (code, message) == expected,
            $"DescribeRule on the deleted archive's managed rule gave '{code}': '{message}', " +
            $"expected ('{expected.Item1}', '{expected.Item2}')");
    }

    /// <summary><c>{**a, **b}</c>.</summary>
    private static JsonObject Merge(JsonObject a, JsonObject b)
    {
        foreach (var (key, value) in b.ToList())
        {
            b.Remove(key);
            a[key] = value;
        }

        return a;
    }

    private static string Quote(string? value) => value is null ? "None" : $"'{value}'";

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<DescribeRuleResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {{'Name': '{result!.Name}', 'EventPattern': {Quote(result.EventPattern)}}}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary><c>get_paginator("list_rules").paginate(EventBusName=bus, NamePrefix=prefix)</c>, flattened.</summary>
    private static async Task<List<Rule>> ListRules(AmazonEventBridgeClient events, string bus, string prefix)
    {
        var rules = new List<Rule>();
        string? token = null;
        do
        {
            var page = await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = prefix, NextToken = token });
            rules.AddRange(page.Rules ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return rules;
    }

    private static async Task<DescribeRuleResponse> PollRulePattern(AmazonEventBridgeClient events, string bus, string name, JsonObject expected)
    {
        var started = FidelityTime.StartNew();
        DescribeRuleResponse? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(RuleConvergeTimeoutS))
        {
            last = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = name, EventBusName = bus });
            if (JsonNode.DeepEquals(JsonNode.Parse(string.IsNullOrEmpty(last.EventPattern) ? "null" : last.EventPattern), expected))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"the archive's managed rule '{name}' never converged to pattern {expected.ToJsonString()} within " +
            $"{RuleConvergeTimeoutS:F1}s; last read {Quote(last?.EventPattern)}");
    }

    private static async Task<Rule> PollForRule(AmazonEventBridgeClient events, string bus, string name)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(RuleAppearTimeoutS))
        {
            foreach (var rule in await ListRules(events, bus, "Events-Archive-"))
            {
                if (rule.Name == name)
                {
                    return rule;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"CreateArchive never produced its managed rule '{name}' on bus '{bus}' within " +
            $"{RuleAppearTimeoutS:F1}s");
    }

    /// <summary>DeleteArchive is asynchronous and the bus cannot be deleted while it still exists.</summary>
    private static async Task DeleteArchiveAndWait(AmazonEventBridgeClient events, string name, bool missingOk = false)
    {
        try
        {
            await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name });
        }
        catch (AmazonServiceException exc) when (missingOk && exc.ErrorCode == "ResourceNotFoundException")
        {
        }

        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(ArchiveDeleteTimeoutS))
        {
            try
            {
                await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"archive {name} still exists {ArchiveDeleteTimeoutS:F1}s after DeleteArchive");
    }
}
