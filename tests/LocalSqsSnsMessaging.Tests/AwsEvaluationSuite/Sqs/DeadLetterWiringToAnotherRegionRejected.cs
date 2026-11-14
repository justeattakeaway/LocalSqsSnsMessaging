// Translated from behavioral/sqs/check_dead_letter_wiring_to_another_region_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dead-letter-wiring-to-another-region-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeadLetterWiringToAnotherRegionRejected
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "dead-letter-wiring-to-another-region-rejected" };
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// A RedrivePolicy or RedriveAllowPolicy naming a queue in another region is refused with
    /// InvalidParameterValue on SetQueueAttributes and CreateQueue and stores nothing; the same
    /// documents naming same-region queues are accepted and ListDeadLetterSourceQueues reports them.
    /// </summary>
    [Test]
    [DisplayName("sqs.dead-letter-wiring-to-another-region-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MultiRegion, "SetQueueAttributes with a RedrivePolicy naming a DLQ in another region succeeded instead of InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Sqs;
        var altRegion = OtherRegion(ctx.Region);
        var alt = ctx.Client(region: altRegion);
        var @base = FidelityContext.UniqueName("sim2real-behav-xr-dlq-");

        var srcUrl = (await home.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-src", Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(home, srcUrl));
        var localDlq = (await home.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-dlq", Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(home, localDlq));
        var farDlq = (await alt.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-dlq", Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(alt, farDlq));
        var (srcArn, localArn, farArn) = (await Arn(home, srcUrl), await Arn(home, localDlq), await Arn(alt, farDlq));

        var farPolicy = PyDumps(("deadLetterTargetArn", farArn), ("maxReceiveCount", "3"));
        var regionMsg = $"Value {farPolicy} for parameter RedrivePolicy is invalid. Reason: " +
                        "Dead-letter target must be in same region as the source.";

        var err = await Error(() => home.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = farPolicy },
        }));
        Ensure((err.Code, err.Message) == ("InvalidParameterValue", regionMsg),
            $"SetQueueAttributes with a DLQ in {altRegion} answered {err}");
        var attrs = (await home.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["All"] })).Attributes!;
        Ensure(!attrs.ContainsKey("RedrivePolicy"),
            $"the refused cross-region RedrivePolicy was stored anyway: {attrs.GetValueOrDefault("RedrivePolicy")}");

        var createdName = @base + "-born";
        err = await Error(() => home.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = createdName,
            Attributes = new() { ["RedrivePolicy"] = farPolicy },
            Tags = new(Tags),
        }));
        Ensure((err.Code, err.Message) == ("InvalidParameterValue", regionMsg),
            $"CreateQueue with a DLQ in {altRegion} answered {err}");
        var missing = await Error(() => home.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = createdName }));
        Ensure(missing.Code == NoSuchQueue,
            $"the refused CreateQueue left a queue behind: GetQueueUrl answered {missing}");

        var allow = PyDumps(("redrivePermission", "byQueue"), ("sourceQueueArns", new[] { farArn }));
        err = await Error(() => home.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = localDlq,
            Attributes = new() { ["RedriveAllowPolicy"] = allow },
        }));
        var allowMsg = $"Value {allow} for parameter RedriveAllowPolicy is invalid. Reason: Amazon SQS " +
                       "can't create the redrive allow policy. All source queue ARNs must be in the " +
                       "same region as the current SQS queue.";
        Ensure((err.Code, err.Message) == ("InvalidParameterValue", allowMsg),
            $"RedriveAllowPolicy listing a {altRegion} source answered {err}");
        var dlqAttrs = (await home.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = localDlq, AttributeNames = ["All"] })).Attributes!;
        Ensure(!dlqAttrs.ContainsKey("RedriveAllowPolicy"),
            $"the refused RedriveAllowPolicy was stored: {dlqAttrs.GetValueOrDefault("RedriveAllowPolicy")}");

        // Attribute and source-list reads lag the write, so poll them within _SETTLE_S.
        await home.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = localDlq,
            Attributes = new() { ["RedriveAllowPolicy"] = PyDumps(("redrivePermission", "byQueue"), ("sourceQueueArns", new[] { srcArn })) },
        });
        await home.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", localArn), ("maxReceiveCount", "3")) },
        });
        string? stored = null;
        var sources = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            stored = (await home.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["RedrivePolicy"] }))
                .Attributes!.GetValueOrDefault("RedrivePolicy");
            sources = [];
            await foreach (var page in home.Paginators.ListDeadLetterSourceQueues(new ListDeadLetterSourceQueuesRequest { QueueUrl = localDlq }).Responses)
            {
                sources.AddRange(page.QueueUrls ?? []);
            }

            if (!string.IsNullOrEmpty(stored) && sources.Count > 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(!string.IsNullOrEmpty(stored) && JsonDocument.Parse(stored).RootElement.GetProperty("deadLetterTargetArn").GetString() == localArn,
            $"the same-region RedrivePolicy reads back as {stored}");
        Ensure(sources.SequenceEqual([srcUrl]),
            $"ListDeadLetterSourceQueues for the same-region DLQ lists {sources.Count} source(s), " +
            "expected exactly the source queue");
    }

    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Repr(result)}");
    }

    private static async Task<string> Arn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(url));
        if (error is not null && error.Code != NoSuchQueue)
        {
            throw error.Exception;
        }
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict (string, int or list-of-string values), with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyValue(kv.Value)}")) + "}";

    private static string PyValue(object value) => value is string[] list
        ? "[" + string.Join(", ", list.Select(v => JsonSerializer.Serialize(v))) + "]"
        : JsonSerializer.Serialize(value);

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
