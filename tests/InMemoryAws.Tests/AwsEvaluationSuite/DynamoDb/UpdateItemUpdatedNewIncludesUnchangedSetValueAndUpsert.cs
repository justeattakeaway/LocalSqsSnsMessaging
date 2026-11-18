// Translated from behavioral/dynamodb/check_update_item_updated_new_includes_unchanged_set_value_and_upsert.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-updated-new-includes-unchanged-set-value-and-upsert</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemUpdatedNewIncludesUnchangedSetValueAndUpsert
{
    /// <summary>
    /// UpdateItem ReturnValues=UPDATED_NEW lists every SET attribute, including one re-assigned
    /// its own current value, and an upserting UpdateItem's UPDATED_NEW carries the new attributes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-updated-new-includes-unchanged-set-value-and-upsert")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-updnew-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("existing")));
        await ddb.PutItemAsync(table, Item(("pk", S("existing")), ("dat", S("kept")), ("cnt", N("1")), ("untouched", S("stay"))));

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #d = :dat, #c = :cnt",
            ExpressionAttributeNames = Names(("#d", "dat"), ("#c", "cnt")),
            ExpressionAttributeValues = Item((":dat", S("kept")), (":cnt", N("2"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        var attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("dat", S("kept")), ("cnt", N("2")))),
            $"UPDATED_NEW must list every SET attribute, the unchanged one included, got {Py.Repr(attrs)}");
        Ensure(!attrs.ContainsKey("untouched"), $"UPDATED_NEW must not echo an attribute SET did not name, got {Py.Repr(Sorted(attrs))}");

        var stored = await Get(ddb, table, key);
        Ensure(
            Py.Equal(stored, Item(("pk", S("existing")), ("dat", S("kept")), ("cnt", N("2")), ("untouched", S("stay")))),
            $"independent GetItem after the update must show the stored item, got {Py.Repr(stored)}");

        var upsertKey = Item(("pk", S("brand-new")));
        Ensure(await Get(ddb, table, upsertKey) is null, "the upsert target already holds an item before the update");
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = upsertKey,
            UpdateExpression = "SET #cr = :c, #ct = :n",
            ExpressionAttributeNames = Names(("#cr", "created"), ("#ct", "count")),
            ExpressionAttributeValues = Item((":c", S("first")), (":n", N("0"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("created", S("first")), ("count", N("0")))),
            $"an upserting UpdateItem's UPDATED_NEW must carry the new attributes, got {Py.Repr(attrs)}");
        Ensure(!attrs.ContainsKey("pk"), $"UPDATED_NEW must not echo the key on an upsert, got {Py.Repr(Sorted(attrs))}");

        var created = await Get(ddb, table, upsertKey);
        Ensure(
            Py.Equal(created, Item(("pk", S("brand-new")), ("created", S("first")), ("count", N("0")))),
            $"independent GetItem after the upsert must show the created item, got {Py.Repr(created)}");
    }

    private static List<string> Sorted(Dictionary<string, AttributeValue> item) => item.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonDynamoDBException ex) when (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
        {
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
