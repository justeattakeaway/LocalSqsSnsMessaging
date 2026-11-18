// Translated from behavioral/dynamodb/check_empty_string_expression_rejected_across_operations.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.empty-string-expression-rejected-across-operations</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyStringExpressionRejectedAcrossOperations
{
    private const string CheckId = "empty-string-expression-rejected-across-operations";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;

    /// <summary>
    /// An empty-string ConditionExpression, UpdateExpression, KeyConditionExpression,
    /// FilterExpression or ProjectionExpression is a ValidationException on every operation that
    /// accepts it, and a refused PutItem, DeleteItem or UpdateItem leaves the item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.empty-string-expression-rejected-across-operations")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("item-1")));
        static Dictionary<string, AttributeValue> WithColor(string color) => Item(("pk", S("item-1")), ("color", S(color)));
        await ddb.PutItemAsync(table, WithColor("red"));

        // 1. PutItem with an empty ConditionExpression.
        var (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = WithColor("blue"), ConditionExpression = "" }));
        Ensure(code == "ValidationException", $"PutItem with ConditionExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        var expected = "1 validation error detected: Invalid ConditionExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, WithColor("red")), $"after the refused PutItem the item is {Py.Repr(item)}; expected color still 'red'");

        // 2. DeleteItem with an empty ConditionExpression.
        (code, message) = await Error(() => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ConditionExpression = "" }));
        Ensure(code == "ValidationException", $"DeleteItem with ConditionExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected = "1 validation error detected: Invalid ConditionExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, WithColor("red")), $"after the refused DeleteItem the item is {Py.Repr(item)}; expected it still present and unchanged");

        // 3. UpdateItem with an empty UpdateExpression.
        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = Key(), UpdateExpression = "" }));
        Ensure(code == "ValidationException", $"UpdateItem with UpdateExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected = "1 validation error detected: Invalid UpdateExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, WithColor("red")), $"after the refused UpdateItem the item is {Py.Repr(item)}; expected color still 'red'");

        // 4. Query with an empty KeyConditionExpression.
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditionExpression = "" }));
        Ensure(code == "ValidationException", $"Query with KeyConditionExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected = "Invalid KeyConditionExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // 5. Scan with an empty FilterExpression.
        (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "" }));
        Ensure(code == "ValidationException", $"Scan with FilterExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected = "Invalid FilterExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // 6. GetItem with an empty ProjectionExpression.
        (code, message) = await Error(() => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "" }));
        Ensure(code == "ValidationException", $"GetItem with ProjectionExpression='' answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected = "Invalid ProjectionExpression: The expression can not be empty;";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Positive control: the same five shapes, with a real expression, all succeed.
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = WithColor("green"), ConditionExpression = "attribute_exists(pk)" });
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #c = :v",
            ExpressionAttributeNames = Names(("#c", "color")),
            ExpressionAttributeValues = Item((":v", S("yellow"))),
        });
        var rows = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("item-1"))),
        })).Items;
        Ensure(Py.Equal(rows, new[] { WithColor("yellow") }), $"the positive-control Query returned {Py.Repr(rows)}, expected the item colored yellow");
        rows = (await ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "attribute_exists(pk)" })).Items;
        Ensure(Py.Equal(rows, new[] { WithColor("yellow") }), $"the positive-control Scan returned {Py.Repr(rows)}, expected the item colored yellow");
        var projected = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "pk" })).Item;
        Ensure(Py.Equal(projected, Key()), $"the positive-control GetItem projection returned {Py.Repr(projected)}, expected only pk");
    }

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr(resp)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code == "ResourceInUseException" && clock.Elapsed < TimeSpan.FromSeconds(TeardownWaitSeconds))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(TableActiveBudgetSeconds))
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {TableActiveBudgetSeconds}s: {Py.Repr(status)}");
    }
}
