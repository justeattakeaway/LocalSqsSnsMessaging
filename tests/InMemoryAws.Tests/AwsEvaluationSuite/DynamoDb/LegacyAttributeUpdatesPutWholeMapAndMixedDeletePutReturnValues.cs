// Translated from behavioral/dynamodb/check_legacy_attribute_updates_put_whole_map_and_mixed_delete_put_return_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attribute-updates-put-whole-map-and-mixed-delete-put-return-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributeUpdatesPutWholeMapAndMixedDeletePutReturnValues
{
    /// <summary>
    /// Legacy AttributeUpdates PUT of a whole map with UPDATED_OLD returns the pre-update map, and
    /// a DELETE mixed with a PUT in one call carries UPDATED_NEW only the PUT's value.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attribute-updates-put-whole-map-and-mixed-delete-put-return-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-legacyrv-");
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

        Dictionary<string, AttributeValue> MapKey() => Item(("pk", S("mapitem")), ("sk", S("s1")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("mapitem")), ("sk", S("s1")),
            ("profile", M(("city", S("nyc")), ("note", S("old"))))));

        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = MapKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["profile"] = new() { Action = AttributeAction.PUT, Value = M(("city", S("sf"))) },
            },
            ReturnValues = ReturnValue.UPDATED_OLD,
        })).Attributes;
        var wantOld = Item(("profile", M(("city", S("nyc")), ("note", S("old")))));
        Ensure(
            Py.Equal(got, wantOld),
            $"legacy PUT of a whole map with UPDATED_OLD returned {Py.Repr(got)}, expected the pre-update map {Py.Repr(wantOld)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = MapKey(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item.GetValueOrDefault("profile"), M(("city", S("sf")))),
            $"after the PUT the stored map is {Py.Repr(item.GetValueOrDefault("profile"))}, expected it replaced wholesale with city=sf only");

        Dictionary<string, AttributeValue> MixKey() => Item(("pk", S("mixitem")), ("sk", S("s1")));
        await ddb.PutItemAsync(table, Item(("pk", S("mixitem")), ("sk", S("s1")), ("a", S("a0")), ("b", S("b0"))));

        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = MixKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["a"] = new() { Action = AttributeAction.DELETE },
                ["b"] = new() { Action = AttributeAction.PUT, Value = S("b1") },
            },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(got, Item(("b", S("b1")))),
            $"a DELETE mixed with a PUT in one call, with UPDATED_NEW, returned {Py.Repr(got)}; " +
            "expected only the PUT's new value for 'b', nothing for the deleted 'a'");
        var item2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = MixKey(), ConsistentRead = true })).Item;
        Ensure(!item2.ContainsKey("a"), $"'a' is still present after its whole-attribute DELETE: {Py.Repr(item2.GetValueOrDefault("a"))}");
        Ensure(Py.Equal(item2.GetValueOrDefault("b"), S("b1")), $"final 'b' is {Py.Repr(item2.GetValueOrDefault("b"))}, expected the PUT's value");
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

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
