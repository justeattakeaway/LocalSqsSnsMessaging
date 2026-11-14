// Translated from behavioral/sns/check_confirmed_sqs_subscription_attributes_and_listing_agree_field_for_field.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.confirmed-sqs-subscription-attributes-and-listing-agree-field-for-field</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConfirmedSqsSubscriptionAttributesAndListingAgreeFieldForField
{
    private const string Scenario = "sns-confirmed-sqs-subscription-attributes-an";

    /// <summary>
    /// A confirmed SQS subscription's attributes are exactly nine fields, its listing entry five
    /// agreeing fields, and a raw sibling differs only in RawMessageDelivery; setting a FilterPolicy
    /// adds FilterPolicy and FilterPolicyScope=MessageAttributes, and clearing it removes both.
    /// </summary>
    [Test]
    [DisplayName("sns.confirmed-sqs-subscription-attributes-and-listing-agree-field-for-field")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var caller = ctx.CallerArn;
        var topicArn = await CreateTopic(ctx);
        var (_, plainQ) = await CreateQueueForTopic(ctx, topicArn);
        var (_, rawQ) = await CreateQueueForTopic(ctx, topicArn);
        var plain = await SubscribeAndConfirm(ctx, topicArn, plainQ);
        var raw = await SubscribeAndConfirm(ctx, topicArn, rawQ, attributes: new() { ["RawMessageDelivery"] = "true" });

        var @base = new Dictionary<string, string>
        {
            ["SubscriptionArn"] = plain, ["TopicArn"] = topicArn, ["Endpoint"] = plainQ,
            ["Protocol"] = "sqs", ["Owner"] = ctx.Account, ["SubscriptionPrincipal"] = caller,
            ["PendingConfirmation"] = "false", ["ConfirmationWasAuthenticated"] = "true",
            ["RawMessageDelivery"] = "false",
        };
        var got = await Attributes(sns, plain);
        Ensure(DictEquals(got, @base),
            $"a confirmed plain sqs subscription must report exactly {SortedRepr(@base.Keys)} with these " +
            $"values; got keys {SortedRepr(got.Keys)}, mismatched " +
            $"{SortedRepr(@base.Keys.Where(k => got.GetValueOrDefault(k) != @base[k]))}");
        var gotRaw = await Attributes(sns, raw);
        var wantRaw = new Dictionary<string, string>(@base) { ["SubscriptionArn"] = raw, ["Endpoint"] = rawQ, ["RawMessageDelivery"] = "true" };
        Ensure(DictEquals(gotRaw, wantRaw),
            "the raw sibling must differ only in RawMessageDelivery; mismatched " +
            $"{SortedRepr(wantRaw.Keys.Union(gotRaw.Keys).Where(k => gotRaw.GetValueOrDefault(k) != wantRaw.GetValueOrDefault(k)))}");

        var listed = new Dictionary<string, Dictionary<string, string?>>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            foreach (var sub in page.Subscriptions ?? [])
            {
                // botocore's listing entry: the five modeled fields, present only when on the wire.
                var entry = new Dictionary<string, string?>
                {
                    ["SubscriptionArn"] = sub.SubscriptionArn, ["Owner"] = sub.Owner, ["Protocol"] = sub.Protocol,
                    ["Endpoint"] = sub.Endpoint, ["TopicArn"] = sub.TopicArn,
                };
                listed[sub.SubscriptionArn] = entry.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }

        Ensure(listed.Keys.ToHashSet().SetEquals([plain, raw]),
            $"the topic must list exactly its two subscriptions, got {listed.Count} entries");
        foreach (var (arn, attrs) in new[] { (plain, @base), (raw, wantRaw) })
        {
            var wantEntry = new[] { "SubscriptionArn", "Owner", "Protocol", "Endpoint", "TopicArn" }.ToDictionary(k => k, k => (string?)attrs[k]);
            var entry = listed[arn];
            Ensure(DictEquals(entry, wantEntry),
                "the listing entry must be exactly the five shared fields and agree with " +
                "GetSubscriptionAttributes; mismatched " +
                $"{SortedRepr(wantEntry.Keys.Union(entry.Keys).Where(k => entry.GetValueOrDefault(k) != wantEntry.GetValueOrDefault(k)))}");
        }

        var policy = new JsonObject { ["tier"] = new JsonArray("gold") };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = plain,
            AttributeName = "FilterPolicy",
            AttributeValue = policy.ToJsonString(),
        });
        var withPolicy = await PollUntil(
            () => Attributes(sns, plain),
            a => a.ContainsKey("FilterPolicy"), timeoutS: 30.0, intervalS: 1.0,
            description: "the FilterPolicy becomes readable");
        var added = withPolicy.Keys.Except(@base.Keys).ToList();
        Ensure(added.ToHashSet().SetEquals(["FilterPolicy", "FilterPolicyScope"]),
            "setting a FilterPolicy must add exactly FilterPolicy and FilterPolicyScope, added " +
            $"{SortedRepr(added)}");
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(withPolicy["FilterPolicy"]), policy) && withPolicy["FilterPolicyScope"] == "MessageAttributes",
            $"expected the stored policy {policy.ToJsonString()} under the default scope 'MessageAttributes', got " +
            $"'{withPolicy["FilterPolicy"]}' / '{withPolicy["FilterPolicyScope"]}'");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = plain,
            AttributeName = "FilterPolicy",
            AttributeValue = "",
        });
        var cleared = await PollUntil(
            () => Attributes(sns, plain),
            a => !a.ContainsKey("FilterPolicy"), timeoutS: 30.0, intervalS: 1.0,
            description: "the cleared FilterPolicy disappears");
        Ensure(DictEquals(cleared, @base),
            "clearing the FilterPolicy must restore exactly the original field set (no stale " +
            $"FilterPolicyScope); extra {SortedRepr(cleared.Keys.Except(@base.Keys))}, missing " +
            $"{SortedRepr(@base.Keys.Except(cleared.Keys))}");
    }

    private static async Task<Dictionary<string, string>> Attributes(Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient sns, string subArn)
        => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];

    /// <summary>Python dict equality.</summary>
    private static bool DictEquals<TValue>(Dictionary<string, TValue> a, Dictionary<string, TValue> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && EqualityComparer<TValue>.Default.Equals(kv.Value, v));

    private static string SortedRepr(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>DeleteQueue; a queue the check itself already deleted is the one tolerated outcome.</summary>
    private static async Task DeleteQueueIfPresent(FidelityContext ctx, string url)
    {
        try
        {
            await ctx.Sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "AWS.SimpleQueueService.NonExistentQueue")
        {
        }
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {(value is Dictionary<string, string> d ? $"{{{string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}" : $"'{value}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }
}
