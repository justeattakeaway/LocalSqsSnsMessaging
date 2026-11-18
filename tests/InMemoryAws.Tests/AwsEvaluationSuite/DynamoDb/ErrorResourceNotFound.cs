// Translated from behavioral/dynamodb/check_error_resource_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.error-resource-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorResourceNotFound
{
    private const string CheckId = "error-resource-not-found";
    private const string ErrResourceNotFound = "ResourceNotFoundException";

    /// <summary>
    /// DescribeTable on a table that was never created raises ResourceNotFoundException, while
    /// the identical call against a real table in the same account succeeds — proving the rejection
    /// is specific to the missing table, not a blanket DescribeTable failure.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.error-resource-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var realName = FidelityContext.UniqueName("sim2real-behav-rnf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(realName));
        await ddb.WaitForTableExists(realName, delaySeconds: 2, maxAttempts: 250);

        var control = (await ddb.DescribeTableAsync(realName)).Table;
        Ensure(
            control.TableName == realName,
            $"positive control failed: expected DescribeTable({Py.Repr(realName)}) to succeed, got TableName={Py.Repr(control.TableName)}");

        var ghost = $"{realName}-ghost";
        var (code, message) = await ErrorCode(() => ddb.DescribeTableAsync(ghost));
        Ensure(
            code == ErrResourceNotFound,
            $"expected {ErrResourceNotFound} for DescribeTable on a table that was never created, got {code ?? "None"}");
        Ensure(
            message == $"Requested resource not found: Table: {ghost} not found",
            $"expected the exact ResourceNotFoundException message AWS returns, got {Py.Repr(message)}");
    }

    /// <summary>
    /// Call fn(); return its botocore error code and message. An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING a lookup real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects DescribeTable on a table that was never created, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}
