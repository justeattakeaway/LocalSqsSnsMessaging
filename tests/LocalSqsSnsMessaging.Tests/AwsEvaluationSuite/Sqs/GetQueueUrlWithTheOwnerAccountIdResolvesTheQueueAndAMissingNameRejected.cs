// Translated from behavioral/sqs/check_get_queue_url_with_the_owner_account_id_resolves_the_queue_and_a_missing_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.get-queue-url-with-the-owner-account-id-resolves-the-queue-and-a-missing-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetQueueUrlWithTheOwnerAccountIdResolvesTheQueueAndAMissingNameRejected
{
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// GetQueueUrl with the caller's own QueueOwnerAWSAccountId returns CreateQueue's usable URL.
    ///
    /// The same lookup is refused with NonExistentQueue for a never-created name and a deleted queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.get-queue-url-with-the-owner-account-id-resolves-the-queue-and-a-missing-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        ctx.OnCleanup(() => DeleteIfPresent(sqs, url));

        var got = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name, QueueOwnerAWSAccountId = ctx.Account })).QueueUrl;
        Ensure(got == url, $"the owner-qualified lookup returned '{got}', CreateQueue returned '{url}'");
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = got, MessageBody = "owner-qualified" });
        var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = got, WaitTimeSeconds = 10 })).Messages ?? [];
        Ensure(msgs.Select(m => m.Body).SequenceEqual(["owner-qualified"]),
            $"receive via the resolved URL: [{string.Join(", ", msgs.Select(m => m.Body))}]");

        var missing = FidelityContext.UniqueName("sim2real-behav-q-missing-");
        var (code, message) = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = missing, QueueOwnerAWSAccountId = ctx.Account }));
        Ensure(code == Gone, $"GetQueueUrl for a missing name: got {code}: '{message}'");
        Ensure(message == "The specified queue does not exist.", $"unexpected message '{message}'");

        await sqs.DeleteQueueAsync(url);
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            // a deleted queue's name stops resolving within about a minute
            try
            {
                await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name, QueueOwnerAWSAccountId = ctx.Account });
            }
            catch (AmazonServiceException exc)
            {
                code = exc.ErrorCode ?? throw new KeyNotFoundException("Code");
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(90), "the deleted queue still resolves after 90s");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(code == Gone, $"GetQueueUrl after DeleteQueue: got '{code}'");
    }

    /// <summary>(code, message) of the ClientError fn() raised. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code ?? throw new KeyNotFoundException("Code"), error.Message);
    }

    private static async Task DeleteIfPresent(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == Gone)
        {
        }
    }
}
