// Translated from behavioral/dynamodb/check_update_item_set_value_first_operand_order_same_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-value-first-operand-order-same-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetValueFirstOperandOrderSameAttribute
{
    private const string Check = "update-item-set-value-first-operand-order-same-attribute";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;

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
    /// SET n = :value + n (value-first operand order) adds against the currently stored n,
    /// leaving a sibling attribute untouched, and compounds over successive updates.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-value-first-operand-order-same-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("n", N("10")), ("label", S("keep"))));

        var first = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = :inc + n",
            ExpressionAttributeValues = Item((":inc", N("5"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(first, Item(("n", N("15")))),
            $"SET n = :inc + n with n=10, :inc=5 returned UPDATED_NEW {Py.Repr(first)}, expected only n=15");
        var item1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item1, Item(("pk", S("item")), ("n", N("15")), ("label", S("keep")))),
            $"after SET n = :inc + n the item is {Py.Repr(item1)}, expected n=15 and label untouched");

        // A second value-first update against the now-stored n=15, never the original n=10.
        var second = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = :inc + n",
            ExpressionAttributeValues = Item((":inc", N("7"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(second, Item(("pk", S("item")), ("n", N("22")), ("label", S("keep")))),
            $"SET n = :inc + n with the stored n=15, :inc=7 returned ALL_NEW {Py.Repr(second)}, " +
            "expected n=22 (not 17, which would mean it ignored the first update)");
        var item2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(item2, second), $"after the second SET n = :inc + n the item is {Py.Repr(item2)}, expected {Py.Repr(second)}");
    }
}
