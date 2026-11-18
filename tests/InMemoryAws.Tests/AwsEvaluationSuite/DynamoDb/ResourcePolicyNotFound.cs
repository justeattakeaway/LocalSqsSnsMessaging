// Translated from behavioral/dynamodb/check_resource_policy_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyNotFound
{
    private const string Check = "resource-policy-not-found";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>
    /// DeleteTable can hit a transient ResourceInUseException while the
    /// resource-policy mutations this check just issued against the same table
    /// are still settling -- retry past that instead of leaking the table.
    /// </summary>
    private static async Task DeleteTablePastLock(IAmazonDynamoDB ddb, string tableName, double timeoutS = 50.0, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>Return (code, message) of the ClientError <paramref name="fn"/> raises; an accepted call fails the check.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Call fn() and return ("SUCCESS", None) or the ClientError's (code, message) --
    /// retrying while that code is ThrottlingException or ResourceInUseException.
    /// Resource-policy mutations on the SAME resource are rate-limited to roughly
    /// one per ~15s (ThrottlingException), and a mutation issued shortly after a
    /// prior Put/Get on the same table can also see a transient
    /// ResourceInUseException while that mutation's own in-flight lock settles --
    /// a check that issues back-to-back Put/Get/Delete/Delete calls must ride out
    /// both rather than treating either as the behavior under test.
    /// </summary>
    private static Task<(string? Code, string? Message)> ErrorCodePastThrottle(Func<Task> fn, double timeoutS = 60.0, double intervalS = 5.0)
    {
        async Task<(string? Code, string? Message)> Attempt()
        {
            var error = await FidelityContext.CaptureError(fn);
            return error is null ? ("SUCCESS", null) : (error.Code, error.Message);
        }

        return PollUntil(
            Attempt,
            r => r.Code is not ("ThrottlingException" or "ResourceInUseException"),
            timeoutS: timeoutS,
            intervalS: intervalS,
            description: "past resource-policy modification-rate cooldown");
    }

    /// <summary>
    /// GetResourcePolicy on a table without a policy raises PolicyNotFoundException with the exact message; an attached
    /// policy reads back, DeleteResourcePolicy removes it, and a repeat delete succeeds while Get raises the same error.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.resource-policy-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTablePastLock(ddb, t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        var notFoundMessage = $"Resource-based policy not found for the provided ResourceArn: {arn}";
        var (baselineCode, baselineMessage) = await Error(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
        Ensure(baselineCode == "PolicyNotFoundException", $"expected PolicyNotFoundException, got {baselineCode}");
        Ensure(baselineMessage == notFoundMessage, $"unexpected message {Py.Repr(baselineMessage)}");
        var accountId = arn.Split(':')[4];
        var policy =
            "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"," +
            $"\"Principal\":{{\"AWS\":\"arn:aws:iam::{accountId}:root\"}}," +
            "\"Action\":\"dynamodb:DescribeTable\",\"Resource\":\"" + arn + "\"}]}";
        var putResp = await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = arn, Policy = policy });
        var revision = putResp.RevisionId;

        // GetResourcePolicy is eventually-consistent immediately after a Put.
        async Task<(string State, GetResourcePolicyResponse? Response)> GetPolicy()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
            if (error is null)
            {
                return ("ok", resp);
            }

            if (error.Code == "PolicyNotFoundException")
            {
                return ("pending", null);
            }

            throw error.Exception;
        }

        var (_, attached) = await PollUntil(GetPolicy, r => r.State == "ok", timeoutS: 30, intervalS: 2.0, description: "policy visible after PutResourcePolicy");
        var attachedRepr = Py.Repr(new Dictionary<string, object?> { ["Policy"] = attached!.Policy, ["RevisionId"] = attached.RevisionId });
        Ensure(attached.RevisionId == revision, attachedRepr);
        Ensure(attached.Policy.Contains("DescribeTable", StringComparison.Ordinal), attachedRepr);

        // First deletion -- a write against the same resource shortly after the
        // Put, so ride out DynamoDB's resource-policy modification-rate cooldown.
        var (firstDeleteResult, _) = await ErrorCodePastThrottle(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn }));
        Ensure(firstDeleteResult == "SUCCESS", $"expected DeleteResourcePolicy to succeed, got {firstDeleteResult}");

        async Task<(string? Code, string? Message)> GetAfterDelete()
        {
            var error = await FidelityContext.CaptureError(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
            return error is null ? ("still-present", null) : (error.Code, error.Message);
        }

        var (finalCode, finalMessage) = await PollUntil(
            GetAfterDelete,
            r => r.Code == "PolicyNotFoundException",
            timeoutS: 30,
            intervalS: 2.0,
            description: "policy gone after DeleteResourcePolicy");
        Ensure(finalCode == "PolicyNotFoundException", finalCode ?? "None");
        Ensure(finalMessage == notFoundMessage, $"unexpected message {Py.Repr(finalMessage)}");

        // DeleteResourcePolicy is idempotent: calling it again once the policy is
        // already gone succeeds as a no-op -- a second write against the same
        // resource in quick succession, so ride out the modification-rate cooldown.
        var (redeleteCode, _) = await ErrorCodePastThrottle(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn }));
        Ensure(redeleteCode == "SUCCESS", $"expected DeleteResourcePolicy to be an idempotent no-op success, got {redeleteCode}");

        // Independent terminal read: the idempotent redelete must not have a side
        // effect of its own (e.g. leaving a policy behind) -- confirm via a fresh
        // GetResourcePolicy, not the redelete call's own response.
        var (stillAbsentCode, stillAbsentMessage) = await ErrorCodePastThrottle(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
        Ensure(stillAbsentCode == "PolicyNotFoundException", $"expected no policy to exist after the idempotent redelete, got {stillAbsentCode}");
        Ensure(stillAbsentMessage == notFoundMessage, $"unexpected message {Py.Repr(stillAbsentMessage)}");
    }
}
