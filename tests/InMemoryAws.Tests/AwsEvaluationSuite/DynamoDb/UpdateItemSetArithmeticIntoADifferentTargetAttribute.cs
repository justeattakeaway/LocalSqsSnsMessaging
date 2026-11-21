// Translated from behavioral/dynamodb/check_update_item_set_arithmetic_into_a_different_target_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-arithmetic-into-a-different-target-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetArithmeticIntoADifferentTargetAttribute
{
    private const string Check = "update-item-set-arithmetic-into-a-different-target-attribute";
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
    /// SET writes an arithmetic result into an attribute other than the one it reads, for
    /// `path + value`, `path - value` and `value + path`, leaving the source untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-arithmetic-into-a-different-target-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("price", N("20"))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Dictionary<string, AttributeValue> Expected(string derived) => Item(("pk", S("item")), ("price", N("20")), ("derived", N(derived)));

        // path + value, writing into a target attribute that does not exist yet.
        var first = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET derived = price + :tax",
            ExpressionAttributeValues = Item((":tax", N("5"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(first, Item(("derived", N("25")))),
            $"SET derived = price + :tax with price=20, :tax=5 returned UPDATED_NEW {Py.Repr(first)}, " +
            "expected only derived=25");
        var item1 = await Read();
        Ensure(
            Py.Equal(item1, Expected("25")),
            $"after SET derived = price + :tax the item is {Py.Repr(item1)}, expected price unchanged at 20 " +
            "and derived=25");

        // path - value, overwriting the target attribute; the source is still untouched.
        var second = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET derived = price - :discount",
            ExpressionAttributeValues = Item((":discount", N("3"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(second, Expected("17")),
            $"SET derived = price - :discount with price=20, :discount=3 returned ALL_NEW {Py.Repr(second)}, " +
            "expected price=20 and derived=17");
        var item2 = await Read();
        Ensure(Py.Equal(item2, second), $"after SET derived = price - :discount the item is {Py.Repr(item2)}, expected {Py.Repr(second)}");

        // value + path (value-first operand order), into the same different target attribute.
        var third = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET derived = :tax + price",
            ExpressionAttributeValues = Item((":tax", N("5"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(third, Expected("25")),
            $"SET derived = :tax + price with :tax=5, price=20 returned ALL_NEW {Py.Repr(third)}, " +
            "expected price=20 and derived=25");
        var item3 = await Read();
        Ensure(Py.Equal(item3, third), $"after SET derived = :tax + price the item is {Py.Repr(item3)}, expected {Py.Repr(third)}");
    }
}
