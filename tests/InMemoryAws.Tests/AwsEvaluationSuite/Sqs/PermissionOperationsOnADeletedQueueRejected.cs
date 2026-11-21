// Translated from behavioral/sqs/check_permission_operations_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.permission-operations-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PermissionOperationsOnADeletedQueueRejected
{
    private const string Check = "permission-operations-on-a-deleted-queue-rejected";
    private const string NotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NotFoundMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// AddPermission and RemovePermission on a queue deleted earlier in the check are each
    /// refused with NonExistentQueue once an independent read already reports it gone, while a
    /// sibling queue still grants and then revokes the same permission.
    /// </summary>
    [Test]
    [DisplayName("sqs.permission-operations-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var account = ctx.Account;
        var @base = FidelityContext.UniqueName("sim2real-behav-permdel-");
        var doomed = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-gone", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, doomed));
        var sibling = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-kept", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, sibling));

        // Positive control: AddPermission and RemovePermission both work on a live queue.
        var controlLabel = FidelityContext.UniqueName("sim2realCtrlLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = sibling, Label = controlLabel, AWSAccountIds = [account], Actions = ["SendMessage"] });
        var policyAfterAdd = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = sibling, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        Ensure(policyAfterAdd.Contains(controlLabel, StringComparison.Ordinal),
            $"positive control AddPermission did not appear in the policy: '{policyAfterAdd}'");
        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = sibling, Label = controlLabel });
        var policyAfterRemove = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = sibling, AttributeNames = ["Policy"] })).Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfterRemove is null || !policyAfterRemove.Contains(controlLabel, StringComparison.Ordinal),
            $"positive control RemovePermission left the label in the policy: '{policyAfterRemove}'");

        // The doomed queue carries a permission at the moment it is deleted.
        var doomedLabel = FidelityContext.UniqueName("sim2realDoomedLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = doomed, Label = doomedLabel, AWSAccountIds = [account], Actions = ["SendMessage"] });
        await sqs.DeleteQueueAsync(doomed);

        // Deletion is eventually consistent; poll an independent read (GetQueueAttributes, not
        // AddPermission/RemovePermission) until it reports the queue gone, then hold both
        // permission operations to the same refusal.
        AwsError? readErr = null;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            readErr = await FidelityContext.CaptureError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = doomed, AttributeNames = ["QueueArn"] }));
            if (readErr is not null)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure((readErr?.Code, readErr?.Message) == (NotFound, NotFoundMsg),
            $"GetQueueAttributes on the deleted queue answered {readErr?.ToString() ?? "{}"} within {SettleSeconds}s");

        var addErr = await Error(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = doomed, Label = FidelityContext.UniqueName("sim2realLateLabel"), AWSAccountIds = [account], Actions = ["SendMessage"],
        }));
        Ensure((addErr.Code, addErr.Message) == (NotFound, NotFoundMsg), $"AddPermission on the deleted queue answered {addErr}");
        var removeErr = await Error(() => sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = doomed, Label = doomedLabel }));
        Ensure((removeErr.Code, removeErr.Message) == (NotFound, NotFoundMsg), $"RemovePermission on the deleted queue answered {removeErr}");
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == NotFound)
        {
        }
    }
}
