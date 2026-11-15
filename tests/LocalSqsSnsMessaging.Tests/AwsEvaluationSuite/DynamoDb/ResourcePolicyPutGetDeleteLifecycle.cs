// Translated from behavioral/dynamodb/check_resource_policy_put_get_delete_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-put-get-delete-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyPutGetDeleteLifecycle
{
    /// <summary>
    /// PutResourcePolicy attaches a resource policy visible via GetResourcePolicy (with the
    /// expected statement content and a fresh RevisionId), and DeleteResourcePolicy removes it
    /// so a subsequent GetResourcePolicy 404s (PolicyNotFoundException).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.resource-policy-put-get-delete-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;
        var tableName = FidelityContext.UniqueName("sim2real-behav-rp-");
        var createResp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        var tableArn = createResp.TableDescription.TableArn;
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);
        var policy = new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Sid"] = "SimparityBehavioralCheck",
                    ["Effect"] = "Allow",
                    ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                    ["Action"] = "dynamodb:DescribeTable",
                    ["Resource"] = tableArn,
                },
            },
        };
        var putResp = await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = JsonSerializer.Serialize(policy) });
        var putRevision = putResp.RevisionId;

        async Task<GetResourcePolicyResponse?> AttemptGetPolicy()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
            if (error is not null)
            {
                // PutResourcePolicy's effect can lag briefly; a GetResourcePolicy issued
                // immediately after may still 404 until it propagates.
                if (error.Code == "PolicyNotFoundException")
                {
                    return null;
                }

                throw error.Exception;
            }

            using var document = JsonDocument.Parse(resp!.Policy);
            return document.RootElement.GetProperty("Statement")[0].GetProperty("Sid").GetString() == "SimparityBehavioralCheck" ? resp : null;
        }

        var fetched = (await PollUntil(AttemptGetPolicy, resp => resp is not null, timeoutS: 60, intervalS: 3))!;
        using var fetchedPolicy = JsonDocument.Parse(fetched.Policy);
        var fetchedAction = fetchedPolicy.RootElement.GetProperty("Statement")[0].GetProperty("Action").GetString();
        Ensure(fetchedAction == "dynamodb:DescribeTable", $"expected fetched policy Action 'dynamodb:DescribeTable', got {Py.Repr(fetchedAction)}");
        var fetchedResource = fetchedPolicy.RootElement.GetProperty("Statement")[0].GetProperty("Resource").GetString();
        Ensure(fetchedResource == tableArn, $"expected fetched policy Resource {Py.Repr(tableArn)}, got {Py.Repr(fetchedResource)}");
        Ensure(
            fetched.RevisionId == putRevision,
            $"expected GetResourcePolicy RevisionId {Py.Repr(putRevision)} to match PutResourcePolicy's, got {Py.Repr(fetched.RevisionId)}");

        // DynamoDB's resource-policy control-plane APIs (Put/Get/Delete ResourcePolicy) enforce a
        // tight per-resource request-rate limit; the Put + several polling Get calls just made
        // against this same table can exhaust botocore's own 5 built-in retries and still get
        // ThrottlingException on Delete — retry with a longer backoff instead of treating that as a
        // hard failure.
        await DeleteResourcePolicyWithRetry(ddb, tableArn, putRevision);

        async Task<(string State, string? Message)> GetAfterDelete()
        {
            var error = await FidelityContext.CaptureError(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn }));
            return error is null ? ("STILL_PRESENT", null) : (error.Code ?? "UNKNOWN_ERROR", error.Message);
        }

        var (finalState, finalMessage) = await PollUntil(GetAfterDelete, state => state.State == "PolicyNotFoundException", timeoutS: 60, intervalS: 3);
        Ensure(
            finalState == "PolicyNotFoundException",
            $"expected GetResourcePolicy to report PolicyNotFoundException after DeleteResourcePolicy, got {Py.Repr(finalState)}");
        var expected = $"Resource-based policy not found for the provided ResourceArn: {tableArn}";
        Ensure(finalMessage == expected, $"GetResourcePolicy message {Py.Repr(finalMessage)}, expected {Py.Repr(expected)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    // DynamoDB briefly reports the table "still in use" (ResourceInUseException) immediately
    // after the PutResourcePolicy + polling GetResourcePolicy calls just issued against it, while
    // that mutation's own in-flight lock on the resource settles — retry, same as the
    // already-handled ThrottlingException, instead of treating that as a hard failure.
    private static async Task DeleteResourcePolicyWithRetry(IAmazonDynamoDB ddb, string tableArn, string revision, double timeoutS = 120, double intervalS = 10)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest
            {
                ResourceArn = tableArn,
                ExpectedRevisionId = revision,
            }));
            if (error is null)
            {
                return;
            }

            if (error.Code is "ThrottlingException" or "ResourceInUseException" && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            throw error.Exception;
        }
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
}
