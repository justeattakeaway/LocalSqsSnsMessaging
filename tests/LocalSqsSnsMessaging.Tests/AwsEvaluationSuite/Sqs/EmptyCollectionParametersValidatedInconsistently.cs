// Translated from behavioral/sqs/check_empty_collection_parameters_validated_inconsistently.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.empty-collection-parameters-validated-inconsistently</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyCollectionParametersValidatedInconsistently
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "empty-collection-parameters-validated-inconsistently" };

    /// <summary>
    /// Empty Attributes/Tags/TagKeys on SetQueueAttributes, TagQueue and UntagQueue are refused with
    /// MissingParameter naming the parameter and change nothing, while CreateQueue with empty
    /// Attributes and GetQueueAttributes with empty AttributeNames succeed as no-ops.
    /// </summary>
    [Test]
    [DisplayName("sqs.empty-collection-parameters-validated-inconsistently")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "GetQueueAttributes with no AttributeNames returns every attribute (kept for compatibility) where SQS returns none")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-emptycoll-"),
            Attributes = new() { ["VisibilityTimeout"] = "37" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = [] }));
        Ensure(code == "MissingParameter", $"SetQueueAttributes({{}}) gave code {code}");
        Ensure(msg == "The request must contain the parameter Attribute.Name.",
            $"SetQueueAttributes({{}}) gave message {msg}");
        var vt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!;
        Ensure(vt["VisibilityTimeout"] == "37",
            $"a refused empty SetQueueAttributes changed VisibilityTimeout to {vt["VisibilityTimeout"]}");

        (code, msg) = await Err(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = [] }));
        Ensure(code == "MissingParameter", $"TagQueue({{}}) gave code {code}");
        Ensure(msg == "The request must contain the parameter Tags.", $"TagQueue({{}}) gave message {msg}");
        var tagsAfter = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(tagsAfter, Tags), $"a refused empty TagQueue changed the tag set to {JsonSerializer.Serialize(tagsAfter)}");

        (code, msg) = await Err(() => sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = [] }));
        Ensure(code == "MissingParameter", $"UntagQueue([]) gave code {code}");
        Ensure(msg == "The request must contain the parameter TagKeys.", $"UntagQueue([]) gave message {msg}");
        tagsAfter = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(tagsAfter, Tags), $"a refused empty UntagQueue changed the tag set to {JsonSerializer.Serialize(tagsAfter)}");

        var name = FidelityContext.UniqueName("sim2real-behav-emptycreate-");
        var created = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = [], Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(created));
        var defaultVt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = created, AttributeNames = ["VisibilityTimeout"] }))
            .Attributes!["VisibilityTimeout"];
        Ensure(defaultVt == "30",
            "CreateQueue(Attributes={}) must take every attribute's default; " +
            $"VisibilityTimeout read back as {defaultVt}, expected the documented default '30'");

        var emptyRead = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [] });
        Ensure(emptyRead.Attributes is null,
            "GetQueueAttributes(AttributeNames=[]) returned Attributes " +
            $"{JsonSerializer.Serialize(emptyRead.Attributes)}, expected no Attributes key at all");
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
