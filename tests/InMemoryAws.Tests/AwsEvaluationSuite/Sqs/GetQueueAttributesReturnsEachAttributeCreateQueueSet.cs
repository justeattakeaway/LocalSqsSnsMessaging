// Translated from behavioral/sqs/check_get_queue_attributes_returns_each_attribute_create_queue_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.get-queue-attributes-returns-each-attribute-create-queue-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetQueueAttributesReturnsEachAttributeCreateQueueSet
{
    private const string Check = "get-queue-attributes-returns-each-attribute-create-queue-set";
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";

    // Each value differs from the attribute's default, so a store that ignores it is caught.
    private static Dictionary<string, string> Sent() => new()
    {
        ["DelaySeconds"] = "5",
        ["MaximumMessageSize"] = "2048",
        ["MessageRetentionPeriod"] = "7200",
        ["ReceiveMessageWaitTimeSeconds"] = "3",
        ["VisibilityTimeout"] = "40",
    };

    /// <summary>
    /// GetQueueAttributes returns every attribute CreateQueue set with the value sent, both when the
    /// attributes are named one by one and in the All listing.
    /// </summary>
    [Test]
    [DisplayName("sqs.get-queue-attributes-returns-each-attribute-create-queue-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var sent = Sent();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-gqa-"), Attributes = Sent(), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(DropQueue(sqs, url));

        var named = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = [.. sent.Keys.Order(StringComparer.Ordinal)],
        })).Attributes ?? [];
        Ensure(DictEquals(named, sent), $"the named read returned {Show(named)}, expected exactly {Show(sent)}");

        var every = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["All"] })).Attributes ?? [];
        var picked = sent.Keys.ToDictionary(name => name, name => every.GetValueOrDefault(name));
        Ensure(picked.All(kv => kv.Value == sent[kv.Key]),
            $"the All read carries {Show(picked.AsEnumerable())} for the sent attributes, expected {Show(sent)}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(IEnumerable<KeyValuePair<string, string?>> d) =>
        "{" + string.Join(", ", d.Select(kv => kv.Value is null ? $"'{kv.Key}': None" : $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string Show(Dictionary<string, string> d) => Show(d.Select(kv => KeyValuePair.Create(kv.Key, (string?)kv.Value)));

    /// <summary>A cleanup deleting the queue; only the queue's own not-found code counts as already gone.</summary>
    private static Func<Task> DropQueue(AmazonSQSClient sqs, string url) => async () =>
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == Gone)
        {
        }
    };
}
