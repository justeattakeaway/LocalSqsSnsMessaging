// Translated from behavioral/sqs/check_queue_deleted_recently_on_fast_recreate_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-deleted-recently-on-fast-recreate-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueDeletedRecentlyOnFastRecreateRejected
{
    private const string CodeRecent = "AWS.SimpleQueueService.QueueDeletedRecently";
    private const string MsgRecent = "You must wait 60 seconds after deleting a queue before you can create another with the same name.";
    private const string CodeMissing = "AWS.SimpleQueueService.NonExistentQueue";
    private const string MsgMissing = "The specified queue does not exist.";

    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "queue-deleted-recently-on-fast-recreate-rejected" };

    /// <summary>(Code, Message) of the ClientError ``fn`` raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {(result is CreateQueueResponse r ? $"{{QueueUrl: {r.QueueUrl}}}" : result)}");
    }

    /// <summary>
    /// CreateQueue with the name of a queue deleted moments earlier is refused with
    /// QueueDeletedRecently and creates nothing; a different fresh name is unaffected.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-deleted-recently-on-fast-recreate-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "CreateQueue with the name of a queue deleted moments earlier succeeded instead of QueueDeletedRecently")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-qdr-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "45" },
            Tags = Tags,
        })).QueueUrl;
        var deleted = new List<bool>();

        ctx.OnCleanup(async () =>
        {
            if (deleted.Count == 0)
            {
                await sqs.DeleteQueueAsync(url);
            }
        });
        await sqs.DeleteQueueAsync(url);
        deleted.Add(true);

        var (code, msg) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = Tags }));
        Ensure(code == CodeRecent, $"recreating a just-deleted name gave code '{code}'");
        Ensure(msg == MsgRecent, $"recreating a just-deleted name gave message '{msg}'");

        (code, msg) = await Err(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }));
        Ensure((code, msg) == (CodeMissing, MsgMissing),
            $"the refused recreate left something resolvable: GetQueueUrl gave ({code}, {msg})");

        // Positive control: the refusal is about the deleted name, not CreateQueue in general.
        var other = FidelityContext.UniqueName("sim2real-behav-qdr-other-");
        var otherUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = other, Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(otherUrl));
        Ensure((await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = other })).QueueUrl == otherUrl,
            "positive control: a fresh name was not resolvable after CreateQueue");
    }
}
