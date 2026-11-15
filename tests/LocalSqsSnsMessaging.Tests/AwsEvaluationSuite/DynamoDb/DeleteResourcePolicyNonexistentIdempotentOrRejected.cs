// Translated from behavioral/dynamodb/check_delete_resource_policy_nonexistent_idempotent_or_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-resource-policy-nonexistent-idempotent-or-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteResourcePolicyNonexistentIdempotentOrRejected
{
    /// <summary>
    /// DeleteResourcePolicy on a table without a policy succeeds as a no-op, while with an ExpectedRevisionId it
    /// raises PolicyNotFoundException with the exact message; with an attached policy's real revision it removes it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-resource-policy-nonexistent-idempotent-or-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;
        var tableName = FidelityContext.UniqueName("sim2real-delrespolicy-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        var tableArn = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableArn;

        // No policy has ever been attached to this table. Right after CreateTable, the
        // separately-provisioned resource-policy control plane can still be catching up, which
        // surfaces as a transient ResourceNotFoundException rather than the real "no policy attached"
        // outcome -- ride that out rather than treating it as the answer.
        var bareDelete = await PollPastTransient(
            () => WithThrottleRetry(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn })));
        Ensure((bareDelete.RevisionId ?? "") == "", Py.Repr(bareDelete.RevisionId));

        // Independent read, through a different operation than the one that just ran: the no-op must
        // not have side-effected a policy into existence.
        var (afterBareDelete, afterBareMsg) = await Error(async () =>
            await WithThrottleRetry(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn })));
        Ensure(afterBareDelete == "PolicyNotFoundException", $"expected no policy to exist after the no-op delete, got {Py.Repr(afterBareDelete)}");
        var expectedGet = $"Resource-based policy not found for the provided ResourceArn: {tableArn}";
        Ensure(afterBareMsg == expectedGet, $"expected message {Py.Repr(expectedGet)}, got {Py.Repr(afterBareMsg)}");

        // With an ExpectedRevisionId supplied, absence of a matching policy becomes an error.
        var (code, message) = await Error(async () =>
            await WithThrottleRetry(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest
            {
                ResourceArn = tableArn,
                ExpectedRevisionId = "nonexistent-revision-id",
            })));
        Ensure(code == "PolicyNotFoundException", $"expected PolicyNotFoundException, got {code ?? "None"}");
        var expectedMsg =
            "Resource-based policy not found for the provided ResourceArn: Requested update for " +
            $"policy with revision id nonexistent-revision-id, but table {tableName} has no " +
            "associated policy.";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // Positive control: attach a real policy and prove the identical shape of call -- Delete with
        // an ExpectedRevisionId -- succeeds and genuinely removes it once the revision actually matches.
        // Without this, "rejects a bogus ExpectedRevisionId" is indistinguishable from "rejects every
        // DeleteResourcePolicy that carries an ExpectedRevisionId at all".
        var policyDoc = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["Sid"] = "AllowDescribe",
                    ["Effect"] = "Allow",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                    ["Action"] = "dynamodb:DescribeTable",
                    ["Resource"] = tableArn,
                },
            },
        });
        var putResp = await WithThrottleRetry(() => ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = policyDoc }));
        var realRevision = putResp.RevisionId;
        await PollUntil(() => PolicyState(ddb, tableArn), state => state == "ATTACHED", timeoutSeconds: 60, intervalSeconds: 5);

        await WithThrottleRetry(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn, ExpectedRevisionId = realRevision }));

        // Independent read again: the matching-revision delete must have actually removed the policy,
        // not merely returned success while leaving it in place. GetResourcePolicy is eventually
        // consistent with a preceding Delete just as it is with a preceding Put, so poll rather than
        // trusting a single immediate read.
        await PollUntil(() => PolicyState(ddb, tableArn), state => state == "ABSENT", timeoutSeconds: 60, intervalSeconds: 5);
    }

    /// <summary>Call fn(); return the botocore (Error.Code, Error.Message). An accepted call raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// 'ATTACHED' when GetResourcePolicy returns a policy, 'ABSENT' on PolicyNotFoundException;
    /// any other error propagates.
    /// </summary>
    private static async Task<string> PolicyState(IAmazonDynamoDB ddb, string tableArn)
    {
        var error = await FidelityContext.CaptureError(() =>
            WithThrottleRetry(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn })));
        if (error is not null)
        {
            if (error.Code != "PolicyNotFoundException")
            {
                throw error.Exception;
            }

            return "ABSENT";
        }

        return "ATTACHED";
    }

    private static async Task<T> WithThrottleRetry<T>(Func<Task<T>> fn, double timeoutSeconds = 120.0, double intervalSeconds = 8.0)
        where T : class
    {
        // DynamoDB's resource-policy mutating/reading APIs (Put/Get/DeleteResourcePolicy) enforce a
        // very low per-resource request rate; back-to-back calls in this check can trip
        // ThrottlingException well within botocore's own client-side retry budget. Ride out a longer
        // horizon instead of treating the throttle itself as the behavior under test.
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (result, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                return result!;
            }

            if (error.Code != "ThrottlingException")
            {
                throw error.Exception;
            }

            lastErr = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"still throttled after {timeoutSeconds}s: {lastErr}");
    }

    private static async Task<T> PollPastTransient<T>(Func<Task<T>> fn, double timeoutSeconds = 60.0, double intervalSeconds = 5.0)
        where T : class
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (result, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                return result!;
            }

            if (error.Code != "ResourceNotFoundException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    private static async Task<string> PollUntil(Func<Task<string>> probe, Func<string, bool> predicate, double timeoutSeconds, double intervalSeconds)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} did not become ACTIVE within {timeoutSeconds}s");
    }
}
