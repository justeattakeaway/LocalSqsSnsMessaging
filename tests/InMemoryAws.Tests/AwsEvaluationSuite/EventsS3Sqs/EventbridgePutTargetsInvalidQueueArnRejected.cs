// Translated from behavioral/events__s3__sqs/check_eventbridge_put_targets_invalid_queue_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.eventbridge-put-targets-invalid-queue-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgePutTargetsInvalidQueueArnRejected
{
    private const string ManagedTagKey = "sim2real:managed";

    private const string BadArnMessage = "Parameter not-an-arn-at-all is not valid. Reason: Provided Arn is not in correct format.";

    /// <summary>
    /// PutTargets rejects a malformed target ARN with ValidationException and its exact message and
    /// attaches no target of that batch, while the well-formed target alone is accepted.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.eventbridge-put-targets-invalid-queue-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("queue-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { [ManagedTagKey] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var busName = FidelityContext.UniqueName("bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = ManagedTagKey, Value = "true" }] });
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("rule-badarn-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = new JsonObject { ["source"] = new JsonArray("app.orders"), ["detail-type"] = new JsonArray("OrderPlaced") }.ToJsonString(),
            Tags = [new Tag { Key = ManagedTagKey, Value = "true" }],
        });
        ctx.OnCleanup(() => CleanupRule(ctx, ruleName, busName));

        var err = await ClientError(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets =
            [
                new Target { Id = "good-target", Arn = queueArn },
                new Target { Id = "bad-target", Arn = "not-an-arn-at-all" },
            ],
        }));
        Ensure(
            err == ("ValidationException", BadArnMessage),
            $"PutTargets must reject a syntactically invalid target ARN with ValidationException {Py.Repr(BadArnMessage)}, got {err}");

        var afterReject = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [];
        Ensure(
            afterReject.Count == 0,
            $"a rejected PutTargets must not partially apply -- expected no targets attached, found [{string.Join(", ", afterReject.Select(t => t.Id))}]");

        var ok = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "good-target", Arn = queueArn }],
        });
        Ensure(
            (ok.FailedEntryCount ?? 1) == 0,
            $"the identical well-formed SQS target must be accepted once the malformed ARN is removed from the batch: FailedEntryCount={ok.FailedEntryCount}");

        var afterAccept = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [])
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        Ensure(
            afterAccept.SetEquals(["good-target"]),
            $"expected exactly the well-formed target registered, got {{{string.Join(", ", afterAccept.Select(Py.Repr))}}}");
    }

    /// <summary>(error code, message) of a call expected to fail; RAISES if the call succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {result}");
    }

    /// <summary>Remove any attached targets, then delete the rule (DeleteRule refuses a rule with targets).</summary>
    private static async Task CleanupRule(FidelityContext ctx, string ruleName, string busName)
    {
        var events = ctx.Events;
        try
        {
            var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets ?? [];
            if (targets.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = [.. targets.Select(t => t.Id)] });
            }
        }
        catch (AmazonEventBridgeException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
        }

        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName, Force = true });
    }
}
