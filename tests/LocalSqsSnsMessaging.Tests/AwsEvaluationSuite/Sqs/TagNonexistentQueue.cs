// Translated from behavioral/sqs/check_tag_nonexistent_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-nonexistent-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagNonexistentQueue
{
    // Empirically-confirmed SQS error codes (real AWS returns these legacy
    // "AWS.SimpleQueueService.*" prefixed codes, not the bare Smithy shape names).
    private const string NonexistentQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// TagQueue on a never-created QueueUrl fails with QueueDoesNotExist, and a positive
    /// control on a real queue proves the target does not simply reject every TagQueue call.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-nonexistent-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueryErrorCodes, "expected AWS.SimpleQueueService.NonExistentQueue, got ('QueueDoesNotExist', 'Queue not found.')")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        // Positive control: TagQueue succeeds and persists on a real, existing queue.
        var realUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-tagnx-") })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueue(sqs, realUrl));
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = realUrl, Tags = new() { ["a"] = "b" } });
        var controlTags = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = realUrl })).Tags ?? [];
        Ensure(controlTags.GetValueOrDefault("a") == "b", $"positive control did not persist tag: {Show(controlTags)}");

        var fakeUrl = await FakeQueueUrl(ctx);
        var code = await ErrorCode(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = fakeUrl, Tags = new() { ["a"] = "b" } }));
        Ensure(code == (NonexistentQueue, "The specified queue does not exist."), $"expected {NonexistentQueue}, got {code}");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING a call real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<TagQueueResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }

    /// <summary>Teardown DeleteQueue; an already-deleted queue answers NonExistentQueue.</summary>
    private static async Task DeleteQueue(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == NonexistentQueue)
        {
        }
    }

    /// <summary>
    /// Return a well-formed queue URL that has never been created.
    ///
    /// Derives the URL prefix (scheme/host/account segment) from a real, transient
    /// probe queue so the fabricated URL matches whatever URL scheme the target
    /// implementation uses, while pointing at a queue name that has never existed
    /// (avoiding eventual-consistency windows around real deletions).
    /// </summary>
    private static async Task<string> FakeQueueUrl(FidelityContext ctx, string prefixHint = "nonexistent-")
    {
        var sqs = ctx.Sqs;
        var probeName = FidelityContext.UniqueName("sim2real-behav-probe-");
        var probeUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = probeName })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueue(sqs, probeUrl));
        try
        {
            await sqs.DeleteQueueAsync(probeUrl);
        }
#pragma warning disable CA1031 // Mirrors the check's bare `except Exception: pass`.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        var @base = probeUrl[..probeUrl.LastIndexOf('/')];
        return $"{@base}/{FidelityContext.UniqueName("sim2real-behav-" + prefixHint)}";
    }
}
