// Translated from behavioral/sqs/check_queue_attribute_range_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-attribute-range-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueAttributeRangeBoundsRejected
{
    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "queue-attribute-range-bounds-rejected" };

    // (attribute, refused values, accepted boundary values). Documented ranges: retention 60..1209600
    // seconds, maximum message size 1024..1048576 bytes, long-poll wait 0..20 seconds.
    // KmsDataKeyReusePeriodSeconds is absent: a queue without a KMS key omits it from
    // GetQueueAttributes, so its "unchanged after refusal" half needs an SSE-KMS queue.
    private static readonly (string Name, string[] Refused, string[] Accepted)[] Bounds =
    [
        ("MessageRetentionPeriod", ["59", "1209601"], ["60", "1209600"]),
        ("MaximumMessageSize", ["1023", "1048577"], ["1024", "1048576"]),
        ("ReceiveMessageWaitTimeSeconds", ["21"], ["0", "20"]),
    ];

    /// <summary>(Code, Message) of the ClientError ``fn`` raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{HTTPStatusCode: {result!.HttpStatusCode}}}");
    }

    private static async Task<string?> Get(AmazonSQSClient sqs, string url, string name)
        => ((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [name] })).Attributes
                ?? throw new KeyNotFoundException("Attributes"))
            .GetValueOrDefault(name);

    /// <summary>
    /// SetQueueAttributes refuses one step outside the documented ranges of MessageRetentionPeriod,
    /// MaximumMessageSize and ReceiveMessageWaitTimeSeconds with InvalidAttributeValue and its exact
    /// message, leaving the stored value unchanged, and accepts and reads back each inclusive boundary.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-attribute-range-bounds-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DefaultAttributes, "GetQueueAttributes on a fresh queue omits MessageRetentionPeriod, so the response has no Attributes: KeyError 'Attributes'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-arb-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var (name, refused, accepted) in Bounds)
        {
            var before = await Get(sqs, url, name);
            Ensure(before is not null, $"a fresh queue does not report {name}");
            foreach (var bad in refused)
            {
                var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
                {
                    QueueUrl = url,
                    Attributes = new() { [name] = bad },
                }));
                Ensure(code == "InvalidAttributeValue", $"{name}={bad} gave code '{code}'");
                Ensure(msg == $"Invalid value for the parameter {name}.", $"{name}={bad} gave message '{msg}'");
                var now = await Get(sqs, url, name);
                Ensure(now == before, $"the refused {name}={bad} changed the stored value from '{before}' to '{now}'");
            }

            foreach (var ok in accepted)
            {
                await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { [name] = ok } });
                var now = await Get(sqs, url, name);
                Ensure(now == ok, $"boundary {name}={ok} was accepted but reads back as '{now}'");
                before = now;
            }
        }
    }
}
