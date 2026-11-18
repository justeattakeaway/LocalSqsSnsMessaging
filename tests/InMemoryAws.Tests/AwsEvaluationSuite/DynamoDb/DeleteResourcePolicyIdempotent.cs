// Translated from behavioral/dynamodb/check_delete_resource_policy_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-resource-policy-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteResourcePolicyIdempotent
{
    private const string Check = "delete-resource-policy-idempotent";
    private const string ErrPolicyNotFound = "PolicyNotFoundException";

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
    /// DeleteResourcePolicy on a table without a policy succeeds with an empty RevisionId and attaches nothing,
    /// while the same call with an ExpectedRevisionId raises PolicyNotFoundException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-resource-policy-idempotent")]
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
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var arn = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table.TableArn;

        // Baseline: no policy has ever been attached.
        var (baselineCode, baselineMsg) = await Error(async () => await ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
        Ensure(baselineCode == ErrPolicyNotFound, baselineCode ?? "None");
        var expectedGet = $"Resource-based policy not found for the provided ResourceArn: {arn}";
        Ensure(baselineMsg == expectedGet, $"expected message {Py.Repr(expectedGet)}, got {Py.Repr(baselineMsg)}");

        // Positive control: the claim this check is named for is that a BARE delete against a
        // policy-less resource is idempotent -- it must succeed as a no-op, not raise. Without this,
        // the ExpectedRevisionId negative below cannot be told apart from "DeleteResourcePolicy always
        // rejects when there's no policy", which would falsify the idempotency the check's name asserts.
        var resp = await ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest { ResourceArn = arn });
        Ensure((resp.RevisionId ?? "") == "", $"expected a no-op with an empty RevisionId, got {Py.Repr(resp.RevisionId)}");

        // The no-op must not have created a policy as a side effect.
        var (afterNoopCode, afterNoopMsg) = await Error(async () => await ddb.GetResourcePolicyAsync(new GetResourcePolicyRequest { ResourceArn = arn }));
        Ensure(afterNoopCode == ErrPolicyNotFound, afterNoopCode ?? "None");
        Ensure(afterNoopMsg == expectedGet, $"expected message {Py.Repr(expectedGet)}, got {Py.Repr(afterNoopMsg)}");

        // An ExpectedRevisionId can never match a nonexistent policy.
        var (code, message) = await Error(async () => await ddb.DeleteResourcePolicyAsync(new DeleteResourcePolicyRequest
        {
            ResourceArn = arn,
            ExpectedRevisionId = "bogus-revision-id-0001",
        }));
        Ensure(code == ErrPolicyNotFound, $"expected {ErrPolicyNotFound}, got {code ?? "None"}");
        var expectedMsg =
            "Resource-based policy not found for the provided ResourceArn: Requested update for " +
            $"policy with revision id bogus-revision-id-0001, but table {t} has no associated policy.";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");
    }
}
