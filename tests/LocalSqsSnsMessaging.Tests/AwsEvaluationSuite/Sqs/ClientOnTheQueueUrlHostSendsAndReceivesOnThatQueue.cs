// Translated from behavioral/sqs/check_client_on_the_queue_url_host_sends_and_receives_on_that_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.client-on-the-queue-url-host-sends-and-receives-on-that-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class ClientOnTheQueueUrlHostSendsAndReceivesOnThatQueue
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// A client on the scheme and host of the QueueUrl CreateQueue returned serves that queue.
    ///
    /// The default client receives what that client sent, and that client receives what it sent.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-queue-message-identifiers.html
    /// </summary>
    [Test]
    [DisplayName("sqs.client-on-the-queue-url-host-sends-and-receives-on-that-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-qurl-");
        ctx.OnCleanup(() => DeleteQueue(sqs, name));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        var p = PublishedUrlMismatch(url);
        Ensure(p is null, p ?? "");
        var parts = new Uri(url);
        var onHost = ctx.Client(serviceUrl: $"{parts.Scheme}://{parts.Authority}");

        var body = $"via-queue-url-host-{name}";
        var sent = await onHost.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        Ensure(sent.MD5OfMessageBody == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body))),
            $"SendMessage on the queue URL host returned MD5OfMessageBody '{sent.MD5OfMessageBody}'");
        var got = await Receive(ctx, sqs, url);
        Ensure(got.Select(m => (m.MessageId, m.Body)).SequenceEqual([(sent.MessageId, body)]),
            $"the default client must receive the message sent on the queue URL host; got [{string.Join(", ", got.Select(m => (m.MessageId, m.Body)))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = got[0].ReceiptHandle });

        var back = $"via-default-endpoint-{name}";
        var backId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = back })).MessageId;
        got = await Receive(ctx, onHost, url);
        Ensure(got.Select(m => (m.MessageId, m.Body)).SequenceEqual([(backId, back)]),
            $"the client on the queue URL host must receive the message sent to that queue; got [{string.Join(", ", got.Select(m => (m.MessageId, m.Body)))}]");
    }

    /// <summary>
    /// The URL-shape part of the runner's <c>ctx.published_url_mismatch(url)</c>: an http(s) URL with a
    /// host. The runner's "a customer can reach this host" check has no in-memory equivalent: every
    /// host routes to the bus.
    /// </summary>
    private static string? PublishedUrlMismatch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parts) || string.IsNullOrEmpty(parts.Host))
        {
            return $"the published data-plane endpoint is not a URL with a host: '{url}'";
        }

        if (parts.Scheme is not ("http" or "https"))
        {
            return $"the published endpoint '{url}' is not http(s)";
        }

        return null;
    }

    /// <summary>Delete the queue by name; tolerates only not-found.</summary>
    private static async Task DeleteQueue(AmazonSQSClient sqs, string name)
    {
        try
        {
            await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl });
        }
        catch (AmazonSQSException exc) when (exc.ErrorCode is NoSuchQueue or "QueueDoesNotExist")
        {
        }
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>The first non-empty receive within the poll budget.</summary>
    private static async Task<List<Message>> Receive(FidelityContext ctx, AmazonSQSClient sqs, string url)
    {
        var deadline = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, 20.0));
        while (true)
        {
            var got = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (got.Count > 0)
            {
                return got;
            }

            if (deadline.Elapsed >= budget)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException("the run budget ended before the queue delivered");
                }

                return got;
            }
        }
    }
}
