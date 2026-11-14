// Translated from behavioral/sqs/check_approx_count_tracks.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.approx-count-tracks</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApproxCountTracks
{
    /// <summary>
    /// ApproximateNumberOfMessages eventually reflects sent messages.
    ///
    /// LENIENT: eventually consistent on real AWS. Best-effort bounded poll.
    /// </summary>
    [Test]
    [DisplayName("sqs.approx-count-tracks")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        for (var i = 0; i < 2; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"count-{i}" });
        }

        var count = -1;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!;
            count = int.Parse(attrs["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            if (count == 2)
            {
                return;
            }

            if (count > 2)
            {
                throw Fail($"ApproximateNumberOfMessages overcounted: sent 2, saw {count}");
            }

            await Task.Delay(TimeSpan.FromSeconds(0.4));
        }

        throw Fail($"ApproximateNumberOfMessages never reached 2 (last={count})");
    }
}
