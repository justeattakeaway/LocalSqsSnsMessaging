// Translated from behavioral/events/check_remove_permission_nonexistent_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.remove-permission-nonexistent-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemovePermissionNonexistentFails
{
    /// <summary>
    /// OVERLY-PERMISSIVE: RemovePermission for a StatementId that was never granted must fail,
    /// without disturbing an unrelated granted statement, and must succeed once that same
    /// StatementId is actually granted.
    /// </summary>
    [Test]
    [DisplayName("events.remove-permission-nonexistent-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-behav", Value = bus }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        const string controlId = "granted-control";
        await ev.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Action = "events:PutEvents",
            Principal = "111122223333",
            StatementId = controlId,
        });
        var policy = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(policy.Contains(controlId, StringComparison.Ordinal), $"expected positive control statement in policy, got: {policy}");

        const string absentId = "never-granted";
        var (result, err) = await FidelityContext.Capture(() =>
            ev.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, StatementId = absentId }));
        if (err is null)
        {
            throw Fail($"RemovePermission for a never-granted StatementId SUCCEEDED: {result}");
        }

        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err}");
        const string expectedMessage = "Statement with the provided id does not exist.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var policyAfter = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(
            policyAfter.Contains(controlId, StringComparison.Ordinal),
            $"failed RemovePermission on '{absentId}' must not disturb an unrelated granted statement, got policy: {policyAfter}");
        Ensure(
            !policyAfter.Contains(absentId, StringComparison.Ordinal),
            $"StatementId '{absentId}' must not appear in a policy it was never granted in, got: {policyAfter}");

        // restore: grant the previously-nonexistent statement, then the identical call must succeed
        await ev.PutPermissionAsync(new PutPermissionRequest
        {
            EventBusName = bus,
            Action = "events:PutEvents",
            Principal = "111122223333",
            StatementId = absentId,
        });
        var policyGranted = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(policyGranted.Contains(absentId, StringComparison.Ordinal), $"expected restored statement in policy, got: {policyGranted}");

        await ev.RemovePermissionAsync(new RemovePermissionRequest { EventBusName = bus, StatementId = absentId });
        var policyFinal = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Policy ?? "";
        Ensure(
            !policyFinal.Contains(absentId, StringComparison.Ordinal),
            $"identical RemovePermission call must now succeed once the statement exists, got policy: {policyFinal}");
    }
}
