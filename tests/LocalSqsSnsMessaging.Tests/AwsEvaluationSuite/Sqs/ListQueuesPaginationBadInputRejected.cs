// Translated from behavioral/sqs/check_list_queues_pagination_bad_input_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-queues-pagination-bad-input-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListQueuesPaginationBadInputRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "list-queues-pagination-bad-input-rejected",
    };

    private const string MsgMax = "Value for parameter MaxResults is invalid. Reason: MaxResults must be an integer between 1 and 1000.";
    private const string MsgToken = "Invalid or expired next token.";

    /// <summary>
    /// ListQueues refuses MaxResults=1001 and a forged NextToken with InvalidParameterValue and exact
    /// messages, while MaxResults=1000 over the same prefix lists exactly the created queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-queues-pagination-bad-input-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected the call to be refused, but ListQueues(MaxResults=1001) succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-lqbad-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, msg) = await Err(() => sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name, MaxResults = 1001 }));
        Ensure(code == "InvalidParameterValue", $"MaxResults=1001 gave code '{code}'");
        Ensure(msg == MsgMax, $"MaxResults=1001 gave message '{msg}'");

        (code, msg) = await Err(() => sqs.ListQueuesAsync(new ListQueuesRequest
        {
            QueueNamePrefix = name, MaxResults = 5, NextToken = "sim2real-not-a-token",
        }));
        Ensure(code == "InvalidParameterValue", $"a forged NextToken gave code '{code}'");
        Ensure(msg == MsgToken, $"a forged NextToken gave message '{msg}'");

        var deadline = Stopwatch.StartNew();
        ListQueuesResponse resp;
        List<string> urls;
        while (true)
        {
            resp = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name, MaxResults = 1000 });
            urls = resp.QueueUrls ?? [];
            if (urls.Count > 0 || deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(urls.SequenceEqual([url]),
            $"positive control: ListQueues(prefix, MaxResults=1000) returned {urls.Count} URLs, " +
            "expected exactly the one created queue");
        Ensure(resp.NextToken is null, "a one-queue listing at MaxResults=1000 carried a NextToken");
    }

    /// <summary>(Code, Message) of the ClientError ``fn`` raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static string Repr(object? result) => result is ListQueuesResponse r
        ? $"{{'QueueUrls': [{string.Join(", ", (r.QueueUrls ?? []).Select(u => $"'{u}'"))}], 'NextToken': '{r.NextToken}'}}"
        : $"{result}";
}
