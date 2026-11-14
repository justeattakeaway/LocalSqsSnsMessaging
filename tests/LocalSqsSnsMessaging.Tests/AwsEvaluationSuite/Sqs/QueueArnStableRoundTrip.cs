// Translated from behavioral/sqs/check_queue_arn_stable_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-arn-stable-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueArnStableRoundTrip
{
    /// <summary>
    /// QueueArn returned by GetQueueAttributes matches the arn:aws:sqs:&lt;region&gt;:&lt;account&gt;:&lt;name&gt;
    /// format, is stable across repeated calls, and the queue name embedded in it matches both
    /// the URL from create_queue and the URL returned by GetQueueUrl.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-arn-stable-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // sts.get_caller_identity()["Account"]
        var accountId = ctx.Account;
        var name = FidelityContext.UniqueName("sim2real-behav-arn-");
        var createUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "queue-arn-stable-round-trip" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(createUrl));

        var lookupUrl = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        Ensure(lookupUrl == createUrl, $"'{lookupUrl}' != '{createUrl}'");

        var arnFirst = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = createUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var arnSecond = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = createUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        Ensure(arnFirst == arnSecond, "QueueArn is not stable across repeated calls");

        var expectedArn = $"arn:aws:sqs:{ctx.Region}:{accountId}:{name}";
        Ensure(arnFirst == expectedArn, $"expected QueueArn '{expectedArn}', got '{arnFirst}'");

        var nameSegment = arnFirst.Split(':')[^1];
        Ensure(nameSegment == name, $"QueueArn name segment '{nameSegment}' != '{name}'");

        var urlSegment = createUrl.TrimEnd('/').Split('/')[^1];
        Ensure(urlSegment == name, $"QueueUrl name segment '{urlSegment}' != '{name}'");
    }
}
