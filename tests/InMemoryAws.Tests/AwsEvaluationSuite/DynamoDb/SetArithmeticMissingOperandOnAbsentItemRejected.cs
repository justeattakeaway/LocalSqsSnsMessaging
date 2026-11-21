// Translated from behavioral/dynamodb/check_set_arithmetic_missing_operand_on_absent_item_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.set-arithmetic-missing-operand-on-absent-item-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetArithmeticMissingOperandOnAbsentItemRejected
{
    private const string Check = "set-arithmetic-missing-operand-on-absent-item-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;
    private const string MissingAttr = "The provided expression refers to an attribute that does not exist in the item";

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}");
        }

        return (err.Code, err.Message);
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

            if (error.Code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitSeconds)
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetSeconds)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetSeconds)}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// `SET c = c + :one` against a key that holds no item raises ValidationException with the
    /// exact missing-attribute message and upserts nothing, while the if_not_exists form creates it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.set-arithmetic-missing-operand-on-absent-item-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("ghost-row")));

        var pre = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(pre is null, $"the key already holds {Py.Repr(pre)} before any write");

        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET c = c + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"SET c = c + :one against an absent item answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == MissingAttr, $"the refusal's message is {Py.Repr(message)}; expected {Py.Repr(MissingAttr)}");

        var afterRefusal = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            afterRefusal is null,
            $"after the refused SET on an absent item, GetItem returned {Py.Repr(afterRefusal)}; " +
            "a refused UpdateItem must not upsert the key");

        // Positive control: the guarded form upserts the key and initialises c.
        var created = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET c = if_not_exists(c, :zero) + :one",
            ExpressionAttributeValues = Item((":one", N("1")), (":zero", N("0"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(created, Item(("c", N("1")))),
            $"if_not_exists(c, :zero) + :one on an absent item returned {Py.Repr(created)}, expected c=1");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored, Item(("pk", S("ghost-row")), ("c", N("1")))),
            $"after the upsert the item is {Py.Repr(stored)}, expected {{**key, 'c': 1}}");

        // ...and the previously refused expression now succeeds, since c exists.
        var second = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET c = c + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(Py.Equal(second, Item(("c", N("2")))), $"SET c = c + :one with c=1 stored returned {Py.Repr(second)}, expected c=2");
    }
}
