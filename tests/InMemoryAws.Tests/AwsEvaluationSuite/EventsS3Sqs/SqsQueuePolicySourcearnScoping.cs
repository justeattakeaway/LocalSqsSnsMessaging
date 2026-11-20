// Translated from behavioral/events__s3__sqs/check_sqs_queue_policy_sourcearn_scoping.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.sqs-queue-policy-sourcearn-scoping</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsQueuePolicySourcearnScoping
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string UnvalidatedDestination = "Unable to validate the following destination configurations";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>A queue policy scoped to bucket A's ARN must not authorize notifications from bucket B.</summary>
    [Test]
    [DisplayName("events__s3__sqs.sqs-queue-policy-sourcearn-scoping")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucketA = await CreateBucket(ctx);
        var bucketB = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);

        // Policy only allows SourceArn == bucket_a
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucketA.Arn));

        // Configuring bucket_a's notifications should succeed (source arn matches policy), and an
        // independent read (not the mutating call's own response) must show it actually landed.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucketA.Name,
            QueueConfigurations = [new QueueConfiguration { Queue = queue.Arn, Events = ["s3:ObjectCreated:*"] }],
        });
        var positiveRead = await s3.GetBucketNotificationAsync(bucketA.Name);
        var positiveArns = (positiveRead.QueueConfigurations ?? []).Select(c => c.Queue).ToHashSet();
        Ensure(
            positiveArns.Contains(queue.Arn),
            $"expected {Py.Repr(queue.Arn)} in bucket_a's saved notification config after a " +
            $"policy-matching put, got {Py.Repr(positiveArns.ToList())}");

        // Configuring bucket_b's notifications to the SAME queue must fail: the queue policy
        // does not authorize bucket_b's ARN as a SourceArn.
        var err = await ClientError(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucketB.Name,
            QueueConfigurations = [new QueueConfiguration { Queue = queue.Arn, Events = ["s3:ObjectCreated:*"] }],
        }));
        Ensure(
            err == ("InvalidArgument", UnvalidatedDestination),
            $"expected InvalidArgument {Py.Repr(UnvalidatedDestination)} rejecting bucket_b (queue policy " +
            $"is scoped to bucket_a's ARN, not bucket_b's), got {err}");

        // The rejected call must not have landed: an independent read shows bucket_b still has
        // no saved queue configuration.
        var negativeRead = await s3.GetBucketNotificationAsync(bucketB.Name);
        Ensure(
            negativeRead.QueueConfigurations is not { Count: > 0 },
            $"bucket_b must have no saved queue configuration after the rejected put, got " +
            $"{Py.Repr(negativeRead.QueueConfigurations?.Select(c => c.Queue).ToList())}");
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn);

    private static JsonObject SqsPolicyForS3(string queueArn, string bucketArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowS3SendMessage",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = bucketArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>(error code, message) of a call expected to fail; RAISES if the call succeeds.</summary>
    private static async Task<(string?, string?)> ClientError(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn)
            ?? throw Fail("the call was expected to be refused but succeeded: {}");
        return (err.Code, err.Message);
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = [], Tags = XsvcResourceTags() })).QueueUrl;
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        }));
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }
}
