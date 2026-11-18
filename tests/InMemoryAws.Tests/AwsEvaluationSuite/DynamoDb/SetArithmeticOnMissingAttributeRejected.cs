// Translated from behavioral/dynamodb/check_set_arithmetic_on_missing_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.set-arithmetic-on-missing-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetArithmeticOnMissingAttributeRejected
{
    private const string CheckId = "set-arithmetic-on-missing-attribute-rejected";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr((resp as UpdateItemResponse)?.Attributes)}");
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

            if (error.Code == "ResourceInUseException" && clock.Elapsed < TimeSpan.FromSeconds(TeardownWaitS))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw new InvalidOperationException($"dynamodb table {Py.Repr(tableName)} could not be deleted: {error.Code}", error.Exception);
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
        while (clock.Elapsed < TimeSpan.FromSeconds(TableActiveBudgetS))
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetS)}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// `SET c = c + :one` on an item without `c` raises ValidationException with the exact message and leaves the item
    /// unchanged, while `SET c = if_not_exists(c, :zero) + :one` sets c to 1 and then to 2.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.set-arithmetic-on-missing-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("counter")));
        await ddb.PutItemAsync(table, Item(("pk", S("counter")), ("label", S("orig"))));

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET c = c + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"SET c = c + :one with no stored c answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(
            message == "The provided expression refers to an attribute that does not exist in the item",
            $"the refusal's message is {Py.Repr(message)}; real AWS names the missing operand as an attribute that does not exist in the item");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("counter")), ("label", S("orig")))),
            $"after the refused SET the item is {Py.Repr(item)}; a refused UpdateItem must change nothing (a target that treats the missing operand as 0 writes c=1 here)");

        // Positive control: the guarded form initialises the attribute...
        var first = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET c = if_not_exists(c, :zero) + :one",
            ExpressionAttributeValues = Item((":one", N("1")), (":zero", N("0"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(Py.Equal(first, Item(("c", N("1")))), $"if_not_exists initialised c to {Py.Repr(first)}, expected 1");

        // ...and the refused expression succeeds once c exists.
        var second = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET c = c + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(second, Item(("c", N("2")))),
            $"SET c = c + :one with c=1 stored returned {Py.Repr(second)}, expected c=2; the refusal above must have been about the missing operand, not the expression");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("counter")), ("label", S("orig")), ("c", N("2")))),
            $"final item is {Py.Repr(final)}, expected label intact and c=2");
    }
}
