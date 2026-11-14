// Translated from behavioral/sqs/check_op_tag_and_permission_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-tag-and-permission-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpTagAndPermissionValidationRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgTag = "Tag values must be between 0 and 256 characters in length.";
    private const string MsgLabel = "Value NoSuchLabel for parameter Label is invalid. Reason: can't find label on existing policy.";
    private const string MsgAction = "Value SQS:NotARealAction for parameter ActionName is invalid. Reason: Please refer to the appropriate WSDL for a list of valid actions.";

    private static async Task<AwsError> ClientError(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, call unexpectedly succeeded");

    private static async Task Try(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code is not ("AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// TagQueue with an oversized tag value, RemovePermission for an unknown Label, and
    /// AddPermission with an invalid action are each rejected without side effect, and the
    /// same operation still succeeds with valid input.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-tag-and-permission-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // sts.get_caller_identity()["Account"]
        var account = ctx.Account;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // --- TagQueue: an oversized tag value is rejected (distinct from the oversized-key
        var oversizedValue = new string('v', 300);
        var errTag = await ClientError(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["Environment"] = oversizedValue } }));
        Ensure(errTag.Code == "InvalidParameterValue", $"got {errTag}");
        Ensure(errTag.Message == MsgTag, $"got {errTag}");
        var tagsAfterReject = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(!tagsAfterReject.ContainsKey("Environment"), "rejected oversized tag value was still stored");
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["Environment"] = "sim2real-test" } });
        ctx.OnCleanup(() => Try(() => sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["Environment"] })));
        var tagsAfterControl = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(tagsAfterControl.GetValueOrDefault("Environment") == "sim2real-test", "valid tag was not persisted");

        // --- RemovePermission: an unknown Label is rejected, the real statement is untouched ---
        var existingLabel = FidelityContext.UniqueName("ExistingLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = existingLabel, AWSAccountIds = [account], Actions = ["SendMessage"] });
        var errMissing = await ClientError(() => sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = "NoSuchLabel" }));
        Ensure(errMissing.Code == "InvalidParameterValue", $"got {errMissing}");
        Ensure(errMissing.Message == MsgLabel, $"got {errMissing}");
        var policyAfterReject = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfterReject is not null && policyAfterReject.Contains(existingLabel, StringComparison.Ordinal),
            "rejected RemovePermission disturbed the existing statement");
        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = existingLabel });
        var policyAfterControl = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfterControl is null || !policyAfterControl.Contains(existingLabel, StringComparison.Ordinal),
            "RemovePermission with a real label did not take effect");

        // --- AddPermission: an invalid Action name is rejected without creating a statement ---
        var badLabel = FidelityContext.UniqueName("BadActionLabel");
        var errAction = await ClientError(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url,
            Label = badLabel,
            AWSAccountIds = [account],
            Actions = ["NotARealAction"],
        }));
        Ensure(errAction.Code == "InvalidParameterValue", $"got {errAction}");
        Ensure(errAction.Message == MsgAction, $"got {errAction}");
        var policyAfterActionReject = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfterActionReject is null || !policyAfterActionReject.Contains(badLabel, StringComparison.Ordinal),
            "rejected AddPermission created a statement");
        var goodLabel = FidelityContext.UniqueName("GoodActionLabel");
        ctx.OnCleanup(() => Try(() => sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = goodLabel })));
        await sqs.AddPermissionAsync(new AddPermissionRequest { QueueUrl = url, Label = goodLabel, AWSAccountIds = [account], Actions = ["SendMessage"] });
        var policyControl = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] })).Attributes!["Policy"];
        Ensure(policyControl.Contains(goodLabel, StringComparison.Ordinal), "AddPermission with a valid action did not persist");
    }
}
