// Translated from behavioral/dynamodb/check_expression_attribute_placeholders_without_an_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.expression-attribute-placeholders-without-an-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExpressionAttributePlaceholdersWithoutAnExpressionRejected
{
    private const string CheckId = "expression-attribute-placeholders-without-an-expression-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;

    private const string NoExpr = "ExpressionAttributeValues can only be specified when using expressions";
    private const string NoExpr1 = "1 validation error detected: " + NoExpr;
    private const string EanNoExpr = "ExpressionAttributeNames can only be specified when using expressions";
    private const string EanNoExpr1 = "1 validation error detected: " + EanNoExpr;
    private const string EmptyEav = "ExpressionAttributeValues must not be empty";
    private const string EmptyEav1 = "1 validation error detected: " + EmptyEav;
    private const string EmptyEan = "ExpressionAttributeNames must not be empty";
    private const string EmptyEan1 = "1 validation error detected: " + EmptyEan;

    /// <summary>
    /// ExpressionAttributeValues or ExpressionAttributeNames with no expression to bind into,
    /// and an empty {} map of either alongside an expression that does use placeholders, is a
    /// ValidationException on PutItem, DeleteItem, GetItem and Scan, and leaves the table unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ExpressionAttributeValues.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.expression-attribute-placeholders-without-an-expression-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("item-1")));
        await ddb.PutItemAsync(table, Item(("pk", S("item-1")), ("color", S("red"))));

        // ExpressionAttributeValues/Names with no expression present.
        await AssertRejected(
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("new-eav"))), ExpressionAttributeValues = Item((":v", S("x"))) }),
            NoExpr1,
            "PutItem with EAV and no expression");
        await AssertRejected(
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("new-ean"))), ExpressionAttributeNames = Names(("#n", "n")) }),
            EanNoExpr1,
            "PutItem with EAN and no expression");
        await AssertRejected(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ExpressionAttributeValues = Item((":v", S("x"))) }),
            NoExpr1,
            "DeleteItem with EAV and no expression");
        await AssertRejected(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ExpressionAttributeNames = Names(("#n", "n")) }),
            EanNoExpr1,
            "DeleteItem with EAN and no expression");
        await AssertRejected(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ExpressionAttributeNames = Names(("#n", "n")) }),
            EanNoExpr,
            "GetItem with EAN and no expression");
        await AssertRejected(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, ExpressionAttributeValues = Item((":v", S("x"))) }),
            NoExpr,
            "Scan with EAV and no expression");
        await AssertRejected(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, ExpressionAttributeNames = Names(("#n", "n")) }),
            EanNoExpr,
            "Scan with EAN and no expression");

        // An empty {} map alongside a valid expression that does use placeholders.
        await AssertRejected(
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("new-emptyeav"))), ConditionExpression = "attribute_not_exists(pk)", ExpressionAttributeValues = Item() }),
            EmptyEav1,
            "PutItem with an empty EAV map and a valid ConditionExpression");
        await AssertRejected(
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("new-emptyean"))), ConditionExpression = "attribute_not_exists(pk)", ExpressionAttributeNames = Names() }),
            EmptyEan1,
            "PutItem with an empty EAN map and a valid ConditionExpression");
        await AssertRejected(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ConditionExpression = "attribute_exists(pk)", ExpressionAttributeValues = Item() }),
            EmptyEav1,
            "DeleteItem with an empty EAV map and a valid ConditionExpression");
        await AssertRejected(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "pk", ExpressionAttributeNames = Names() }),
            EmptyEan,
            "GetItem with an empty EAN map and a valid ProjectionExpression");
        await AssertRejected(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "attribute_exists(pk)", ExpressionAttributeValues = Item() }),
            EmptyEav,
            "Scan with an empty EAV map and a valid FilterExpression");
        await AssertRejected(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "attribute_exists(pk)", ExpressionAttributeNames = Names() }),
            EmptyEan,
            "Scan with an empty EAN map and a valid FilterExpression");

        // None of the twelve refused calls wrote anything: the table holds exactly the one item.
        var items = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(
            Py.Equal(items, new[] { Item(("pk", S("item-1")), ("color", S("red"))) }),
            $"table holds {Py.Repr(items)} after every refusal above; expected only the original item");

        // Positive control: the same shapes, used correctly, succeed.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("new-ok"))),
            ConditionExpression = "attribute_not_exists(#p)",
            ExpressionAttributeNames = Names(("#p", "pk")),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("new-ok"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, Item(("pk", S("new-ok")))), $"positive-control PutItem left {Py.Repr(got)}");
        await ddb.DeleteItemAsync(table, Item(("pk", S("new-ok"))));
    }

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("call unexpectedly SUCCEEDED where an error was required");
        }

        return (error.Code, error.Message);
    }

    private static async Task AssertRejected(Func<Task> call, string expectedMessage, string what)
    {
        var (code, message) = await Error(call);
        Ensure(code == "ValidationException", $"{what} answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == expectedMessage, $"{what}: expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
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
