// Translated from behavioral/dynamodb/check_delete_table_twice_rejected_not_silently_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-table-twice-rejected-not-silently-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteTableTwiceRejectedNotSilentlySucceeds
{
    private const string Check = "delete-table-twice-rejected-not-silently-succeeds";

    /// <summary>
    /// A second DeleteTable call issued after the table has fully disappeared
    /// returns ResourceNotFoundException, rather than succeeding silently a
    /// second time or returning some other generic error.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-table-twice-rejected-not-silently-succeeds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        CreateTableRequest TableKwargs() => new()
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        };
        await ddb.CreateTableAsync(TableKwargs());
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        // First delete: must succeed.
        await ddb.DeleteTableAsync(tableName);
        await WaitTableGone(ddb, tableName);

        // Second delete on the now fully-deleted table: must be rejected, not a
        // silent no-op success and not some unrelated error.
        var (code, message) = await Error(async () => await ddb.DeleteTableAsync(tableName));
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException on second DeleteTable, got {Py.Repr(code)}");
        var expectedMessage = $"Requested resource not found: Table: {tableName} not found";
        Ensure(message == expectedMessage, $"expected the second DeleteTable message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // DescribeTable on the same name must agree the table is gone.
        var (describeCode, describeMessage) = await Error(async () => await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }));
        Ensure(describeCode == "ResourceNotFoundException", $"expected ResourceNotFoundException on DescribeTable of a deleted table, got {Py.Repr(describeCode)}");
        var expectedDescribe = $"Requested resource not found: Table: {tableName} not found";
        Ensure(describeMessage == expectedDescribe, $"expected the DescribeTable message {Py.Repr(expectedDescribe)}, got {Py.Repr(describeMessage)}");

        // The name is now free: recreating with the identical name must succeed and
        // become usable, proving the deletion actually released the identifier
        // rather than leaving the target in a phantom not-found-but-still-reserved
        // state.
        await ddb.CreateTableAsync(TableKwargs());
        var recreated = await WaitTableActive(ddb, tableName);
        Ensure(recreated.TableName == tableName, $"expected recreated table name {Py.Repr(tableName)}, got {Py.Repr(recreated.TableName)}");
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task WaitTableGone(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became fully deleted");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
