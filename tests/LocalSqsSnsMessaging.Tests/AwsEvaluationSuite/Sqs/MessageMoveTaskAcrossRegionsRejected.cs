// Translated from behavioral/sqs/check_message_move_task_across_regions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-move-task-across-regions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageMoveTaskAcrossRegionsRejected
{
    private const string NotFound = "ResourceNotFoundException";
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const double MoveDeadlineSeconds = 180.0;

    private static Dictionary<string, string> Tags => new() { ["sim2real"] = "message-move-task-across-regions-rejected" };

    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>The botocore Error dict ``fn`` raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {(result is StartMessageMoveTaskResponse r ? $"{{TaskHandle: {r.TaskHandle}}}" : result)}");
    }

    private static async Task<string> Arn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code != NoSuchQueue)
        {
            throw error.Exception;
        }
    }

    private static async Task<(string Src, string Dlq, string DlqArn)> DlqPair(AmazonSQSClient sqs, FidelityContext ctx, string @base)
    {
        var dlq = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-dlq", Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, dlq));
        var dlqArn = await Arn(sqs, dlq);
        var src = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = @base + "-src",
            Tags = Tags,
            Attributes = new() { ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\": \"{dlqArn}\", \"maxReceiveCount\": \"1\"}}" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, src));
        return (src, dlq, dlqArn);
    }

    /// <summary>
    /// StartMessageMoveTask to a queue in another region is refused (ResourceNotFoundException).
    ///
    /// Nothing moves or is listed; a move from the same DLQ to its home-region source then completes.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-move-task-across-regions-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.MoveTasks, "StartMessageMoveTask to a destination in another region succeeded instead of being refused with ResourceNotFoundException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Sqs;
        var altRegion = OtherRegion(ctx.Region);
        var alt = ctx.Client(region: altRegion);
        var @base = FidelityContext.UniqueName("sim2real-behav-xr-move-");

        var (src, dlq, dlqArn) = await DlqPair(home, ctx, @base);
        var farDest = (await alt.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-dest", Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(alt, farDest));
        var farDestArn = await Arn(alt, farDest);

        var body = FidelityContext.UniqueName("parked-");
        await home.SendMessageAsync(new SendMessageRequest { QueueUrl = dlq, MessageBody = body });

        var err = await Error(() => home.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = dlqArn,
            DestinationArn = farDestArn,
        }));
        Ensure((err.Code, err.Message) == (NotFound, "The resource that you specified for the DestinationArn parameter doesn't exist."),
            $"a move task to a '{altRegion}' destination answered {err}");

        var tasks = (await home.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
        Ensure(tasks.Count == 0, $"the refused move left task(s) listed: [{string.Join(", ", tasks.Select(t => t.Status))}]");
        var far = await alt.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = farDest, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((far.Messages ?? []).Count == 0, "the refused move delivered into the far queue");

        // Positive control: the same DLQ moves to its home-region source.
        var handle = (await home.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = dlqArn,
            DestinationArn = await Arn(home, src),
        })).TaskHandle;
        Ensure(!string.IsNullOrEmpty(handle), "StartMessageMoveTask to the home-region source returned no TaskHandle");
        var got = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (got.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(MoveDeadlineSeconds))
        {
            foreach (var m in (await home.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = src, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                got.Add(m.Body);
                await home.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = src, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got.SequenceEqual([body]),
            $"the same-region move delivered [{string.Join(", ", got)}] to the source within {MoveDeadlineSeconds}s");
        string? status = null;
        deadline = Stopwatch.StartNew();
        while (status != "COMPLETED" && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var results = (await home.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results ?? [];
            status = results.Count > 0 ? results[0].Status : null;
            if (status != "COMPLETED")
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        Ensure(status == "COMPLETED", $"the same-region move task ended as '{status}'");
        far = await alt.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = farDest, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure((far.Messages ?? []).Count == 0, "the far queue received a message it was never sent");
    }
}
