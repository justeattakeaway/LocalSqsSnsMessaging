// Translated from behavioral/sqs/check_tag_queue_invalid_tag_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-queue-invalid-tag-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagQueueInvalidTagKeyRejected
{
    /// <summary>
    /// TagQueue with a tag key that exceeds the 128-character AWS-wide tag-key limit is
    /// rejected rather than stored, distinct from the generic attribute-validation errors
    /// covered elsewhere; a normal tag key still persists.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-queue-invalid-tag-key-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Tags, "expected TagQueue with a 300-character tag key to be rejected with InvalidParameterValue, but it succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-tagkey-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var oversizedKey = new string('k', 300);
        var (code, message) = await Error(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { [oversizedKey] = "v" } }));
        Ensure(code == "InvalidParameterValue", $"expected InvalidParameterValue for a 300-char tag key, got '{code}'");
        const string expectedMessage = "Tag keys must be between 1 and 128 characters in length.";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // effect absent: the oversized tag key was not stored
        var tagsAfter = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(!tagsAfter.ContainsKey(oversizedKey), $"oversized tag key was stored despite rejection: {Show(tagsAfter)}");

        // positive control: a normal tag key/value is accepted and persisted
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["Environment"] = "sim2real-test" } });
        ctx.OnCleanup(() => Try(() => sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["Environment"] })));
        var controlTags = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(controlTags.GetValueOrDefault("Environment") == "sim2real-test", $"expected control tag to persist, got {Show(controlTags)}");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{(kv.Key.Length > 20 ? kv.Key[..20] + "..." : kv.Key)}': '{kv.Value}'")) + "}";

    private static async Task<(string? Code, string? Message)> Error(Func<Task<TagQueueResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded and returned {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }

    private static async Task Try(Func<Task> fn)
    {
        try
        {
            await fn();
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
        }
    }
}
