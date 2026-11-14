// Translated from behavioral/sqs/check_get_queue_attributes_invalid_attribute_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.get-queue-attributes-invalid-attribute-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetQueueAttributesInvalidAttributeNameRejected
{
    private const string Check = "get-queue-attributes-invalid-attribute-name-rejected";

    // Exact messages real SQS returns for these refusals.
    private const string MsgBadAttr = "Unknown Attribute NotARealAttributeName.";

    /// <summary>
    /// GetQueueAttributes rejects an unrecognized AttributeName; a valid name in the same call set still works.
    /// </summary>
    [Test]
    [DisplayName("sqs.get-queue-attributes-invalid-attribute-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Positive control: a valid attribute name on this queue succeeds and returns real data.
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!;
        Ensure(attrs["QueueArn"].StartsWith("arn:aws:sqs:", StringComparison.Ordinal),
            $"positive control failed: QueueArn was '{attrs.GetValueOrDefault("QueueArn")}'");

        var err = await ClientError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["NotARealAttributeName"] }));
        Ensure(err.Code == "InvalidAttributeName",
            $"expected InvalidAttributeName for an unrecognized AttributeName, got {err}");
        Ensure(err.Message == MsgBadAttr, $"got {err}");
    }

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail(
            "real AWS rejects GetQueueAttributes(AttributeNames=['NotARealAttributeName']), but it " +
            $"SUCCEEDED and returned {result}");
    }
}
