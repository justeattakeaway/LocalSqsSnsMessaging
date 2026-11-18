// Translated from behavioral/dynamodb/check_attribute_updates_add_delete_put_return_values_on_existing_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-updates-add-delete-put-return-values-on-existing-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeUpdatesAddDeletePutReturnValuesOnExistingItem
{
    /// <summary>
    /// Legacy AttributeUpdates ADD unions a set with duplicates dropped and appends to a list;
    /// DELETE with a Value removes one set member, DELETE with none removes the whole attribute,
    /// and PUT replaces a map wholesale rather than merging it, each shown through UPDATED_NEW/OLD.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-updates-add-delete-put-return-values-on-existing-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-attrupd-");
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

        var key = Item(("pk", S("item1")), ("sk", S("s1")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("item1")), ("sk", S("s1")),
            ("counter", N("10")),
            ("tags", SS("a", "b")),
            ("hist", L(S("x"))),
            ("profile", M(("city", S("nyc")), ("note", S("old"))))));

        async Task<Dictionary<string, AttributeValue>?> Upd(string name, AttributeValueUpdate update, ReturnValue rv) =>
            (await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                AttributeUpdates = new() { [name] = update },
                ReturnValues = rv,
            })).Attributes;

        var got = await Upd("counter", new AttributeValueUpdate { Action = AttributeAction.ADD, Value = N("5") }, ReturnValue.UPDATED_NEW);
        Ensure(Py.Equal(got, Item(("counter", N("15")))), $"ADD counter +5 with UPDATED_NEW returned {Py.Repr(got)}");

        got = await Upd("counter", new AttributeValueUpdate { Action = AttributeAction.ADD, Value = N("3") }, ReturnValue.UPDATED_OLD);
        Ensure(Py.Equal(got, Item(("counter", N("15")))), $"ADD counter +3 with UPDATED_OLD returned {Py.Repr(got)}, expected the pre-update 15");

        got = await Upd("tags", new AttributeValueUpdate { Action = AttributeAction.ADD, Value = SS("b", "c") }, ReturnValue.UPDATED_NEW);
        Ensure(
            got is not null && got["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["a", "b", "c"]) && got["tags"].SS.Count == 3,
            $"ADD tags {{b,c}} onto existing {{a,b}} with UPDATED_NEW returned {Py.Repr(got)}, " +
            "expected the union {a,b,c} with the duplicate 'b' dropped");

        got = await Upd("hist", new AttributeValueUpdate { Action = AttributeAction.ADD, Value = L(S("z")) }, ReturnValue.UPDATED_NEW);
        Ensure(
            Py.Equal(got, Item(("hist", L(S("x"), S("z"))))),
            $"ADD a one-element list onto an existing list with UPDATED_NEW returned {Py.Repr(got)}, expected the element appended");

        got = await Upd("tags", new AttributeValueUpdate { Action = AttributeAction.DELETE, Value = SS("a") }, ReturnValue.UPDATED_OLD);
        Ensure(
            got is not null && got["tags"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["a", "b", "c"]),
            $"DELETE of set member 'a' with UPDATED_OLD returned {Py.Repr(got)}, expected the pre-delete set {{a,b,c}}");

        got = await Upd("profile", new AttributeValueUpdate { Action = AttributeAction.PUT, Value = M(("city", S("sf"))) }, ReturnValue.UPDATED_NEW);
        Ensure(
            Py.Equal(got, Item(("profile", M(("city", S("sf")))))),
            $"PUT of a whole map with UPDATED_NEW returned {Py.Repr(got)}, expected the map replaced wholesale " +
            "with no 'note' carried over from before");

        got = await Upd("hist", new AttributeValueUpdate { Action = AttributeAction.DELETE }, ReturnValue.UPDATED_OLD);
        Ensure(
            Py.Equal(got, Item(("hist", L(S("x"), S("z"))))),
            $"DELETE of the whole 'hist' attribute with UPDATED_OLD returned {Py.Repr(got)}, expected its pre-delete value");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(item.GetValueOrDefault("counter"), N("18")), $"final counter is {Py.Repr(item.GetValueOrDefault("counter"))}, expected 18");
        Ensure(
            (item.GetValueOrDefault("tags")?.SS ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(["b", "c"]),
            $"final tags are {Py.Repr(item.GetValueOrDefault("tags"))}, expected {{b,c}} after the member delete");
        Ensure(
            Py.Equal(item.GetValueOrDefault("profile"), M(("city", S("sf")))),
            $"final profile is {Py.Repr(item.GetValueOrDefault("profile"))}, expected city=sf only");
        Ensure(!item.ContainsKey("hist"), $"'hist' is still present after its whole-attribute DELETE: {Py.Repr(item.GetValueOrDefault("hist"))}");
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
