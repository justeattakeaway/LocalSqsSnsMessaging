// Translated from behavioral/sqs/check_queue_name_eighty_character_boundary_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-name-eighty-character-boundary-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueNameEightyCharacterBoundaryRejected
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string StdMsg = "Can only include alphanumeric characters, hyphens, or underscores. 1 to 80 in length";
    private const string FifoMsg = "The name of a FIFO queue can only include alphanumeric characters, hyphens, or "
                                   + "underscores, must end with .fifo suffix and be 1 to 80 in length.";

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "queue-name-eighty-character-boundary-rejected" };

    /// <summary>
    /// Queue names are at most 80 characters (FIFO including '.fifo'): 80-character names are
    /// created and resolve; 81 characters, or '.fifo' on a standard queue, is refused with
    /// InvalidParameterValue and the per-kind message, and no queue is created.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-name-eighty-character-boundary-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var stem = FidelityContext.UniqueName("sim2real-behav-name80-");

        var std80 = Pad(stem + "-", 80, 's');
        var fifo80 = Pad(stem + "-", 75, 'f') + ".fifo";
        Ensure(std80.Length == 80 && fifo80.Length == 80, "test names are not 80 characters");

        var stdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = std80, Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdUrl));
        var fifoUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifo80,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoUrl));
        foreach (var (name, url) in new[] { (std80, stdUrl), (fifo80, fifoUrl) })
        {
            Ensure(url[(url.LastIndexOf('/') + 1)..] == name,
                $"the URL for an 80-character name ends in {url[(url.LastIndexOf('/') + 1)..].Length} characters");
            Ensure((await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl == url,
                "GetQueueUrl for an 80-character name did not resolve to its queue");
        }

        var std81 = std80 + "s";
        var fifo81 = fifo80[..^5] + "f.fifo";
        var stdFifoSuffix = Pad(stem + "-", 70, 's') + ".fifo";
        foreach (var (name, attrs, msg) in new (string, Dictionary<string, string>, string)[]
                 {
                     (std81, new(), StdMsg),
                     (fifo81, new() { ["FifoQueue"] = "true" }, FifoMsg),
                     (stdFifoSuffix, new(), StdMsg),
                 })
        {
            var err = await Error(() => sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = name,
                Attributes = attrs,
                Tags = Tags(),
            }));
            Ensure((err.Code, err.Message) == ("InvalidParameterValue", msg),
                $"CreateQueue for a {name.Length}-character name (FIFO={attrs.Count > 0}) answered {err}");
            var missing = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }));
            Ensure(missing.Code == NoSuchQueue,
                $"the refused {name.Length}-character name resolves anyway: {missing}");
        }
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static string Pad(string prefix, int length, char fill)
    {
        Ensure(prefix.Length < length, $"prefix '{prefix}' is already {prefix.Length} characters");
        return prefix + new string(fill, length - prefix.Length);
    }
}
