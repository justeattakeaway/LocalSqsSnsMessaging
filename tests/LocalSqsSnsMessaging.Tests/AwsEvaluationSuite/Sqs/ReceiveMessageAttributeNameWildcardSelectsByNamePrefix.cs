// Translated from behavioral/sqs/check_receive_message_attribute_name_wildcard_selects_by_name_prefix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-attribute-name-wildcard-selects-by-name-prefix</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageAttributeNameWildcardSelectsByNamePrefix
{
    private const string Scenario = "sqs-attribute-name-wildcard";

    private static Dictionary<string, MessageAttributeValue> Attrs() => new()
    {
        ["order.id"] = new() { DataType = "String", StringValue = "A-1" },
        ["order.total"] = new() { DataType = "Number", StringValue = "42" },
        ["orderly"] = new() { DataType = "String", StringValue = "yes" },
        ["Order.region"] = new() { DataType = "String", StringValue = "west" },
        ["trace"] = new() { DataType = "Binary", BinaryValue = new MemoryStream([0x01, 0x02]) },
    };

    private static List<string> SortedAttrNames() => Attrs().Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A MessageAttributeNames entry ending in ``.*`` selects by case-sensitive name prefix and
    /// combines with exact names; ``.*`` alone returns all, and a match of nothing omits
    /// MessageAttributes while the same message is still delivered with its sent values.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-attribute-name-wildcard-selects-by-name-prefix")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "MessageAttributeNames=['order.*'] returned every attribute instead of selecting by case-sensitive name prefix")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = await CreateQueue(ctx);
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "sim2real-wildcard", MessageAttributes = Attrs() });

        async Task<(string MessageId, List<string>? Names)> Project(List<string> names)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30.0))
            {
                var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = url,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 2,
                    VisibilityTimeout = 0,
                    MessageAttributeNames = names,
                })).Messages ?? [];
                if (msgs.Count > 0)
                {
                    var attrs = msgs[0].MessageAttributes;
                    return (msgs[0].MessageId, attrs?.Keys.Order(StringComparer.Ordinal).ToList());
                }
            }

            throw Fail($"the message was not receivable for selection [{string.Join(", ", names)}]");
        }

        var cases = new (List<string> Names, List<string>? Expected)[]
        {
            (["order.*"], ["order.id", "order.total", "orderly"]),
            (["order.*", "trace"], ["order.id", "order.total", "orderly", "trace"]),
            (["Order.*"], ["Order.region"]),
            ([".*"], SortedAttrNames()),
            (["nothing.*"], null),
        };
        foreach (var (names, expected) in cases)
        {
            var (messageId, got) = await Project(names);
            Ensure(messageId == sent.MessageId,
                $"selection [{string.Join(", ", names)}] received a different message than the one sent");
            Ensure(got is null ? expected is null : expected is not null && got.SequenceEqual(expected),
                $"MessageAttributeNames=[{string.Join(", ", names)}] returned attributes {Show(got)}, expected {Show(expected)}");
        }

        var (_, full) = await Project(["All"]);
        var values = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 2,
            MessageAttributeNames = ["order.*"],
        })).Messages ?? [];
        Ensure(full is not null && full.SequenceEqual(SortedAttrNames()) && values.Count > 0, "the message must still hold all five attributes");
        var gotValues = values[0].MessageAttributes!.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.StringValue);
        var expectedValues = new Dictionary<string, string?> { ["order.id"] = "A-1", ["order.total"] = "42", ["orderly"] = "yes" };
        Ensure(gotValues.Count == expectedValues.Count && !gotValues.Except(expectedValues).Any(),
            $"the wildcard-selected attributes must carry their sent values, got {string.Join(", ", gotValues)}");
    }

    // The Python module's unused helpers (_error, _receive_all, _receive_window) are not ported.

    /// <summary>CreateQueue (tagged + prefix-named) and register DeleteQueue immediately.</summary>
    private static async Task<string> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"),
            Attributes = attributes ?? [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }

    private static string Show(List<string>? names) => names is null ? "None" : $"[{string.Join(", ", names.Select(n => $"'{n}'"))}]";
}
