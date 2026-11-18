// Translated from behavioral/dynamodb/check_resource_policy_delete_nonexistent_is_noop.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-delete-nonexistent-is-noop</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyDeleteNonexistentIsNoop
{
    private const string Check = "resource-policy-delete-nonexistent-is-noop";

    /// <summary>
    /// DeleteResourcePolicy on a table without a policy succeeds and GetResourcePolicy still raises
    /// PolicyNotFoundException with the exact message; with a policy attached, the same delete returns its RevisionId
    /// and removes it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.resource-policy-delete-nonexistent-is-noop")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-respol-neg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, tableName));
        await WaitTableActive(ddb, tableName);
        var tableArn = (await ddb.DescribeTableAsync(tableName)).Table.TableArn;

        // Right after a table becomes ACTIVE, the (separately-provisioned)
        // resource-policy control plane can still be catching up with the new
        // table, which surfaces as a transient ResourceNotFoundException instead
        // of the real "no policy attached" outcome. Poll past that transient
        // state before asserting the actual (successful, no-op) delete. Real AWS
        // also enforces a very low request rate on the resource-policy mutating
        // APIs (PutResourcePolicy/DeleteResourcePolicy) -- calling them in a tight
        // poll loop exhausts botocore's own retry budget and surfaces as
        // ThrottlingException, so route every call for this policy through a
        // longer-horizon retry that treats throttling as transient too.
        async Task<string> NoopDeleteOutcome()
        {
            var error = await FidelityContext.CaptureError(() => CallWithThrottleRetry(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn })));
            return error is null ? "succeeded" : error.Code!;
        }

        var outcome = await PollUntil(NoopDeleteOutcome, c => c != "ResourceNotFoundException", timeoutS: 60, intervalS: 5);
        Ensure(outcome == "succeeded", $"expected DeleteResourcePolicy on a policy-less table to succeed as a no-op, got {Py.Repr(outcome)}");

        // Confirm the no-op truly created no policy: GetResourcePolicy still 404s.
        var notFoundMessage = $"Resource-based policy not found for the provided ResourceArn: {tableArn}";
        var (afterCode, afterMessage) = await Error(() => CallWithThrottleRetry(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn })));
        Ensure(afterCode == "PolicyNotFoundException", $"expected no policy to exist after the no-op delete, got {Py.Repr(afterCode)}");
        Ensure(afterMessage == notFoundMessage, $"unexpected message {Py.Repr(afterMessage)}");

        // Positive control: attach a policy for real, then the identical delete call succeeds.
        var policyDoc = new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Sid"] = "AllowDescribe",
                    ["Effect"] = "Allow",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{tableArn.Split(':')[4]}:root" },
                    ["Action"] = "dynamodb:DescribeTable",
                    ["Resource"] = tableArn,
                },
            },
        };
        await CallWithThrottleRetry(() => ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = JsonSerializer.Serialize(policyDoc) }));
        await PollUntil(() => HasPolicy(ddb, tableArn), v => v, timeoutS: 60, intervalS: 5);
        var deleteResp = await CallWithThrottleRetry(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn }));
        Ensure(!string.IsNullOrEmpty(deleteResp.RevisionId), $"expected a real delete to report the removed policy's RevisionId, got {Py.Repr(deleteResp.RevisionId)}");

        // Independent terminal read, at the destination: the real delete must have actually
        // removed the policy, not just returned a success response. Confirmed via GetResourcePolicy
        // -- a different operation than the one that mutated -- rather than trusting delete_resp alone.
        await PollUntil(() => HasPolicy(ddb, tableArn), v => !v, timeoutS: 60, intervalS: 5);
        var (finalAfterCode, finalAfterMessage) = await Error(() => CallWithThrottleRetry(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn })));
        Ensure(finalAfterCode == "PolicyNotFoundException", $"expected GetResourcePolicy to 404 after the real delete removed the policy, got {Py.Repr(finalAfterCode)}");
        Ensure(finalAfterMessage == notFoundMessage, $"unexpected message {Py.Repr(finalAfterMessage)}");
    }

    private static async Task<bool> HasPolicy(IAmazonDynamoDB ddb, string resourceArn)
    {
        var error = await FidelityContext.CaptureError(() => CallWithThrottleRetry(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = resourceArn })));
        if (error is null)
        {
            return true;
        }

        if (error.Code == "PolicyNotFoundException")
        {
            return false;
        }

        throw error.Exception;
    }

    // DynamoDB's resource-based-policy mutating/reading APIs
    // (Put/Get/DeleteResourcePolicy) enforce a very low per-resource request
    // rate on real AWS; botocore's own client-side retries exhaust quickly
    // under that limit and surface as ThrottlingException. Retry across a
    // longer horizon with a wider interval instead of failing on the first
    // throttle.
    private static async Task<T> CallWithThrottleRetry<T>(Func<Task<T>> fn, double timeoutS = 120, double intervalS = 8)
        where T : class
    {
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
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
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"still throttled after {timeoutS}s: {lastErr}");
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

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
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

        throw Fail($"condition not met within {timeoutS}s, last value: {Py.Repr(last)}");
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120) =>
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value,
            s => s == "ACTIVE",
            timeoutS: timeoutS,
            intervalS: 3);

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }
}
