// Translated from behavioral/sqs/check_queue_arn_and_get_queue_url_consistency.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-arn-and-get-queue-url-consistency</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueArnAndGetQueueUrlConsistency
{
    /// <summary>
    /// QueueArn embeds the correct region/account/name, and GetQueueUrl resolves back to the CreateQueue URL.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-arn-and-get-queue-url-consistency")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // sts.get_caller_identity()["Account"]
        var accountId = ctx.Account;

        var name = FidelityContext.UniqueName("sim2real-behav-");
        var createdUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real:managed"] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(createdUrl));

        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = createdUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var parts = arn.Split(':');
        Ensure(parts[0] == "arn", $"QueueArn '{arn}' does not start with 'arn': '{parts[0]}'");
        Ensure(parts[2] == "sqs", $"QueueArn '{arn}' service segment is not 'sqs': '{parts[2]}'");
        Ensure(parts[3] == ctx.Region, $"QueueArn '{arn}' region segment '{parts[3]}' != '{ctx.Region}'");
        Ensure(parts[4] == accountId, $"QueueArn '{arn}' account segment '{parts[4]}' != '{accountId}'");
        Ensure(parts[5] == name, $"QueueArn '{arn}' name segment '{parts[5]}' != '{name}'");

        var resolvedUrl = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        Ensure(resolvedUrl == createdUrl, $"GetQueueUrl resolved '{resolvedUrl}', expected the CreateQueue URL '{createdUrl}'");
    }
}
