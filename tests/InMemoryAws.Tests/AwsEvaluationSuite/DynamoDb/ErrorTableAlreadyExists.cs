// Translated from behavioral/dynamodb/check_error_table_already_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.error-table-already-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorTableAlreadyExists
{
    private const string ErrResourceInUse = "ResourceInUseException";
    private const string ErrResourceNotFound = "ResourceNotFoundException";

    /// <summary>
    /// CreateTable with an existing name raises ResourceInUseException with the exact message and leaves the table
    /// ACTIVE with its TableId; once that table is deleted, the name creates a new table with a new TableId.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.error-table-already-exists")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");

        // Positive control: the first create succeeds and the table becomes ACTIVE.
        await ddb.CreateTableAsync(KeySchema(table));
        ctx.OnCleanup(() => SafeDelete(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var originalId = (await ddb.DescribeTableAsync(table)).Table.TableId;

        // Break exactly one thing: create the SAME name again while it still exists.
        var (code, message) = await ClientError(() => ddb.CreateTableAsync(KeySchema(table)));
        Ensure(code == ErrResourceInUse, $"expected {ErrResourceInUse}, got {Py.Repr(code)}");
        Ensure(message == $"Table already exists: {table}", $"expected the exact ResourceInUseException message AWS returns, got {Py.Repr(message)}");

        // Effect absent, via an independent read: the rejected attempt did not replace or
        // otherwise disturb the existing table: TableId must be unchanged.
        var afterReject = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            afterReject.TableId == originalId,
            $"existing table was disturbed by the rejected duplicate create: expected TableId {Py.Repr(originalId)}, got {Py.Repr(afterReject.TableId)}");
        Ensure(afterReject.TableStatus == TableStatus.ACTIVE, $"existing table left ACTIVE, got {Py.Repr(afterReject.TableStatus)}");

        // Restore: delete the table for real, and prove the name is gone via an independent read.
        await ddb.DeleteTableAsync(table);
        await WaitGone(ddb, table);

        // The identical call now succeeds: the conflict tracked live existence, not the name.
        await ddb.CreateTableAsync(KeySchema(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var recreatedId = (await ddb.DescribeTableAsync(table)).Table.TableId;
        Ensure(recreatedId != originalId, $"recreated table kept the deleted table's identity ({Py.Repr(recreatedId)})");
    }

    private static CreateTableRequest KeySchema(string table) => new()
    {
        TableName = table,
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        BillingMode = BillingMode.PAY_PER_REQUEST,
    };

    /// <summary>Call fn(); return its botocore Error Code and Message. An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task WaitGone(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (error is not null)
            {
                if (error.Code == ErrResourceNotFound)
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {Py.Repr(table)} never became fully deleted");
    }

    private static async Task SafeDelete(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != ErrResourceNotFound)
        {
            throw error.Exception;
        }
    }
}
