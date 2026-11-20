using Amazon.Runtime;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3SnsSqs;

/// <summary>
/// The s3__sns__sqs checks' teardown, which proves each resource gone: <c>_refused</c>,
/// <c>_poll_refused</c> and the <c>_gone_*</c> helpers.
/// </summary>
internal static class S3SnsTeardown
{
    /// <summary>Bounded convergence for a delete to reach its terminal not-found answer.</summary>
    private const double GoneS = 120.0;

    private static readonly string[] SqsNotFound = ["AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"];
    private const string SqsNotFoundMessage = "The specified queue does not exist.";

    /// <summary>(code, HTTP status, message) of a ClientError.</summary>
    private static (string Code, int Status, string Message) Err(AwsError err) =>
        (err.Code ?? string.Empty, (int)err.StatusCode, S3CoreFidelity.BotocoreMessage(err) ?? string.Empty);

    /// <summary><c>fn()</c> must fail with one of <c>codes</c> at <c>status</c> and exactly <c>message</c>.</summary>
    public static async Task Refused(Func<Task> fn, string[] codes, int status, string what, string message)
    {
        if (await FidelityContext.CaptureError(fn) is not { } err)
        {
            throw Fail($"{what}: expected {string.Join('/', codes)} HTTP {status}, the call succeeded");
        }

        var (code, got, msg) = Err(err);
        Ensure(
            codes.Contains(code) && got == status,
            $"{what}: expected {string.Join('/', codes)} HTTP {status}, got {Py.Repr(code)} HTTP {got}: {Py.Repr(msg.Length > 150 ? msg[..150] : msg)}");
        Ensure(msg == message, $"{what}: {code} message is {Py.Repr(msg.Length > 200 ? msg[..200] : msg)}, expected {Py.Repr(message)}");
    }

    /// <summary>
    /// Poll <c>fn</c> until it reaches the terminal not-found answer, then grade it with
    /// <see cref="Refused"/>. A success while the delete converges is retried; any other error
    /// fails at once.
    /// </summary>
    public static async Task PollRefused(Func<Task> fn, string[] codes, int status, string what, string message, double timeoutS = GoneS)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            if (await FidelityContext.CaptureError(fn) is { } err)
            {
                if (codes.Contains(Err(err).Code))
                {
                    await Refused(fn, codes, status, what, message);
                    return;
                }

                throw err.Exception;
            }

            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{what}: still readable {timeoutS:F0}s after the delete");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    public static async Task GoneSubscription(FidelityContext ctx, string subArn, string topicArn)
    {
        var sns = ctx.Sns;
        await sns.UnsubscribeAsync(subArn);
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetSubscriptionAttributesAsync(subArn), nf, 404, "GetSubscriptionAttributes", "Subscription does not exist");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            foreach (var s in page.Subscriptions ?? [])
            {
                listed.Add(s.SubscriptionArn);
            }
        }

        Ensure(!listed.Contains(subArn), "ListSubscriptionsByTopic still lists the removed subscription");
        await Refused(
            () => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn,
                AttributeName = "RawMessageDelivery",
                AttributeValue = "true",
            }),
            nf, 404, "SetSubscriptionAttributes on removed subscription", "Subscription does not exist");
    }

    public static async Task GoneTopic(FidelityContext ctx, string arn)
    {
        var sns = ctx.Sns;
        await sns.DeleteTopicAsync(arn);
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetTopicAttributesAsync(arn), nf, 404, "GetTopicAttributes", "Topic does not exist");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                listed.Add(t.TopicArn);
            }
        }

        Ensure(!listed.Contains(arn), "ListTopics still lists the deleted topic");
        await Refused(() => sns.PublishAsync(arn, "gone"), nf, 404, "Publish to deleted topic", "Topic does not exist");
    }

    public static async Task GoneQueue(FidelityContext ctx, string name, string url)
    {
        var sqs = ctx.Sqs;
        await sqs.DeleteQueueAsync(url);
        // AWS documents up to 60 s for a deleted queue to stop answering.
        await PollRefused(() => sqs.GetQueueUrlAsync(name), SqsNotFound, 400, $"GetQueueUrl {name}", SqsNotFoundMessage);
        var listed = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name })).QueueUrls ?? [];
        Ensure(
            !listed.Exists(u => u.TrimEnd('/').Split('/')[^1] == name),
            $"ListQueues still lists deleted queue {name}");
        await Refused(() => sqs.SendMessageAsync(url, "gone"), SqsNotFound, 400, $"SendMessage {name}", SqsNotFoundMessage);
    }

    private static async Task EmptyBucket(Amazon.S3.IAmazonS3 s3, string bucket)
    {
        await foreach (var page in s3.Paginators.ListVersions(new ListVersionsRequest { BucketName = bucket }).Responses)
        {
            var objs = (page.Versions ?? []).Select(o => new KeyVersion { Key = o.Key, VersionId = o.VersionId }).ToList();
            if (objs.Count > 0)
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = objs, Quiet = true });
            }
        }
    }

    public static async Task GoneBucket(FidelityContext ctx, string bucket)
    {
        var s3 = ctx.S3;
        await EmptyBucket(s3, bucket);
        await s3.DeleteBucketAsync(bucket);
        // HeadBucket has no body: the code is the bare status.
        await PollRefused(
            () => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }),
            ["404", "NoSuchBucket", "NotFound"], 404, $"HeadBucket {bucket}", "Not Found");
        var names = ((await s3.ListBucketsAsync(new ListBucketsRequest { Prefix = bucket })).Buckets ?? []).Select(b => b.BucketName).ToHashSet();
        Ensure(!names.Contains(bucket), $"ListBuckets still lists deleted bucket {bucket}");
        await Refused(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "gone", ContentBody = "x" }),
            ["NoSuchBucket"], 404, $"PutObject {bucket}", "The specified bucket does not exist");
    }

    /// <summary>A teardown that ignores only the given not-found codes (the body already deleted it).</summary>
    public static Func<Task> Tolerant(Func<Task> fn, params string[] codes) => async () =>
    {
        try
        {
            await fn();
        }
        catch (AmazonServiceException ex) when (codes.Contains(AwsError.From(ex).Code))
        {
        }
    };

    /// <summary><c>_SQS_NF</c>: SQS's codes for a queue that does not exist.</summary>
    internal static readonly string[] SqsNotFoundCodes = SqsNotFound;
}
