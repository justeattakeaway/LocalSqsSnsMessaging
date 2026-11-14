// Translated from behavioral/sqs/check_remove_permission_nonexistent_label_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.remove-permission-nonexistent-label-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemovePermissionNonexistentLabelRejected
{
    /// <summary>
    /// RemovePermission for a Label that was never granted fails instead of succeeding as
    /// a no-op, leaves the existing policy statement intact, and removing the real label
    /// still works.
    /// </summary>
    [Test]
    [DisplayName("sqs.remove-permission-nonexistent-label-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // sts.get_caller_identity()["Account"]
        var accountId = ctx.Account;
        const int timeoutS = 30;

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-rmperm-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var existingLabel = FidelityContext.UniqueName("ExistingLabel");
        await sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url,
            Label = existingLabel,
            AWSAccountIds = [accountId],
            Actions = ["SendMessage"],
        });
        var policyAfterAdd = await Poll(
            () => Policy(sqs, url),
            p => p is not null && p.Contains(existingLabel, StringComparison.Ordinal),
            timeoutS: timeoutS);
        Ensure(policyAfterAdd is not null && policyAfterAdd.Contains(existingLabel, StringComparison.Ordinal),
            $"AddPermission statement for '{existingLabel}' never appeared in policy: '{policyAfterAdd}'");

        var neverGrantedLabel = FidelityContext.UniqueName("NeverGrantedLabel");
        var (code, message) = await Error(() => sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = neverGrantedLabel }));
        Ensure(code == "InvalidParameterValue",
            $"expected InvalidParameterValue removing a never-granted Label, got '{code}'");
        Ensure(message == $"Value {neverGrantedLabel} for parameter Label is invalid. "
                          + "Reason: can't find label on existing policy.", $"unexpected refusal message '{message}'");

        // effect absent: the real, existing statement is still present
        var policyAfterReject = await Policy(sqs, url);
        Ensure(policyAfterReject is not null && policyAfterReject.Contains(existingLabel, StringComparison.Ordinal),
            $"rejected RemovePermission on '{neverGrantedLabel}' disturbed the existing "
            + $"statement for '{existingLabel}': '{policyAfterReject}'");

        // positive control: removing the label that actually exists succeeds
        await sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = existingLabel });
        var policyAfterControl = await Poll(
            () => Policy(sqs, url),
            p => p is null || !p.Contains(existingLabel, StringComparison.Ordinal),
            timeoutS: timeoutS);
        Ensure(policyAfterControl is null || !policyAfterControl.Contains(existingLabel, StringComparison.Ordinal),
            $"label '{existingLabel}' still present after RemovePermission: '{policyAfterControl}'");
    }

    // A queue with no policy set omits the "Attributes" key entirely.
    private static async Task<string?> Policy(IAmazonSQS sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
        .Attributes?.GetValueOrDefault("Policy");

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
