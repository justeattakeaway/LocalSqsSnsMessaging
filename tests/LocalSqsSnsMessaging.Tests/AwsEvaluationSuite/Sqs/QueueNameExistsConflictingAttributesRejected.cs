// Translated from behavioral/sqs/check_queue_name_exists_conflicting_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-name-exists-conflicting-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueNameExistsConflictingAttributesRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgExists = "A queue already exists with the same name and a different value for attribute ContentBasedDeduplication";

    /// <summary>
    /// CreateQueue reusing an existing FIFO queue's name with a different ContentBasedDeduplication is
    /// rejected with QueueAlreadyExists and its exact message and leaves the attribute unchanged,
    /// while the same call with identical attributes returns the same URL.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-name-exists-conflicting-attributes-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-sqs-fifonameconflict-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Positive control: the FIFO-specific attribute actually took effect on creation.
        var createdAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!;
        Ensure(createdAttrs["ContentBasedDeduplication"] == "true",
            $"expected ContentBasedDeduplication=true right after creation, got {Show(createdAttrs)}");

        var err = await ClientError(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
        }));
        // Real AWS returns the wire error code "QueueAlreadyExists" for this condition (the
        // Smithy/API-doc shape name "QueueNameExists" is not the wire code).
        Ensure(err.Code == "QueueAlreadyExists", $"expected QueueAlreadyExists, got {err}");
        Ensure(err.Message == MsgExists, $"got {err}");

        // the conflicting call must not have mutated the existing queue's FIFO-specific attribute
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!;
        Ensure(attrs["ContentBasedDeduplication"] == "true",
            $"ContentBasedDeduplication changed to '{attrs["ContentBasedDeduplication"]}' "
            + "by a rejected CreateQueue call");

        // positive control: identical attributes -> idempotent success, same URL returned
        var idempotent = await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
        });
        Ensure(idempotent.QueueUrl == url,
            $"expected identical re-create to return the same queue URL '{url}', got '{idempotent.QueueUrl}'");

        // Terminal independent read: the idempotent re-create did not change the attribute either.
        var finalAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!;
        Ensure(finalAttrs["ContentBasedDeduplication"] == "true",
            $"ContentBasedDeduplication changed to '{finalAttrs["ContentBasedDeduplication"]}' "
            + "after an idempotent re-create");
    }

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection but call succeeded and returned {result}");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
