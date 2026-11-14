// Translated from behavioral/events/check_event_bus_dead_letter_config_rejects_non_sqs_arns_and_keeps_prior_config.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.event-bus-dead-letter-config-rejects-non-sqs-arns-and-keeps-prior-config</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventBusDeadLetterConfigRejectsNonSqsArnsAndKeepsPriorConfig
{
    private const string Check = "event-bus-dead-letter-config-rejects-non-sqs-arns-and-keeps-prior-config";

    /// <summary>
    /// Bus DeadLetterConfig must be an SQS ARN: SNS and non-ARN values are refused on update (prior
    /// config kept) and create (no bus); an SQS ARN of a nonexistent queue is stored verbatim.
    /// </summary>
    [Test]
    [DisplayName("events.event-bus-dead-letter-config-rejects-non-sqs-arns-and-keeps-prior-config")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventBuses, "DescribeEventBus omits the DeadLetterConfig the bus was created with")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var queue = FidelityContext.UniqueName("sim2real-behav-dlq-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queue, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            DeadLetterConfig = new DeadLetterConfig { Arn = queueArn },
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        Ensure(await Dlq(events, bus) == queueArn, "positive control: the SQS dead-letter queue must be stored");

        var snsArn = $"arn:aws:sns:{ctx.Region}:{ctx.Account}:{FidelityContext.UniqueName("sim2real-behav-topic-")}";
        (string Arn, string Expected)[] refusals =
        [
            (snsArn, "sns is not supported as a dead letter resource."),
            ("sim2real-not-an-arn",
             "Parameter sim2real-not-an-arn is not valid. Reason: Provided Arn is not in correct format."),
        ];
        foreach (var (config, expected) in refusals)
        {
            var (code, message) = await Error(() => events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, DeadLetterConfig = new DeadLetterConfig { Arn = config } }));
            Ensure(
                (code, message) == ("ValidationException", expected),
                $"UpdateEventBus DeadLetterConfig={{'Arn': '{config}'}} gave '{code}' '{message}', expected '{expected}'");
            Ensure(
                await Dlq(events, bus) == queueArn,
                $"the refused update {{'Arn': '{config}'}} must leave the prior dead-letter queue in place");
        }

        var refusedBus = FidelityContext.UniqueName("sim2real-behav-bus-");
        ctx.OnCleanup(() => DeleteIfPresent(events, refusedBus));
        var (c, m) = await Error(() => events.CreateEventBusAsync(new CreateEventBusRequest { Name = refusedBus, DeadLetterConfig = new DeadLetterConfig { Arn = snsArn } }));
        Ensure(
            (c, m) == ("ValidationException", "sns is not supported as a dead letter resource."),
            $"CreateEventBus with an SNS dead-letter ARN gave '{c}' '{m}'");
        var gone = await Error(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = refusedBus }));
        Ensure(
            gone == ("ResourceNotFoundException", $"Event bus {refusedBus} does not exist."),
            $"the refused CreateEventBus must create nothing; DescribeEventBus gave ('{gone.Code}', '{gone.Message}')");

        var missingQueue = $"arn:aws:sqs:{ctx.Region}:{ctx.Account}:{FidelityContext.UniqueName("sim2real-behav-noq-")}";
        var updated = await events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, DeadLetterConfig = new DeadLetterConfig { Arn = missingQueue } });
        Ensure(
            updated.DeadLetterConfig?.Arn == missingQueue,
            $"an SQS ARN is accepted without an existence check; UpdateEventBus answered {DlqRepr(updated.DeadLetterConfig)}");
        Ensure(await Dlq(events, bus) == missingQueue, "the accepted dead-letter ARN must be stored verbatim");
    }

    /// <summary>The bus's DeadLetterConfig, as its Arn (<c>{"Arn": arn}</c> compares equal exactly when the Arns do).</summary>
    private static async Task<string?> Dlq(IAmazonEventBridge events, string bus) =>
        (await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).DeadLetterConfig?.Arn;

    private static string DlqRepr(DeadLetterConfig? config) => config is null ? "None" : $"{{'Arn': '{config.Arn}'}}";

    /// <summary>Delete the bus if a refused CreateEventBus created it anyway.</summary>
    private static async Task DeleteIfPresent(IAmazonEventBridge events, string name)
    {
        var error = await FidelityContext.CaptureError(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name }));
        if (error is not null)
        {
            if (error.Code == "ResourceNotFoundException")
            {
                return;
            }

            throw error.Exception;
        }

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name });
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
