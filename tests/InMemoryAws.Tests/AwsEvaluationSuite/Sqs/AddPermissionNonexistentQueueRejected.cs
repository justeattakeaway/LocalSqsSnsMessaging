// Translated from behavioral/sqs/check_add_permission_nonexistent_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-nonexistent-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionNonexistentQueueRejected
{
    private const string ErrNonexistentQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string MsgNonexistentQueue = "The specified queue does not exist.";

    /// <summary>
    /// AddPermission against a queue URL naming no live queue fails with the NonExistentQueue
    /// error, and AddPermission against a real queue still succeeds and is readable afterward.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-nonexistent-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-addpermnx-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var ghostUrl = $"{url[..url.LastIndexOf('/')]}/{Guid.NewGuid().ToString("N")[..12]}-ghost";
        var (result, err) = await FidelityContext.Capture(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = ghostUrl, Label = "GhostLabel", AWSAccountIds = [accountId], Actions = ["SendMessage"],
        }));
        if (err is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        Ensure(err.Code == ErrNonexistentQueue,
            $"expected {ErrNonexistentQueue} for AddPermission on a queue url naming no live queue, got {err}");
        Ensure(err.Message == MsgNonexistentQueue, $"unexpected refusal message {err}");

        // positive control: AddPermission against the real queue succeeds, and the statement is
        // readable through an independent operation, GetQueueAttributes.
        var label = FidelityContext.UniqueName("RealLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = label, AWSAccountIds = [accountId], Actions = ["SendMessage"] });
        var policy = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        using var doc = JsonDocument.Parse(policy);
        var sids = doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(s => s.TryGetProperty("Sid", out var sid) ? sid.GetString() : null)
            .ToHashSet();
        Ensure(sids.Contains(label), $"expected '{label}' in the real queue's policy statements, got {{{string.Join(", ", sids)}}}");
    }
}
