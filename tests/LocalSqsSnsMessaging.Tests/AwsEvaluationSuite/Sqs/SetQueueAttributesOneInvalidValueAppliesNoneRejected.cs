// Translated from behavioral/sqs/check_set_queue_attributes_one_invalid_value_applies_none_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-one-invalid-value-applies-none-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesOneInvalidValueAppliesNoneRejected
{
    private static readonly string[] Names = ["VisibilityTimeout", "DelaySeconds", "MessageRetentionPeriod"];

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "set-queue-attributes-one-invalid-value-applies-none-rejected" };

    /// <summary>
    /// SetQueueAttributes with two valid values and one out-of-range value is refused whole with
    /// InvalidAttributeValue naming the bad attribute and applies none; the corrected call applies
    /// all.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-one-invalid-value-applies-none-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DefaultAttributes, "GetQueueAttributes on a fresh queue omits the default VisibilityTimeout/DelaySeconds/MessageRetentionPeriod (KeyError 'Attributes')")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqa-"),
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var before = await Read(sqs, url);
        Ensure(DictEquals(before, new() { ["VisibilityTimeout"] = "30", ["DelaySeconds"] = "0", ["MessageRetentionPeriod"] = "345600" }),
            $"a fresh queue does not carry the documented defaults: {Show(before)}");

        var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["VisibilityTimeout"] = "45", ["DelaySeconds"] = "5", ["MessageRetentionPeriod"] = "10" },
        }));
        Ensure(code == "InvalidAttributeValue", $"the mixed request gave code '{code}'");
        Ensure(msg == "Invalid value for the parameter MessageRetentionPeriod.", $"the mixed request gave message '{msg}'");
        var after = await Read(sqs, url);
        Ensure(DictEquals(after, before),
            $"a refused SetQueueAttributes applied some of its values: before {Show(before)}, after {Show(after)}");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["VisibilityTimeout"] = "45", ["DelaySeconds"] = "5", ["MessageRetentionPeriod"] = "120" },
        });
        var fixedAttrs = await Read(sqs, url);
        Ensure(DictEquals(fixedAttrs, new() { ["VisibilityTimeout"] = "45", ["DelaySeconds"] = "5", ["MessageRetentionPeriod"] = "120" }),
            $"positive control: the corrected request did not apply all three values: {Show(fixedAttrs)}");
    }

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }

    // Python indexes resp["Attributes"], which raises KeyError when the response omits it.
    private static async Task<Dictionary<string, string>> Read(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [.. Names] })).Attributes
           ?? throw new KeyNotFoundException("'Attributes'");

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
