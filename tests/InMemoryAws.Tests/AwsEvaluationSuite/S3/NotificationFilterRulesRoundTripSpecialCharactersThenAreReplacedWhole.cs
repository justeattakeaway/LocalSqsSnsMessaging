// Translated from behavioral/s3/check_notification_filter_rules_round_trip_special_characters_then_are_replaced_whole.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-filter-rules-round-trip-special-characters-then-are-replaced-whole</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationFilterRulesRoundTripSpecialCharactersThenAreReplacedWhole
{
    private const string FirstPrefix = "incoming/t&m<1>/";
    private const string FirstSuffix = "-été.csv";
    private const string SecondPrefix = "archive/文件/";
    private const string SecondSuffix = ".json";

    /// <summary>
    /// A notification filter's prefix/suffix round-trip reserved XML characters and non-ASCII
    /// text byte for byte, and a second PutBucketNotificationConfiguration replaces the entire
    /// configuration rather than adding to it.
    /// </summary>
    [Test]
    [DisplayName("s3.notification-filter-rules-round-trip-special-characters-then-are-replaced-whole")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-notifenc-");
        var firstArn = await QueueNotifiableBy(ctx, sqs, bucket, "sim2real-notifenc-q1-");
        var secondArn = await QueueNotifiableBy(ctx, sqs, bucket, "sim2real-notifenc-q2-");

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [Configuration("first", firstArn, "s3:ObjectCreated:*", FirstPrefix, FirstSuffix)],
        });
        var firstConfigs = (await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket })).QueueConfigurations ?? [];
        var firstEntry = firstConfigs.FirstOrDefault(c => c.Id == "first")
            ?? throw Fail($"the first queue config was not returned: {Py.Repr(firstConfigs.Select(c => c.Id).ToList())}");
        var firstRules = FilterRules(firstEntry);
        Ensure(
            Py.Equal(firstRules, new Dictionary<string, object?> { ["prefix"] = FirstPrefix, ["suffix"] = FirstSuffix }),
            $"filter rules with reserved/non-ASCII characters did not round-trip exactly: {Py.Repr(firstRules)}");

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [Configuration("second", secondArn, "s3:ObjectRemoved:*", SecondPrefix, SecondSuffix)],
        });
        var secondConfigs = (await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket })).QueueConfigurations ?? [];
        var ids = secondConfigs.Select(c => c.Id).ToHashSet();
        Ensure(
            ids.SetEquals(["second"]),
            $"the second PutBucketNotificationConfiguration left {Py.Repr(ids.ToList())}; expected it to replace the whole configuration, leaving only 'second'");
        var secondEntry = secondConfigs[0];
        Ensure(secondEntry.Queue == secondArn, $"expected the surviving config to point at {Py.Repr(secondArn)}, got {Py.Repr(secondEntry.Queue)}");
        var secondRules = FilterRules(secondEntry);
        Ensure(
            Py.Equal(secondRules, new Dictionary<string, object?> { ["prefix"] = SecondPrefix, ["suffix"] = SecondSuffix }),
            $"the second config's filter rules were {Py.Repr(secondRules)}, expected the second set");
    }

    /// <summary>A queue configuration with one event and a prefix and suffix rule.</summary>
    private static QueueConfiguration Configuration(string id, string queueArn, string eventName, string prefix, string suffix) => new()
    {
        Id = id,
        Queue = queueArn,
        Events = [eventName],
        Filter = new Filter
        {
            S3KeyFilter = new S3KeyFilter
            {
                FilterRules = [new FilterRule { Name = "prefix", Value = prefix }, new FilterRule { Name = "suffix", Value = suffix }],
            },
        },
    };

    /// <summary><c>_queue_notifiable_by(...)</c>: a queue whose policy lets the bucket send it S3 events; its ARN.</summary>
    private static async Task<string> QueueNotifiableBy(FidelityContext ctx, IAmazonSQS sqs, string bucket, string namePrefix)
    {
        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName(namePrefix))).QueueUrl;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, url));
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new Dictionary<string, string> { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(arn, bucket) },
        });
        return arn;
    }

    /// <summary><c>_filter_rules(config)</c>: each rule's lower-cased name to its value.</summary>
    private static Dictionary<string, object?> FilterRules(QueueConfiguration config) =>
        (config.Filter?.S3KeyFilter?.FilterRules ?? []).ToDictionary(r => r.Name.ToLowerInvariant(), r => (object?)r.Value, StringComparer.Ordinal);
}
