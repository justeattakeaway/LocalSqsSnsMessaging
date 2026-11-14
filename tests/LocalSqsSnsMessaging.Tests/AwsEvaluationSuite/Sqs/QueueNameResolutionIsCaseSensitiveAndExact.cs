// Translated from behavioral/sqs/check_queue_name_resolution_is_case_sensitive_and_exact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-name-resolution-is-case-sensitive-and-exact</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueNameResolutionIsCaseSensitiveAndExact
{
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";
    private const string GoneMessage = "The specified queue does not exist.";

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "queue-name-resolution-is-case-sensitive-and-exact" };

    /// <summary>
    /// Queue names resolve exactly: GetQueueUrl and ListQueues miss a different-case spelling, a FIFO
    /// name without its .fifo suffix, and a .FIFO suffix, each refused with NonExistentQueue.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-name-resolution-is-case-sensitive-and-exact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var name = FidelityContext.UniqueName("sim2real-behav-Case-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Positive control: the exact name resolves, via both operations.
        var resolved = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        Ensure(resolved == url, $"GetQueueUrl('{name}') returned '{resolved}', CreateQueue returned '{url}'");
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var urls = await ListUrls(sqs, name);
            if (urls.Contains(url))
            {
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(30), $"ListQueues(prefix='{name}') never listed '{url}'");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        // A different-case spelling of the exact same name is a MISS on both operations.
        var flipped = SwapCase(name);
        Ensure(flipped != name, $"swapcase() of '{name}' produced the same string, test is void");
        var (code, msg) = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = flipped }));
        Ensure(code == Gone, $"GetQueueUrl('{flipped}') (case-flipped) gave '{code}', expected '{Gone}'");
        Ensure(msg == GoneMessage,
            $"GetQueueUrl('{flipped}') (case-flipped) gave message '{msg}', expected '{GoneMessage}'");
        var otherCaseUrls = await ListUrls(sqs, flipped);
        Ensure(!otherCaseUrls.Contains(url),
            $"ListQueues(prefix='{flipped}') (case-flipped) matched '{url}', "
            + $"which was created under the different-case name '{name}'");

        // A FIFO queue's base name, with the mandatory .fifo suffix dropped, is also a MISS.
        var fifoBase = FidelityContext.UniqueName("sim2real-behav-fifobase-");
        var fifoName = fifoBase + ".fifo";
        var fifoUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoName,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoUrl));

        var resolvedFifo = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = fifoName })).QueueUrl;
        Ensure(resolvedFifo == fifoUrl,
            $"GetQueueUrl('{fifoName}') returned '{resolvedFifo}', CreateQueue returned '{fifoUrl}'");
        (code, msg) = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = fifoBase }));
        Ensure(code == Gone, $"GetQueueUrl('{fifoBase}') (suffix dropped) gave '{code}', expected '{Gone}'");
        Ensure(msg == GoneMessage,
            $"GetQueueUrl('{fifoBase}') (suffix dropped) gave message '{msg}', "
            + $"expected '{GoneMessage}'");

        // The suffix itself is matched case-sensitively too: ``.FIFO`` is not ``.fifo``.
        var upperSuffix = fifoBase + ".FIFO";
        (code, msg) = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = upperSuffix }));
        Ensure(code == Gone,
            $"GetQueueUrl('{upperSuffix}') (suffix upper-cased) gave '{code}', expected '{Gone}'");
        Ensure(msg == GoneMessage,
            $"GetQueueUrl('{upperSuffix}') (suffix upper-cased) gave message '{msg}', "
            + $"expected '{GoneMessage}'");
    }

    /// <summary>The botocore (Code, Message) <c>fn()</c> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<List<string>> ListUrls(IAmazonSQS sqs, string prefix)
    {
        List<string> urls = [];
        string? token = null;
        while (true)
        {
            var request = new ListQueuesRequest { QueueNamePrefix = prefix };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await sqs.ListQueuesAsync(request);
            urls.AddRange(page.QueueUrls ?? []);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return urls;
            }
        }
    }

    private static string SwapCase(string s) =>
        new(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c).ToArray());
}
