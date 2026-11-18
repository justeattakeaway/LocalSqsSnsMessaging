// Translated from behavioral/dynamodb/check_create_table_invalid_key_schema_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-invalid-key-schema-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableInvalidKeySchemaFails
{
    /// <summary>
    /// CreateTable fails with ValidationException and the exact message when a key attribute has
    /// no AttributeDefinitions entry, creating no table; the same table with both keys defined succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-invalid-key-schema-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var controlName = FidelityContext.UniqueName("sim2real-behav-ddb-badkey-control-");
        ctx.OnCleanup(() => ddb.DeleteTableAsync(controlName));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = controlName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await ddb.WaitForTableExists(controlName, delaySeconds: 2, maxAttempts: 250);
        var controlNames = await TableNames(ddb);
        Ensure(
            controlNames.Contains(controlName),
            $"positive control: expected {controlName} to exist after a valid composite-key CreateTable, absent from the {controlNames.Count} listed tables");

        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-badkey-");
        // Registered up front so an unexpectedly-accepted CreateTable cannot leak a table.
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, tableName));

        Task<CreateTableResponse> BadCreate() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            // "sk" is referenced by KeySchema but missing from AttributeDefinitions.
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });

        var (code, message) = await Error(BadCreate);
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        const string expectedMsg = "Invalid KeySchema: Some index key attribute have no definition";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");
        var names = await TableNames(ddb);
        Ensure(!names.Contains(tableName), $"rejected CreateTable must not create {tableName}, yet ListTables lists it");
    }

    /// <summary>
    /// Call fn() and return the (Code, Message) of the ClientError it raises; an accepted call
    /// is itself a failure.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call succeeded: {result}");
    }

    /// <summary>Every table name in the region, read through all ListTables pages.</summary>
    private static async Task<List<string>> TableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            names.AddRange(page.TableNames);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);

        return names;
    }

    /// <summary>Cleanup for a table the check expects never to exist: delete it if a target created it.</summary>
    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
