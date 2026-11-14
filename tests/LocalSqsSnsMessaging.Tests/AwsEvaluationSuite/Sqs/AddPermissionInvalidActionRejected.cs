// Translated from behavioral/sqs/check_add_permission_invalid_action_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.add-permission-invalid-action-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AddPermissionInvalidActionRejected
{
    /// <summary>
    /// AddPermission with an action name outside the SQS action set is rejected,
    /// leaves the queue policy unchanged, and a valid Actions value still works.
    /// </summary>
    [Test]
    [DisplayName("sqs.add-permission-invalid-action-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var accountId = ctx.Account;

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-addperm-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var policyBefore = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
            .Attributes?.GetValueOrDefault("Policy");
        Ensure(policyBefore is null, "fresh queue should not have a policy yet");

        var badLabel = FidelityContext.UniqueName("BadActionLabel");
        var (code, message) = await Error(() => sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url,
            Label = badLabel,
            AWSAccountIds = [accountId],
            Actions = ["NotARealSqsAction"],
        }));
        Ensure(code == "InvalidParameterValue", $"expected InvalidParameterValue, got '{code}'");
        const string expectedMessage =
            "Value SQS:NotARealSqsAction for parameter ActionName is invalid. " +
            "Reason: Please refer to the appropriate WSDL for a list of valid actions.";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // effect absent: no policy was created by the rejected call
        var policyAfter = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
            .Attributes?.GetValueOrDefault("Policy");
        Ensure(policyAfter is null, "rejected AddPermission must not create a policy statement");

        // positive control: a valid action name is accepted and persisted
        var goodLabel = FidelityContext.UniqueName("GoodActionLabel");
        ctx.OnCleanup(() => Try(() => sqs.RemovePermissionAsync(new RemovePermissionRequest { QueueUrl = url, Label = goodLabel })));
        await sqs.AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = url,
            Label = goodLabel,
            AWSAccountIds = [accountId],
            Actions = ["SendMessage"],
        });
        var policyControl = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
            .Attributes!["Policy"];
        Ensure(policyControl.Contains(goodLabel, StringComparison.Ordinal),
            $"expected '{goodLabel}' in queue policy after a valid AddPermission, got '{policyControl}'");
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; raise if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected rejection but call succeeded with {result}");
    }

    private static async Task Try<T>(Func<Task<T>> fn)
    {
        try
        {
            await fn();
        }
        catch (AmazonSQSException exc) when (exc.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
        }
    }
}
