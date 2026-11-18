// Translated from behavioral/dynamodb/check_get_resource_policy_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-resource-policy-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetResourcePolicyNotFoundRejected
{
    /// <summary>
    /// GetResourcePolicy on a table without a policy raises PolicyNotFoundException with the exact message; after
    /// PutResourcePolicy it returns that policy and revision, and after DeleteResourcePolicy the same error again.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-resource-policy-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;
        var tableName = FidelityContext.UniqueName("sim2real-respolicy-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        var tableArn = (await ddb.DescribeTableAsync(tableName)).Table.TableArn;

        var expectedMessage = $"Resource-based policy not found for the provided ResourceArn: {tableArn}";
        var (baselineCode, baselineMessage) = await ErrorCodeOrRaise(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
        Ensure(baselineCode == "PolicyNotFoundException", $"expected PolicyNotFoundException, got {baselineCode}");
        Ensure(baselineMessage == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(baselineMessage)}");

        var policy = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Effect"] = "Allow",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                    ["Action"] = "dynamodb:DescribeTable",
                    ["Resource"] = tableArn,
                },
            },
        });
        var putResp = await RetryPastThrottle(() => ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = policy }));
        var revision = putResp.RevisionId;

        var attached = await PollUntil(
            () => AttemptGet(ddb, tableArn),
            r => r is not null,
            timeoutSeconds: 30.0,
            intervalSeconds: 2.0,
            description: "policy visible after PutResourcePolicy");
        Ensure(attached!.RevisionId == revision, Py.Repr((attached.RevisionId, attached.Policy)));
        Ensure(attached.Policy.Contains("DescribeTable", StringComparison.Ordinal), Py.Repr((attached.RevisionId, attached.Policy)));

        await RetryPastThrottle(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn, ExpectedRevisionId = revision }));

        var (finalCode, finalMessage) = await PollUntil(
            () => ProbeErrorCode(ddb, tableArn),
            c => c.Code == "PolicyNotFoundException",
            timeoutSeconds: 30.0,
            intervalSeconds: 2.0,
            description: "policy gone after DeleteResourcePolicy");
        Ensure(finalCode == "PolicyNotFoundException", finalCode ?? "None");
        Ensure(finalMessage == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(finalMessage)}");
    }

    private static async Task<(string? Code, string? Message)> ErrorCodeOrRaise<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to raise a ClientError, it returned successfully");
        }

        return (error.Code, error.Message);
    }

    private static async Task<(string? Code, string? Message)> ProbeErrorCode(IAmazonDynamoDB ddb, string tableArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
        return error is null ? ("SUCCESS", null) : (error.Code, error.Message);
    }

    private static async Task<GetResourcePolicyResponse?> AttemptGet(IAmazonDynamoDB ddb, string tableArn)
    {
        var (resp, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
        if (error is not null)
        {
            if (error.Code == "PolicyNotFoundException")
            {
                return null;
            }

            throw error.Exception;
        }

        return resp;
    }

    /// <summary>
    /// DynamoDB's resource-policy control-plane APIs (Put/DeleteResourcePolicy) enforce a tight
    /// per-resource modification-rate limit; issuing them back-to-back on the same table can draw
    /// ThrottlingException or a transient ResourceInUseException while the prior mutation settles.
    /// </summary>
    private static async Task<T> RetryPastThrottle<T>(Func<Task<T>> fn, double timeoutSeconds = 60.0, double intervalSeconds = 5.0)
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

            if (error.Code is "ThrottlingException" or "ResourceInUseException" && clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} did not become ACTIVE within {timeoutSeconds}s");
    }
}
