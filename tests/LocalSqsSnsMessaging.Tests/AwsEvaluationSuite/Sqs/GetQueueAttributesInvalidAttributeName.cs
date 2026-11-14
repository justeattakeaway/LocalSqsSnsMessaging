// Translated from behavioral/sqs/check_get_queue_attributes_invalid_attribute_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.get-queue-attributes-invalid-attribute-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetQueueAttributesInvalidAttributeName
{
    private const string UnknownAttributeMessage = "Unknown Attribute BogusAttribute.";

    /// <summary>
    /// GetQueueAttributes rejects the unknown attribute name BogusAttribute with
    /// InvalidAttributeName and its exact message, while the same call for QueueArn returns
    /// it.
    /// </summary>
    [Test]
    [DisplayName("sqs.get-queue-attributes-invalid-attribute-name")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!;
        Ensure(attrs.ContainsKey("QueueArn"), $"positive control did not return QueueArn: [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");

        var error = await ClientError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["BogusAttribute"] }));
        Ensure(error.Code == "InvalidAttributeName",
            $"expected InvalidAttributeName, got '{error.Code}'");
        Ensure(error.Message == UnknownAttributeMessage,
            $"expected message '{UnknownAttributeMessage}', got '{error.Message}'");
    }

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }
}
