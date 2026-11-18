// Translated from behavioral/dynamodb/check_unused_expression_attribute_value_and_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.unused-expression-attribute-value-and-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnusedExpressionAttributeValueAndNameRejected
{
    private const string Check = "unused-expression-attribute-value-and-name-rejected";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            return (err.Code, err.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}");
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null)
            {
                return;
            }

            var code = error.Code;
            if (code == "ResourceNotFoundException")
            {
                return;
            }

            if (code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw new InvalidOperationException($"dynamodb table {Py.Repr(tableName)} could not be deleted: {code}", error.Exception);
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        TableStatus? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetS)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetS)}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// An UpdateItem with an unused ExpressionAttributeValues or ExpressionAttributeNames entry raises
    /// ValidationException with the exact message and writes nothing, while the same update without it sets n to 6.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.unused-expression-attribute-value-and-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("strict")));
        await ddb.PutItemAsync(table, Item(("pk", S("strict")), ("n", N("5"))));

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = :v",
            ExpressionAttributeValues = Item((":v", N("6")), (":extra", N("7"))),
        }));
        Ensure(
            code == "ValidationException",
            $"an unused ExpressionAttributeValues key answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        var expected =
            "1 validation error detected: Value provided in ExpressionAttributeValues unused in "
            + "expressions: keys: {:extra}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = :v",
            ExpressionAttributeNames = Names(("#unused", "zzz")),
            ExpressionAttributeValues = Item((":v", N("6"))),
        }));
        Ensure(
            code == "ValidationException",
            $"an unused ExpressionAttributeNames key answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        expected =
            "1 validation error detected: Value provided in ExpressionAttributeNames unused in "
            + "expressions: keys: {#unused}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("strict")), ("n", N("5")))),
            $"after two refused updates the item is {Py.Repr(item)}; expected n still 5 -- a refused request must not apply its (otherwise valid) SET");

        // Positive control: the same update, minus the stray entries.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = :v",
            ExpressionAttributeValues = Item((":v", N("6"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(Py.Equal(resp.Attributes, Item(("n", N("6")))), $"the clean update returned {Py.Repr(resp.Attributes)}, expected n=6");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(final, Item(("pk", S("strict")), ("n", N("6")))), $"final item {Py.Repr(final)}, expected n=6");
    }
}
