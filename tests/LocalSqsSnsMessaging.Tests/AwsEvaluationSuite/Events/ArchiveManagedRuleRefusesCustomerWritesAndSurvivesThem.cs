// Translated from behavioral/events/check_archive_managed_rule_refuses_customer_writes_and_survives_them.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.archive-managed-rule-refuses-customer-writes-and-survives-them</summary>
[Category("AwsEvaluationSuite")]
public sealed class ArchiveManagedRuleRefusesCustomerWritesAndSurvivesThem
{
    private const string Check = "archive-managed-rule-refuses-customer-writes-and-survives-them";
    private const double RuleAppearTimeoutS = 60.0;
    private const double ArchiveDeleteTimeoutS = 180.0;

    /// <summary>
    /// The rule CreateArchive makes on its bus is service-managed: every customer write to it fails
    /// with ManagedRuleException and its exact message, and leaves the rule's state, pattern and
    /// single target unchanged; the same writes on a customer rule on that bus succeed.
    /// </summary>
    [Test]
    [DisplayName("events.archive-managed-rule-refuses-customer-writes-and-survives-them")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var queueName = FidelityContext.UniqueName("sim2real-behav-q-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];

        // Positive control: a customer rule on the same bus.
        var ownRule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var source = FidelityContext.UniqueName("sim2real.archive.");
        var ownRuleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ownRule, EventBusName = bus, EventPattern = $$"""{"source": ["{{source}}"]}""",
            State = "ENABLED",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ownRule, EventBusName = bus }));

        var archive = FidelityContext.UniqueName("sim2real-arc-");
        var archivePattern = new JsonObject { ["source"] = new JsonArray(source) };
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive, EventSourceArn = busArn, EventPattern = $$"""{"source": ["{{source}}"]}""",
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, archive));

        var managed = $"Events-Archive-{archive}";
        var managedArn = $"arn:aws:events:{ctx.Region}:{ctx.Account}:rule/{bus}/{managed}";
        var listed = await PollForRule(events, bus, managed);
        Ensure(
            listed.Arn == managedArn,
            $"the archive's managed rule has ARN '{listed.Arn}', expected '{managedArn}'");
        Ensure(
            !string.IsNullOrEmpty(listed.ManagedBy),
            $"the rule CreateArchive made must report who manages it (ManagedBy), got {listed}");
        var expectedPattern = new JsonObject
        {
            ["source"] = new JsonArray(source),
            ["replay-name"] = new JsonArray(new JsonObject { ["exists"] = false }),
        };
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(listed.EventPattern), expectedPattern),
            "the managed rule must match what the archive matches and exclude replayed events: " +
            $"expected {expectedPattern.ToJsonString()}, got '{listed.EventPattern}'");
        var targetsBefore = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = managed, EventBusName = bus })).Targets ?? [];
        Ensure(
            targetsBefore.Select(t => t.Id).SequenceEqual([managed]),
            $"the managed rule must carry exactly one target, Id '{managed}'; got {TargetsRepr(targetsBefore)}");

        // Only DeleteRule and RemoveTargets point at the Force parameter in their refusal.
        var plain = $"{managed} is a managed rule.";
        var forceable = $"{managed} is a managed rule. Set 'force' parameter to true to override.";
        var refusals = new (string Op, Func<Task> Call, string ExpectedMessage)[]
        {
            ("DisableRule", () => events.DisableRuleAsync(new DisableRuleRequest { Name = managed, EventBusName = bus }), plain),
            ("EnableRule", () => events.EnableRuleAsync(new EnableRuleRequest { Name = managed, EventBusName = bus }), plain),
            ("PutRule",
                () => events.PutRuleAsync(new PutRuleRequest
                {
                    Name = managed, EventBusName = bus,
                    EventPattern = """{"source": ["sim2real.hijack"]}""",
                }),
                plain),
            ("DeleteRule", () => events.DeleteRuleAsync(new DeleteRuleRequest { Name = managed, EventBusName = bus }), forceable),
            ("RemoveTargets",
                () => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = managed, EventBusName = bus, Ids = [managed] }),
                forceable),
            ("TagResource",
                () => events.TagResourceAsync(new TagResourceRequest
                {
                    ResourceARN = managedArn, Tags = [new Tag { Key = "sim2real", Value = Check }],
                }),
                $"Tag related operations on the managed rule {managed} is not allowed."),
            ("PutTargets",
                () => events.PutTargetsAsync(new PutTargetsRequest
                {
                    Rule = managed, EventBusName = bus, Targets = [new Target { Id = "sim2real-q", Arn = queueArn }],
                }),
                plain),
        };
        foreach (var (op, call, expectedMessage) in refusals)
        {
            var (code, message) = await Error(call);
            Ensure(
                code == "ManagedRuleException",
                $"{op} on the archive's managed rule '{managed}' must be refused with " +
                $"ManagedRuleException, got '{code}': '{message}'");
            Ensure(
                message == expectedMessage,
                $"{op} on the managed rule was refused with message '{message}', expected " +
                $"'{expectedMessage}'");
        }

        var after = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = managed, EventBusName = bus });
        Ensure(
            after.State?.Value == "ENABLED",
            $"refused DisableRule/PutRule calls changed the managed rule's state to '{after.State?.Value}'");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(after.EventPattern), expectedPattern),
            $"a refused PutRule changed the managed rule's pattern to '{after.EventPattern}'");
        var targetsAfter = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = managed, EventBusName = bus })).Targets ?? [];
        Ensure(
            TargetsRepr(targetsAfter) == TargetsRepr(targetsBefore),
            "refused PutTargets/RemoveTargets changed the managed rule's targets from " +
            $"{TargetsRepr(targetsBefore)} to {TargetsRepr(targetsAfter)}");

        // The same writes on the customer's own rule go through.
        await events.DisableRuleAsync(new DisableRuleRequest { Name = ownRule, EventBusName = bus });
        var ownState = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = ownRule, EventBusName = bus })).State?.Value;
        Ensure(
            ownState == "DISABLED",
            $"positive control: DisableRule on the customer's rule left it '{ownState}'");
        await events.TagResourceAsync(new TagResourceRequest { ResourceARN = ownRuleArn, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        var ownTags = (await events.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = ownRuleArn })).Tags ?? [];
        Ensure(
            ownTags.Count == 1 && ownTags[0].Key == "sim2real" && ownTags[0].Value == Check,
            "positive control: TagResource on the customer's rule left tags " +
            $"[{string.Join(", ", ownTags.Select(t => $"{{'Key': '{t.Key}', 'Value': '{t.Value}'}}"))}]");
    }

    /// <summary>A target list's comparable rendering (the Python compares the target dicts).</summary>
    private static string TargetsRepr(List<Target> targets) =>
        "[" + string.Join(", ", targets.Select(t =>
            $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}', 'RoleArn': '{t.RoleArn}', 'Input': '{t.Input}', 'InputPath': '{t.InputPath}'}}")) + "]";

    /// <summary>(code, message) of the ClientError <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message ?? "");
    }

    private static async Task<Rule> PollForRule(IAmazonEventBridge events, string bus, string name)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(RuleAppearTimeoutS))
        {
            string? token = null;
            do
            {
                var page = await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = "Events-Archive-", NextToken = token });
                foreach (var rule in page.Rules ?? [])
                {
                    if (rule.Name == name)
                    {
                        return rule;
                    }
                }

                token = page.NextToken;
            }
            while (!string.IsNullOrEmpty(token));

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"CreateArchive never produced its managed rule '{name}' on bus '{bus}' within " +
            $"{RuleAppearTimeoutS:0.0}s");
    }

    /// <summary>DeleteArchive is asynchronous and the bus cannot be deleted while it still exists.</summary>
    private static async Task DeleteArchiveAndWait(IAmazonEventBridge events, string name)
    {
        await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name });
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(ArchiveDeleteTimeoutS))
        {
            var error = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"archive {name} still exists {ArchiveDeleteTimeoutS:0.0}s after DeleteArchive");
    }
}
