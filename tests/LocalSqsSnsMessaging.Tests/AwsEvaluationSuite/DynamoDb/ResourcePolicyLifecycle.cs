// Translated from behavioral/dynamodb/check_resource_policy_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.resource-policy-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResourcePolicyLifecycle
{
    /// <summary>
    /// Each PutResourcePolicy returns a new RevisionId that GetResourcePolicy reports with the latest document, and
    /// after DeleteResourcePolicy, GetResourcePolicy raises PolicyNotFoundException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.resource-policy-lifecycle")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        var accountId = arn.Split(':')[4];
        var policyDocV1 =
            "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"," +
            $"\"Principal\":{{\"AWS\":\"arn:aws:iam::{accountId}:root\"}}," +
            "\"Action\":\"dynamodb:DescribeTable\",\"Resource\":\"" + arn + "\"}]}";
        var put1 = await PutPolicyWithRetry(ddb, arn, policyDocV1);
        var rev1 = put1.RevisionId;
        Ensure(!string.IsNullOrEmpty(rev1), $"PutResourcePolicy returned no RevisionId: {Py.Repr(Boto(put1.RevisionId, null))}");
        var got1 = await GetPolicyContaining(ddb, arn, "DescribeTable");
        Ensure(got1.RevisionId == rev1, Py.Repr(Boto(got1.RevisionId, got1.Policy)));
        Ensure(got1.Policy.Contains("DescribeTable", StringComparison.Ordinal), Py.Repr(Boto(got1.RevisionId, got1.Policy)));
        var policyDocV2 = policyDocV1.Replace("DescribeTable", "GetItem", StringComparison.Ordinal);
        var put2 = await PutPolicyWithRetry(ddb, arn, policyDocV2, expectedRevisionId: rev1);
        var rev2 = put2.RevisionId;
        Ensure(rev2 != rev1, "a successful policy update must yield a new RevisionId");
        var got2 = await GetPolicyContaining(ddb, arn, "GetItem");
        Ensure(got2.Policy.Contains("GetItem", StringComparison.Ordinal), Py.Repr(Boto(got2.RevisionId, got2.Policy)));
        await DeletePolicyWithRetry(ddb, arn, rev2);
        var (code, message) = await PollUntil(
            () => GetPolicyOutcome(ddb, arn),
            r => r.Code == "PolicyNotFoundException",
            timeoutS: 60,
            intervalS: 3);
        Ensure(code == "PolicyNotFoundException", $"expected PolicyNotFoundException, got {code}");
        var expected = $"Resource-based policy not found for the provided ResourceArn: {arn}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");
    }

    private static Dictionary<string, object?> Boto(string? revisionId, string? policy)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (policy is not null)
        {
            output["Policy"] = policy;
        }

        if (revisionId is not null)
        {
            output["RevisionId"] = revisionId;
        }

        return output;
    }

    // DynamoDB's resource-policy control-plane surface enforces a tight per-resource request
    // rate; back-to-back Put/Get/Delete ResourcePolicy calls against a just-created table can
    // transiently 429 (ThrottlingException) or report the resource as still settling
    // (ResourceInUseException) -- retry for a bounded time rather than treating either as a hard
    // failure.
    private static async Task<PutResourcePolicyResponse> PutPolicyWithRetry(
        IAmazonDynamoDB ddb, string arn, string policy, string? expectedRevisionId = null, double timeoutS = 90, double intervalS = 5)
    {
        var request = new PutResourcePolicyRequest { ResourceArn = arn, Policy = policy };
        if (expectedRevisionId is not null)
        {
            request.ExpectedRevisionId = expectedRevisionId;
        }

        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.PutResourcePolicyAsync(request));
            if (error is null)
            {
                return resp!;
            }

            if (error.Code is "ThrottlingException" or "ResourceInUseException" && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task DeletePolicyWithRetry(IAmazonDynamoDB ddb, string arn, string expectedRevisionId, double timeoutS = 120, double intervalS = 10)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest
            {
                ResourceArn = arn,
                ExpectedRevisionId = expectedRevisionId,
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

    /// <summary>(code, message) of GetResourcePolicy's refusal, or ('still-present', None) while it succeeds.</summary>
    private static async Task<(string? Code, string? Message)> GetPolicyOutcome(IAmazonDynamoDB ddb, string arn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
        return error is null ? ("still-present", null) : (error.Code, error.Message);
    }

    // PutResourcePolicy's effect can lag briefly; a GetResourcePolicy issued immediately after
    // may still 404 (PolicyNotFoundException) or briefly return the PREVIOUS statement -- poll
    // until the expected content is actually visible.
    private static async Task<GetResourcePolicyResponse> GetPolicyContaining(
        IAmazonDynamoDB ddb, string arn, string needle, double timeoutS = 60, double intervalS = 3)
    {
        async Task<GetResourcePolicyResponse?> Attempt()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
            if (error is not null)
            {
                if (error.Code is "ThrottlingException" or "PolicyNotFoundException")
                {
                    return null;
                }

                throw error.Exception;
            }

            return resp!.Policy.Contains(needle, StringComparison.Ordinal) ? resp : null;
        }

        return (await PollUntil(Attempt, r => r is not null, timeoutS, intervalS))!;
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
