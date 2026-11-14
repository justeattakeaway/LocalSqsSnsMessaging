// Translated from behavioral/sqs/check_queue_create_idempotent_same_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-create-idempotent-same-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueCreateIdempotentSameAttributes
{
    /// <summary>
    /// CreateQueue repeated with identical name and attributes returns the same URL, creates no
    /// second queue, and persists the requested attributes, per independent GetQueueUrl and
    /// GetQueueAttributes reads.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-create-idempotent-same-attributes")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        static Dictionary<string, string> Attrs() => new() { ["VisibilityTimeout"] = "60", ["MessageRetentionPeriod"] = "3600" };

        var url1 = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = Attrs() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url1));

        var url2 = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = Attrs() })).QueueUrl;
        Ensure(url1 == url2, $"idempotent CreateQueue must return the same QueueUrl, got '{url1}' vs '{url2}'");

        // independent read, through GetQueueUrl (never the mutating CreateQueue calls), proves
        // the second call did not spawn a second queue under the same name
        var resolved = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        Ensure(resolved == url1, $"GetQueueUrl resolved to '{resolved}', expected the created queue '{url1}'");

        // independent read of the persisted attributes: a target that ignores Attributes on
        // CreateQueue, or clobbers them on the idempotent re-create, must not pass this
        var persisted = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url1,
            AttributeNames = ["VisibilityTimeout", "MessageRetentionPeriod"],
        })).Attributes!;
        Ensure(persisted.GetValueOrDefault("VisibilityTimeout") == "60",
            $"VisibilityTimeout expected '60', got '{persisted.GetValueOrDefault("VisibilityTimeout")}'");
        Ensure(persisted.GetValueOrDefault("MessageRetentionPeriod") == "3600",
            $"MessageRetentionPeriod expected '3600', got '{persisted.GetValueOrDefault("MessageRetentionPeriod")}'");
    }
}
