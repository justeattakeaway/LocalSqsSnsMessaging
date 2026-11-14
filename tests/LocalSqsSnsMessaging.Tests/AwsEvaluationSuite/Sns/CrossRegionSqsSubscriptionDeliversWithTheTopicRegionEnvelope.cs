// Translated from behavioral/sns/check_cross_region_sqs_subscription_delivers_with_the_topic_region_envelope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.cross-region-sqs-subscription-delivers-with-the-topic-region-envelope</summary>
[Category("AwsEvaluationSuite")]
public sealed class CrossRegionSqsSubscriptionDeliversWithTheTopicRegionEnvelope
{
    private const string Check = "cross-region-sqs-subscription-delivers-with-the-topic-region-envelope";

    /// <summary>A second commercial region of the same account (region-pinned by design).</summary>
    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>
    /// A topic delivers to an SQS queue in another region; only the topic region lists the
    /// subscription, and the envelope carries the topic's ARN and an UnsubscribeURL for it.
    /// </summary>
    [Test]
    [DisplayName("sns.cross-region-sqs-subscription-delivers-with-the-topic-region-envelope")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe returns a bare UUID SubscriptionArn instead of '<topic ARN>:<uuid>', so the check cannot read its region")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Region;
        var away = OtherRegion(home);
        var sns = ctx.Sns;
        var snsAway = ctx.SnsClient(away);
        var sqsAway = ctx.Client(away);

        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-xr-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqsAway.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-xr-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqsAway.DeleteQueueAsync(url));
        var qArn = (await sqsAway.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        Ensure(qArn.Split(':')[3] == away, $"the queue ARN's region is '{qArn.Split(':')[3]}', expected '{away}'");
        await sqsAway.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = qArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
                    }),
                }.ToJsonString(),
            },
        });

        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = qArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
        Ensure(sub.Split(':')[3] == home && sub.StartsWith(topic + ":", StringComparison.Ordinal),
            $"the subscription ARN must extend the topic ARN in the topic's region '{home}'; " +
            $"its region is '{sub.Split(':').ElementAtOrDefault(3)}'");
        var listed = new List<Subscription>();
        await foreach (var p in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topic }).Responses)
        {
            listed.AddRange(p.Subscriptions ?? []);
        }

        Ensure(listed.Count == 1 && listed[0].SubscriptionArn == sub && listed[0].Endpoint == qArn,
            $"the topic must list exactly the cross-region subscription; listed {listed.Count}");
        var awayListing = new HashSet<string>();
        await foreach (var p in snsAway.Paginators.ListSubscriptions(new ListSubscriptionsRequest()).Responses)
        {
            foreach (var s in p.Subscriptions ?? [])
            {
                awayListing.Add(s.SubscriptionArn);
            }
        }

        Ensure(!awayListing.Contains(sub), "the queue region's ListSubscriptions lists the topic region's subscription");

        var body = FidelityContext.UniqueName("sim2real-xr-body-");
        var mid = (await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = body, Subject = "cross region" })).MessageId;
        var env = await Receive(sqsAway, url, mid, 120);
        Ensure(env is not null, "the notification never reached the queue in the other region");
        Ensure((string?)env["Type"] == "Notification" && (string?)env["TopicArn"] == topic
            && (string?)env["Message"] == body && (string?)env["Subject"] == "cross region",
            $"the delivered envelope must carry the notification as published; got Type " +
            $"{Repr(env["Type"])}, Subject {Repr(env["Subject"])}, TopicArn matches={((string?)env["TopicArn"] == topic ? "True" : "False")}");
        var unsubscribeUrl = (string?)env["UnsubscribeURL"] ?? "";
        var query = ParseQs(Uri.TryCreate(unsubscribeUrl, UriKind.Absolute, out var unsub) ? unsub.Query : "");
        var subscriptionArns = query.GetValueOrDefault("SubscriptionArn");
        var actions = query.GetValueOrDefault("Action");
        Ensure(subscriptionArns is [var only] && only == sub && actions is ["Unsubscribe"],
            $"the UnsubscribeURL must unsubscribe the cross-region subscription; its query names " +
            $"Action {(actions is null ? "None" : "[" + string.Join(", ", actions.Select(a => $"'{a}'")) + "]")} and {subscriptionArns?.Count ?? 0} SubscriptionArn(s)");
    }

    private static string Repr(JsonNode? value) => value is null ? "None" : $"'{(string?)value}'";

    /// <summary>urllib.parse.parse_qs: name -> every non-blank value.</summary>
    private static Dictionary<string, List<string>> ParseQs(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (value.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(name, out var values))
            {
                result[name] = values = [];
            }

            values.Add(value);
        }

        return result;
    }

    private static async Task<JsonObject?> Receive(AmazonSQSClient sqs, string url, string messageId, double timeoutS)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
                var env = JsonNode.Parse(m.Body)!.AsObject();
                if ((string?)env["MessageId"] == messageId)
                {
                    return env;
                }
            }
        }

        return null;
    }
}
