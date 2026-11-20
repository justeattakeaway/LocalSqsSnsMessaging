// Translated from behavioral/s3/check_notification_fifo_destination_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsTag = Amazon.SimpleNotificationService.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-fifo-destination-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationFifoDestinationRejected
{
    private const string Check = "notification-fifo-destination-rejected";

    /// <summary>
    /// PutBucketNotificationConfiguration refuses a FIFO SQS queue destination and a FIFO SNS topic
    /// destination with the exact error, and applies neither; a standard queue or topic with the
    /// same permission is accepted.
    /// </summary>
    [Test]
    [DisplayName("s3.notification-fifo-destination-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var sns = ctx.Sns;
        var bucket = await NewBucket(ctx, s3);

        // ---- positive control: a standard (non-FIFO) queue is accepted ----
        var (_, okQueueArn) = await NewQueue(ctx, sqs, bucket, fifo: false);
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = okQueueArn, Events = ["s3:ObjectCreated:*"] }],
        });
        var control = await s3.GetBucketNotificationAsync(bucket);
        var controlArns = (control.QueueConfigurations ?? []).Select(c => c.Queue).ToList();
        Ensure(
            controlArns.SequenceEqual([okQueueArn]),
            $"positive control: a standard queue must be accepted, got {Py.Repr(controlArns)}");
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket });

        // ---- break exactly one thing: the SAME permission, on a FIFO queue ----
        var (_, fifoQueueArn) = await NewQueue(ctx, sqs, bucket, fifo: true);
        // botocore reports S3's ArgumentName and ArgumentValue, which the .NET SDK drops, so this call
        // goes over raw HTTP to read the same error document.
        var (code, message, argName, argValue) = Error(await S3Fidelity.PutNotificationConfigurationRaw(
            ctx, bucket, S3Fidelity.QueueConfigurationXml(fifoQueueArn, ["s3:ObjectCreated:*"])));
        Ensure(code == "InvalidArgument", $"expected InvalidArgument for a FIFO queue destination, got {Py.Repr(code)}");
        Ensure(message == "FIFO SQS queues are not supported.", message ?? "None");
        Ensure(argName == "Queue", $"expected ArgumentName 'Queue', got {Py.Repr(argName)}");
        Ensure(argValue == fifoQueueArn, $"expected ArgumentValue {Py.Repr(fifoQueueArn)}, got {Py.Repr(argValue)}");
        var afterQueue = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            afterQueue.QueueConfigurations is not { Count: > 0 },
            $"a rejected PutBucketNotificationConfiguration must not partially apply, got {Py.Repr(afterQueue.QueueConfigurations?.Select(c => c.Queue).ToList())}");

        // ---- positive control: a standard (non-FIFO) topic is accepted ----
        var okTopicArn = await NewTopic(ctx, sns, bucket, fifo: false);
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            TopicConfigurations = [new TopicConfiguration { Topic = okTopicArn, Events = ["s3:ObjectCreated:*"] }],
        });
        var control2 = await s3.GetBucketNotificationAsync(bucket);
        var control2Arns = (control2.TopicConfigurations ?? []).Select(c => c.Topic).ToList();
        Ensure(
            control2Arns.SequenceEqual([okTopicArn]),
            $"positive control: a standard topic must be accepted, got {Py.Repr(control2Arns)}");
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket });

        // ---- break exactly one thing: the SAME permission, on a FIFO topic ----
        var fifoTopicArn = await NewTopic(ctx, sns, bucket, fifo: true);
        var (code2, message2, argName2, argValue2) = Error(await S3Fidelity.PutNotificationConfigurationRaw(
            ctx, bucket, S3Fidelity.TopicConfigurationXml(fifoTopicArn, ["s3:ObjectCreated:*"])));
        Ensure(code2 == "InvalidArgument", $"expected InvalidArgument for a FIFO topic destination, got {Py.Repr(code2)}");
        Ensure(
            message2 == "The ARN failed to satisfy constraint: Member must satisfy regular expression pattern: "
                + "^arn:aws[\\p{Alpha}\\-]*:sns:[\\p{Alnum}\\-]+:[\\p{Digit}]{12}:[\\p{Alnum}\\-_]{1,256}$",
            message2 ?? "None");
        Ensure(argName2 == "Topic", $"expected ArgumentName 'Topic', got {Py.Repr(argName2)}");
        Ensure(argValue2 == fifoTopicArn, $"expected ArgumentValue {Py.Repr(fifoTopicArn)}, got {Py.Repr(argValue2)}");
        var afterTopic = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            afterTopic.TopicConfigurations is not { Count: > 0 },
            $"a rejected PutBucketNotificationConfiguration must not partially apply, got {Py.Repr(afterTopic.TopicConfigurations?.Select(c => c.Topic).ToList())}");
    }

    /// <summary>Create a uniquely-named, tagged bucket, register teardown, return its name.</summary>
    private static async Task<string> NewBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Amazon.S3.Model.Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    /// <summary>
    /// Create a uniquely-named queue (FIFO or standard), permit S3 to send to it, tag it, register
    /// teardown, return (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> NewQueue(FidelityContext ctx, AmazonSQSClient sqs, string bucket, bool fifo)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-q-");
        if (fifo)
        {
            name += ".fifo";
        }

        var request = new CreateQueueRequest { QueueName = name };
        if (fifo)
        {
            request.Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" };
        }

        var queueUrl = (await sqs.CreateQueueAsync(request)).QueueUrl;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, queueUrl));
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new() { ["sim2real"] = Check } });
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });
        return (queueUrl, queueArn);
    }

    /// <summary>
    /// Create a uniquely-named topic (FIFO or standard), permit S3 to publish to it, tag it,
    /// register teardown, return the topic ARN.
    /// </summary>
    private static async Task<string> NewTopic(FidelityContext ctx, AmazonSimpleNotificationServiceClient sns, string bucket, bool fifo)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-t-");
        if (fifo)
        {
            name += ".fifo";
        }

        var request = new CreateTopicRequest { Name = name, Tags = [new SnsTag { Key = "sim2real", Value = Check }] };
        if (fifo)
        {
            request.Attributes = new() { ["FifoTopic"] = "true" };
        }

        var topicArn = (await sns.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteTopic(sns, topicArn));
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "Policy",
            AttributeValue = S3Fidelity.NotificationAllowS3Policy(topicArn, bucket, "SNS:Publish"),
        });
        return topicArn;
    }

    /// <summary>
    /// (Error.Code, Error.Message, Error.ArgumentName, Error.ArgumentValue) of the refusal. An
    /// unexpected success raises, so acceptance never reads as a code mismatch.
    /// </summary>
    private static (string? Code, string? Message, string? ArgumentName, string? ArgumentValue) Error(NotificationError? err)
    {
        if (err is null)
        {
            throw Fail("expected the call to be refused, but it succeeded and returned {}");
        }

        return (err.Code, err.Message, err.Get("ArgumentName"), err.Get("ArgumentValue"));
    }
}
