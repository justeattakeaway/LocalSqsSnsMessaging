// Translated from behavioral/dynamodb/check_put_resource_policy_revision_id_conflict_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-resource-policy-revision-id-conflict-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutResourcePolicyRevisionIdConflictRejected
{
    private const string CheckId = "put-resource-policy-revision-id-conflict-rejected";

    /// <summary>
    /// PutResourcePolicy with an ExpectedRevisionId another writer has replaced raises PolicyNotFoundException with the
    /// exact message and keeps that writer's revision, while the same call with the current revision lands.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-resource-policy-revision-id-conflict-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;

        var tableName = FidelityContext.UniqueName("sim2real-behav-rpr-");
        var createResp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        var tableArn = createResp.TableDescription.TableArn;
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.ToString(),
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);

        string Policy(string sid, string action)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["Sid"] = sid,
                        ["Effect"] = "Allow",
                        ["Principal"] = new Dictionary<string, string> { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                        ["Action"] = action,
                        ["Resource"] = tableArn,
                    },
                },
            });
        }

        // Writer A's initial read: install the first policy revision and remember it as "what A saw".
        // Note: policy Sid values must be alphanumeric only (no hyphens) or DynamoDB rejects the
        // policy document with a ValidationException before revision-checking even comes into play.
        var putA = await PutPolicyWithRetry(ddb, tableArn, Policy("WriterAInitial", "dynamodb:DescribeTable"));
        var staleRevision = putA.RevisionId;
        ctx.OnCleanup(() => SafeDeleteResourcePolicy(ddb, tableArn));

        // Writer B concurrently replaces the policy WITHOUT any revision check, moving the resource
        // to a new, current revision that writer A does not know about.
        var putB = await PutPolicyWithRetry(ddb, tableArn, Policy("WriterBConcurrent", "dynamodb:GetItem"));
        var currentRevision = putB.RevisionId;
        Ensure(currentRevision != staleRevision, "PutResourcePolicy did not advance the RevisionId");

        // Writer A now submits its change, still carrying the now-stale revision it originally read.
        var (code, message) = await ErrorCodeAndMessage(() => ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest
        {
            ResourceArn = tableArn,
            Policy = Policy("WriterAStaleSubmit", "dynamodb:PutItem"),
            ExpectedRevisionId = staleRevision,
        }));
        Ensure(code == "PolicyNotFoundException", Py.Repr((code, message)));
        var expected = "Resource-based policy not found for the provided ResourceArn: Requested update for "
            + $"policy with revision id {staleRevision}, but the policy associated to target table "
            + $"{tableName} has revision id {currentRevision}.";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        // The concurrent writer's policy (B's) must be UNCHANGED by A's rejected, stale submission.
        var fetched = await GetPolicyMatchingSid(ddb, tableArn, "WriterBConcurrent");
        Ensure(
            fetched.RevisionId == currentRevision,
            $"stale rejected write must leave the policy at B's revision {Py.Repr(currentRevision)}, found {Py.Repr(fetched.RevisionId)}");

        // A correctly-targeted PutResourcePolicy (using the CURRENT revision) is accepted.
        var putC = await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest
        {
            ResourceArn = tableArn,
            Policy = Policy("WriterARetry", "dynamodb:PutItem"),
            ExpectedRevisionId = currentRevision,
        });
        var newRevision = putC.RevisionId;
        Ensure(
            newRevision != currentRevision,
            $"a correctly-targeted PutResourcePolicy must advance the RevisionId past {Py.Repr(currentRevision)}, got the same value back");

        // Independent terminal read at the destination: GetResourcePolicy, not the mutating
        // PutResourcePolicy's own response, must show the retried write actually landed.
        var landed = await GetPolicyMatchingSid(ddb, tableArn, "WriterARetry");
        Ensure(
            landed.RevisionId == newRevision,
            $"GetResourcePolicy must reflect the accepted retry's revision {Py.Repr(newRevision)}, found {Py.Repr(landed.RevisionId)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task SafeDeleteResourcePolicy(IAmazonDynamoDB ddb, string tableArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = tableArn }));
        if (error is not null && error.Code is not ("PolicyNotFoundException" or "ResourceNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail("expected the call to be refused, but it succeeded");
    }

    /// <summary>
    /// DynamoDB's resource-policy control-plane surface enforces a tight per-resource request
    /// rate; back-to-back PutResourcePolicy calls issued against the same, just-created table can
    /// transiently 429 (ThrottlingException) or report the resource as still settling
    /// (ResourceInUseException) — retry for a bounded time rather than treating either as a hard
    /// failure.
    /// </summary>
    private static async Task<PutResourcePolicyResponse> PutPolicyWithRetry(
        IAmazonDynamoDB ddb, string tableArn, string policy, double timeoutS = 90, double intervalS = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                return await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = tableArn, Policy = policy });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode is "ThrottlingException" or "ResourceInUseException" && clock.Elapsed.TotalSeconds < timeoutS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }
    }

    /// <summary>
    /// PutResourcePolicy's effect can lag briefly; a GetResourcePolicy issued immediately after
    /// may still 404 (PolicyNotFoundException) or briefly return the PREVIOUS statement -- poll
    /// until the expected statement is actually visible.
    /// </summary>
    private static async Task<GetResourcePolicyResponse> GetPolicyMatchingSid(
        IAmazonDynamoDB ddb, string tableArn, string expectedSid, double timeoutS = 60, double intervalS = 3)
    {
        async Task<GetResourcePolicyResponse?> Attempt()
        {
            GetResourcePolicyResponse resp;
            try
            {
                resp = await ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = tableArn });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode is "ThrottlingException" or "PolicyNotFoundException")
            {
                return null;
            }

            using var document = JsonDocument.Parse(resp.Policy);
            return document.RootElement.GetProperty("Statement")[0].GetProperty("Sid").GetString() == expectedSid ? resp : null;
        }

        var result = await PollUntil(Attempt, r => r is not null, timeoutS: timeoutS, intervalS: intervalS);
        return result!;
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed.TotalSeconds < timeoutS)
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
