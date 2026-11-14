// Translated from behavioral/sns/check_topic_mutable_attributes_round_trip_and_clear.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-mutable-attributes-round-trip-and-clear</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicMutableAttributesRoundTripAndClear
{
    private const string Check = "topic-mutable-attributes-round-trip-and-clear";

    /// <summary>
    /// DisplayName, the KMS key id, TracingConfig and SignatureVersion each round-trip through
    /// SetTopicAttributes, each write changes only its own attribute, and setting DisplayName and the
    /// KMS key id to the empty string clears them back to their defaults.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-mutable-attributes-round-trip-and-clear")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-mutattr-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        // SNS stores the KMS key id without resolving it and nothing is published, so no key exists.
        var alias = $"alias/{FidelityContext.UniqueName("sim2real-behav-sns-key-")}";

        var @base = await Attrs(sns, arn);
        Ensure(@base.GetValueOrDefault("DisplayName") == "", $"a new topic's DisplayName defaults to '', got {Repr(@base.GetValueOrDefault("DisplayName"))}");
        Ensure(!@base.ContainsKey("KmsMasterKeyId"), "a new topic carries no KmsMasterKeyId");
        var unset = new[] { "TracingConfig", "SignatureVersion" }.Where(@base.ContainsKey).Order(StringComparer.Ordinal).ToList();
        Ensure(unset.Count == 0, $"a new topic reports no TracingConfig or SignatureVersion until set, got [{string.Join(", ", unset.Select(k => $"'{k}'"))}]");

        (string Name, string Value)[] writes =
        [
            ("DisplayName", "Alerts"), ("KmsMasterKeyId", alias),
            ("TracingConfig", "Active"), ("SignatureVersion", "2"),
        ];
        var expected = new Dictionary<string, string>();
        foreach (var (name, value) in writes)
        {
            await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = name, AttributeValue = value });
            expected[name] = value;
            var got = await Attrs(sns, arn);
            var seen = expected.Keys.ToDictionary(k => k, got.GetValueOrDefault);
            Ensure(expected.All(kv => seen[kv.Key] == kv.Value), $"after setting {name}, the written attributes read back as {Repr(seen)}");
        }

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "DisplayName", AttributeValue = "" });
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "KmsMasterKeyId", AttributeValue = "" });
        var final = await Attrs(sns, arn);
        Ensure(final.GetValueOrDefault("DisplayName") == "", $"an empty DisplayName must clear it, got {Repr(final.GetValueOrDefault("DisplayName"))}");
        Ensure(!final.ContainsKey("KmsMasterKeyId"),
            $"an empty KmsMasterKeyId must remove the attribute, got {Repr(final.GetValueOrDefault("KmsMasterKeyId"))}");
        var kept = new[] { "TracingConfig", "SignatureVersion" }.ToDictionary(k => k, final.GetValueOrDefault);
        Ensure(kept["TracingConfig"] == "Active" && kept["SignatureVersion"] == "2",
            $"clearing two attributes must leave the other writes in place, got {Repr(kept)}");
    }

    private static async Task<Dictionary<string, string>> Attrs(AmazonSimpleNotificationServiceClient sns, string arn)
        => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";

    private static string Repr(Dictionary<string, string?> values)
        => $"{{{string.Join(", ", values.Select(kv => $"'{kv.Key}': {Repr(kv.Value)}"))}}}";
}
