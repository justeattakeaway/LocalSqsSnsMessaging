// Translated from behavioral/sqs/check_create_queue_existing_name_different_tags_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-existing-name-different-tags-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueExistingNameDifferentTagsRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "create-queue-existing-name-different-tags-rejected",
        ["team"] = "alpha",
    };

    // The real message ends in a trailing space.
    private const string MsgTags = "A queue already exists with the same name and a different value for tags ";

    /// <summary>
    /// Re-creating an existing queue with a different tag value is refused with QueueAlreadyExists
    /// and changes nothing; re-creating with identical tags returns the same URL.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-existing-name-different-tags-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-cqt-");
        var attrs = new Dictionary<string, string> { ["VisibilityTimeout"] = "40" };
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs, Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var changed = new Dictionary<string, string>(Tags) { ["team"] = "beta" };
        var (code, msg) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs, Tags = changed }));
        Ensure(code == "QueueAlreadyExists", $"a different tag value gave code {code}");
        Ensure(msg == MsgTags, $"a different tag value gave message {msg}");
        var stored = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(stored, Tags), $"the refused re-create changed the stored tags to {JsonSerializer.Serialize(stored)}");

        var again = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs, Tags = new(Tags) })).QueueUrl;
        Ensure(again == url, $"an identical re-create returned a different URL: {again} != {url}");
        Ensure(DictEquals((await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [], Tags),
            "the identical re-create changed the stored tags");
        var vt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] }))
            .Attributes!["VisibilityTimeout"];
        Ensure(vt == "40", $"the queue's VisibilityTimeout reads {vt} after the re-creates");
    }

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Repr(result)}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
