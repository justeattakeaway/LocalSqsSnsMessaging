// Translated from behavioral/dynamodb/check_resource_policy_revision_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-revision-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyRevisionMismatch
{
    private const string Check = "resource-policy-revision-mismatch";
    private const string ErrPolicyNotFound = "PolicyNotFoundException";

    /// <summary>The suite's cleanup budget, shared by the check's teardowns.</summary>
    private const double CleanupS = 900.0;

    /// <summary>Run a delete; retry ResourceInUseException while a policy update settles, not-found is done.</summary>
    private static async Task RetryInUse(Func<Task> fn, Dictionary<string, FidelityStopwatch> cleanupDeadline)
    {
        if (!cleanupDeadline.TryGetValue("t", out var clock))
        {
            clock = FidelityTime.StartNew();
            cleanupDeadline["t"] = clock;
        }

        while (true)
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                return;
            }

            if (error.Code is "ResourceNotFoundException" or ErrPolicyNotFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(CleanupS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Call fn(); return its botocore (error code, message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING an update real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected PutResourcePolicy to be rejected, but it SUCCEEDED: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// PutResourcePolicy's effect on GetResourcePolicy can lag briefly -- a read issued
    /// immediately after a successful put may still 404 with PolicyNotFoundException. Poll until
    /// the expected revision is actually visible.
    /// </summary>
    private static async Task<GetResourcePolicyResponse> PollForRevision(IAmazonDynamoDB ddb, string arn, string expectedRevision, double timeoutS = 60, double intervalS = 3)
    {
        var clock = FidelityTime.StartNew();
        GetResourcePolicyResponse? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
            if (error is not null)
            {
                if (error.Code != ErrPolicyNotFound)
                {
                    throw error.Exception;
                }

                last = null;
            }
            else
            {
                last = resp!;
                if (last.RevisionId == expectedRevision)
                {
                    return last;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"GetResourcePolicy never reflected revision {Py.Repr(expectedRevision)} within {timeoutS}s; last saw {Py.Repr(Boto(last))}");
    }

    private static Dictionary<string, object?>? Boto(GetResourcePolicyResponse? response) =>
        response is null ? null : new Dictionary<string, object?> { ["Policy"] = response.Policy, ["RevisionId"] = response.RevisionId };

    /// <summary>
    /// PutResourcePolicy with an ExpectedRevisionId that is not the current revision raises PolicyNotFoundException with
    /// the exact message and leaves the policy unchanged; the same update with the current revision applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.resource-policy-revision-mismatch")]
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
        var cleanupDeadline = new Dictionary<string, FidelityStopwatch>(StringComparer.Ordinal);
        ctx.OnCleanup(() => RetryInUse(() => ddb.DeleteTableAsync(t), cleanupDeadline));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        var accountId = arn.Split(':')[4];
        var policyV1 =
            "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"," +
            $"\"Principal\":{{\"AWS\":\"arn:aws:iam::{accountId}:root\"}}," +
            "\"Action\":\"dynamodb:DescribeTable\",\"Resource\":\"" + arn + "\"}]}";

        // Positive control: the initial PutResourcePolicy (no ExpectedRevisionId yet exists) succeeds.
        var put1 = await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = arn, Policy = policyV1 });
        var realRevision = put1.RevisionId;
        ctx.OnCleanup(() => RetryInUse(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn }), cleanupDeadline));

        // Break exactly one thing: submit a well-formed but never-valid ExpectedRevisionId.
        var policyV2 = policyV1.Replace("DescribeTable", "GetItem", StringComparison.Ordinal);
        var (code, message) = await ErrorCode(() => ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest
        {
            ResourceArn = arn,
            Policy = policyV2,
            ExpectedRevisionId = "bogus-revision-id-xyz",
        }));
        Ensure(code == ErrPolicyNotFound, $"expected {ErrPolicyNotFound}, got {code}");
        var expected =
            "Resource-based policy not found for the provided ResourceArn: Requested update for policy with " +
            $"revision id bogus-revision-id-xyz, but the policy associated to target table {t} has revision " +
            $"id {realRevision}.";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Effect absent, via an independent read: the rejected update must not have applied. (Bounded
        // retry: PutResourcePolicy's own effect can itself still be propagating to GetResourcePolicy.)
        var unchanged = await PollForRevision(ddb, arn, realRevision);
        Ensure(unchanged.Policy.Contains("DescribeTable", StringComparison.Ordinal), "rejected mismatched update must not apply");

        // Restore: the identical update, resubmitted with the CURRENT (correct) revision, succeeds.
        var put2 = await ddb.PutResourcePolicyAsync(new PutResourcePolicyRequest { ResourceArn = arn, Policy = policyV2, ExpectedRevisionId = realRevision });
        Ensure(put2.RevisionId != realRevision, "a successful PutResourcePolicy must advance the RevisionId");

        // Terminal read, independent of the mutating call above: the new policy is what is now stored.
        var applied = await PollForRevision(ddb, arn, put2.RevisionId);
        Ensure(
            applied.Policy.Contains("GetItem", StringComparison.Ordinal) && !applied.Policy.Contains("DescribeTable", StringComparison.Ordinal),
            Py.Repr(Boto(applied)));
    }
}
