// Translated from behavioral/sqs/check_queue_delay_seconds_attribute_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-delay-seconds-attribute-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueDelaySecondsAttributeBoundsRejected
{
    private const string Message = "Invalid value for the parameter DelaySeconds.";

    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "queue-delay-seconds-attribute-bounds-rejected" };

    /// <summary>(Code, Message) of the ClientError ``fn`` raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static async Task<string> Get(AmazonSQSClient sqs, string url)
        => ((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["DelaySeconds"] })).Attributes
                ?? throw new KeyNotFoundException("Attributes"))["DelaySeconds"];

    /// <summary>
    /// SetQueueAttributes refuses a DelaySeconds one step outside 0..900, or non-numeric, with
    /// InvalidAttributeValue and its exact message, leaving the stored value unchanged, and accepts both
    /// boundaries; CreateQueue with an out-of-range DelaySeconds is refused the same way and creates no queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-delay-seconds-attribute-bounds-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-delaybounds-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var before = await Get(sqs, url);
        Ensure(before == "0", $"a fresh queue's DelaySeconds was '{before}', expected the default '0'");

        foreach (var bad in new[] { "-1", "901", "abc" })
        {
            var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["DelaySeconds"] = bad } }));
            Ensure(code == "InvalidAttributeValue", $"DelaySeconds={bad} gave code '{code}'");
            Ensure(msg == Message, $"DelaySeconds={bad} gave message '{msg}'");
            var now = await Get(sqs, url);
            Ensure(now == before, $"the refused DelaySeconds={bad} changed the stored value from '{before}' to '{now}'");
        }

        foreach (var ok in new[] { "0", "900" })
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["DelaySeconds"] = ok } });
            var now = await Get(sqs, url);
            Ensure(now == ok, $"boundary DelaySeconds={ok} was accepted but reads back as '{now}'");
            before = now;
        }

        var name = FidelityContext.UniqueName("sim2real-behav-delaycreate-");
        var created = new List<string>();

        ctx.OnCleanup(async () =>
        {
            foreach (var u in created)
            {
                await sqs.DeleteQueueAsync(u);
            }
        });

        async Task<CreateQueueResponse> CreateBad()
        {
            var output = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["DelaySeconds"] = "901" }, Tags = Tags });
            created.Add(output.QueueUrl);
            return output;
        }

        var (c, m) = await Err(CreateBad);
        Ensure(c == "InvalidAttributeValue", $"CreateQueue with DelaySeconds=901 gave code '{c}'");
        Ensure(m == Message, $"CreateQueue with DelaySeconds=901 gave message '{m}'");

        (c, m) = await Err(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }));
        Ensure((c, m) == ("AWS.SimpleQueueService.NonExistentQueue", "The specified queue does not exist."),
            $"the refused CreateQueue left a resolvable queue behind: GetQueueUrl gave ({c}, {m})");
    }
}
