// Translated from behavioral/events/check_put_targets_retry_policy_above_documented_maxima_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

// Lower bounds (-1, 59) are not probed: botocore validates integer `min` client-side, so the
// request never reaches the service. botocore does not validate `max`, so upper bounds do.

/// <summary>events.put-targets-retry-policy-above-documented-maxima-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsRetryPolicyAboveDocumentedMaximaRejected
{
    private const int MaxRetryAttempts = 185;
    private const int MaxEventAgeSeconds = 86400;
    private const string TargetId = "t1";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A RetryPolicy at the documented maxima (185 attempts, 86400 s) is stored verbatim; one
    /// over is refused with ValidationException and leaves the stored policy unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_RetryPolicy.html
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-retry-policy-above-documented-maxima-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;
        var queueArn = $"arn:aws:sqs:{ctx.Region}:{account}:{FidelityContext.UniqueName("sim2real-behav-q-")}";

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = TagsKv() });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }), $"delete event bus {bus}"));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.retry-bounds"]}""",
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => IgnoreNotFound(
            () => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus, Force = true }), $"delete rule {rule}"));

        var atMaximum = (MaxRetryAttempts, MaxEventAgeSeconds);
        var accepted = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = TargetId, Arn = queueArn, RetryPolicy = Policy(atMaximum) }],
        });
        ctx.OnCleanup(() => IgnoreNotFound(
            () => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = [TargetId] }),
            $"remove target {TargetId} from rule {rule}"));
        Ensure(
            accepted.FailedEntryCount == 0,
            $"positive control failed: PutTargets with the documented maxima {Show(atMaximum)} must be " +
            $"accepted, got FailedEntryCount={accepted.FailedEntryCount} FailedEntries={accepted.FailedEntries?.Count}");
        var stored = await StoredRetryPolicy(events, rule, bus);
        Ensure(
            Show(stored) == Show(atMaximum),
            "the accepted RetryPolicy must round-trip byte-identical through ListTargetsByRule: " +
            $"expected {Show(atMaximum)}, got {Show(stored)} — a target that clamps or rewrites the value is " +
            "silently changing delivery behaviour");

        var probeA = await RefusePutTargets(
            events,
            rule,
            bus,
            new Target { Id = TargetId, Arn = queueArn, RetryPolicy = Policy((MaxRetryAttempts + 1, MaxEventAgeSeconds)) },
            label: $"MaximumRetryAttempts={MaxRetryAttempts + 1}");
        var unchangedA = await StoredRetryPolicy(events, rule, bus);
        Ensure(
            Show(unchangedA) == Show(atMaximum),
            "the refused PutTargets must not have partially written: expected the stored policy to " +
            $"still be {Show(atMaximum)}, ListTargetsByRule now reports {Show(unchangedA)}");

        var probeB = await RefusePutTargets(
            events,
            rule,
            bus,
            new Target { Id = TargetId, Arn = queueArn, RetryPolicy = Policy((MaxRetryAttempts, MaxEventAgeSeconds + 1)) },
            label: $"MaximumEventAgeInSeconds={MaxEventAgeSeconds + 1}");
        var unchangedB = await StoredRetryPolicy(events, rule, bus);
        Ensure(
            Show(unchangedB) == Show(atMaximum),
            "the refused PutTargets must not have partially written: expected the stored policy to " +
            $"still be {Show(atMaximum)}, ListTargetsByRule now reports {Show(unchangedB)}");

        // Each ValidationException message names its own field and value; only that member name is
        // left unpinned, as the service spells it in its own casing.
        var (surfaceA, codeA, messageA) = probeA;
        var (surfaceB, codeB, messageB) = probeB;
        Ensure(
            (surfaceA, codeA) == (surfaceB, codeB),
            "the two out-of-range RetryPolicy probes must be refused with the same surface and code: " +
            $"MaximumRetryAttempts probe gave surface='{surfaceA}' code='{codeA}', " +
            $"MaximumEventAgeInSeconds probe gave surface='{surfaceB}' code='{codeB}' — a target that " +
            "refuses one and something else for the other is not applying a single range check");
        Ensure(
            codeA == "ValidationException",
            "an out-of-range RetryPolicy must be refused with ValidationException; got " +
            $"surface='{surfaceA}' code='{codeA}' message='{messageA}'");
        foreach (var (message, value, limit) in new[]
        {
            (messageA, MaxRetryAttempts + 1, MaxRetryAttempts),
            (messageB, MaxEventAgeSeconds + 1, MaxEventAgeSeconds),
        })
        {
            var head = $"1 validation error detected: Value '{value}' at 'targets.1.member.retryPolicy.";
            var tail = $"' failed to satisfy constraint: Member must have value less than or equal to {limit}";
            Ensure(
                message.StartsWith(head, StringComparison.Ordinal) && message.EndsWith(tail, StringComparison.Ordinal),
                $"expected a message starting '{head}' and ending '{tail}', got '{message}'");
        }

        // The refused calls must leave the rule usable rather than wedged.
        var requalified = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = TargetId, Arn = queueArn, RetryPolicy = Policy((0, 60)) }],
        });
        Ensure(
            requalified.FailedEntryCount == 0,
            "after two refusals a legal PutTargets must still succeed on the same rule, got " +
            $"FailedEntryCount={requalified.FailedEntryCount} FailedEntries={requalified.FailedEntries?.Count}");
        var final = await StoredRetryPolicy(events, rule, bus);
        Ensure(
            Show(final) == Show((0, 60)),
            $"the overwrite must replace the stored policy: expected the documented minima, got {Show(final)}");
    }

    private static List<Tag> TagsKv() =>
        [new Tag { Key = ManagedTagKey, Value = "true" }, new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" }];

    private static RetryPolicy Policy((int Attempts, int Age) p) =>
        new() { MaximumRetryAttempts = p.Attempts, MaximumEventAgeInSeconds = p.Age };

    /// <summary>The RetryPolicy of target _TARGET_ID, read through ListTargetsByRule.</summary>
    private static async Task<RetryPolicy?> StoredRetryPolicy(AmazonEventBridgeClient events, string rule, string bus)
    {
        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? [];
        var matching = targets.Where(t => t.Id == TargetId).ToList();
        Ensure(
            matching.Count == 1,
            $"ListTargetsByRule({rule}) must report exactly one target with Id='{TargetId}', got " +
            $"[{string.Join(", ", targets.Select(t => $"'{t.Id}'"))}]");
        return matching[0].RetryPolicy ?? throw new KeyNotFoundException("'RetryPolicy'");
    }

    /// <summary>
    /// Call PutTargets expecting a refusal and raise if it was accepted.
    ///
    /// Returns ("exception"|"entry", code, message).
    /// </summary>
    private static async Task<(string Surface, string? Code, string Message)> RefusePutTargets(
        AmazonEventBridgeClient events, string rule, string bus, Target target, string label)
    {
        var (resp, error) = await FidelityContext.Capture(() =>
            events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [target] }));
        if (error is not null)
        {
            return ("exception", error.Code, error.Message ?? "");
        }

        var failed = resp!.FailedEntries ?? [];
        if (resp.FailedEntryCount is > 0)
        {
            Ensure(
                failed.Count == resp.FailedEntryCount,
                $"FailedEntryCount={resp.FailedEntryCount} but FailedEntries has {failed.Count} rows");
            return ("entry", failed[0].ErrorCode, failed[0].ErrorMessage ?? "");
        }

        throw Fail(
            $"PutTargets ACCEPTED an out-of-range RetryPolicy ({label}) — real EventBridge documents " +
            $"MaximumRetryAttempts 0..{MaxRetryAttempts} and MaximumEventAgeInSeconds " +
            $"60..{MaxEventAgeSeconds}. Response was {{'FailedEntryCount': {resp.FailedEntryCount}}}");
    }

    /// <summary>Run a teardown call; only ResourceNotFoundException (already gone) is tolerated.</summary>
    private static async Task IgnoreNotFound(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not {what}: {error.Code}", error.Exception);
        }
    }

    private static string Show((int Attempts, int Age) p) =>
        $"{{'MaximumRetryAttempts': {p.Attempts}, 'MaximumEventAgeInSeconds': {p.Age}}}";

    private static string Show(RetryPolicy? p) =>
        p is null ? "None" : $"{{'MaximumRetryAttempts': {p.MaximumRetryAttempts}, 'MaximumEventAgeInSeconds': {p.MaximumEventAgeInSeconds}}}";
}
