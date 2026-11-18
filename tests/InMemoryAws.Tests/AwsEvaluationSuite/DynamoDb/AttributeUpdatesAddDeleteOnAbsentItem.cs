// Translated from behavioral/dynamodb/check_attribute_updates_add_delete_on_absent_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-updates-add-delete-on-absent-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeUpdatesAddDeleteOnAbsentItem
{
    /// <summary>
    /// Legacy AttributeUpdates ADD against a wholly absent item starts the attribute from zero
    /// and creates the item with only the key and that attribute; DELETE, with or without a Value,
    /// against a wholly absent item is a no-op that never creates the item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-updates-add-delete-on-absent-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-attrupdabsent-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var addKey = Item(("pk", S("absent-add")), ("sk", S("s1")));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = addKey,
            AttributeUpdates = new() { ["counter"] = new AttributeValueUpdate { Action = AttributeAction.ADD, Value = N("7") } },
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("counter", N("7")))),
            $"ADD counter +7 against an absent item with UPDATED_NEW returned {Py.Repr(resp.Attributes)}, " +
            "expected it to start from zero");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = addKey, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("absent-add")), ("sk", S("s1")), ("counter", N("7")))),
            $"the item ADD created is {Py.Repr(item)}, expected only the key plus counter=7");

        var deleteKey = Item(("pk", S("absent-delete")), ("sk", S("s1")));
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = deleteKey,
            AttributeUpdates = new() { ["ghost"] = new AttributeValueUpdate { Action = AttributeAction.DELETE } },
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            resp.Attributes is null,
            $"whole-attribute DELETE against an absent item with UPDATED_OLD returned {Py.Repr(resp.Attributes)}");
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = deleteKey, ConsistentRead = true })).Item;
        Ensure(item is null, $"whole-attribute DELETE against an absent item created {Py.Repr(item)}; expected no item");

        var deleteSetKey = Item(("pk", S("absent-delete-set")), ("sk", S("s1")));
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = deleteSetKey,
            AttributeUpdates = new() { ["tags"] = new AttributeValueUpdate { Action = AttributeAction.DELETE, Value = SS("x") } },
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            resp.Attributes is null,
            $"set-member DELETE against an absent item with UPDATED_NEW returned {Py.Repr(resp.Attributes)}");
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = deleteSetKey, ConsistentRead = true })).Item;
        Ensure(item is null, $"set-member DELETE against an absent item created {Py.Repr(item)}; expected no item");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }
}
