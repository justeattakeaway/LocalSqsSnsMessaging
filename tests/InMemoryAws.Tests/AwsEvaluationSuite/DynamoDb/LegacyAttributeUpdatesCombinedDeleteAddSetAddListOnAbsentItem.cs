// Translated from behavioral/dynamodb/check_legacy_attribute_updates_combined_delete_add_set_add_list_on_absent_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attribute-updates-combined-delete-add-set-add-list-on-absent-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributeUpdatesCombinedDeleteAddSetAddListOnAbsentItem
{
    /// <summary>
    /// A single legacy AttributeUpdates call against a wholly absent item, combining a no-op
    /// DELETE with an ADD on a String Set and an ADD on a List, creates the item with only the two
    /// ADD results, and UPDATED_NEW reports only those two, nothing for the DELETE.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attribute-updates-combined-delete-add-set-add-list-on-absent-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-legacycombo-");
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("absent-combo")), ("sk", S("s1")));
        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
            {
                ["ghost"] = new() { Action = AttributeAction.DELETE },
                ["tags"] = new() { Action = AttributeAction.ADD, Value = SS("x", "y") },
                ["hist"] = new() { Action = AttributeAction.ADD, Value = L(S("z")) },
            },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            got is not null && (got.GetValueOrDefault("tags")?.SS ?? []).ToHashSet().SetEquals(["x", "y"]),
            $"combined call on an absent item with UPDATED_NEW returned {Py.Repr(got)} for 'tags', expected the ADD set {{x,y}}");
        Ensure(
            Py.Equal(got.GetValueOrDefault("hist"), L(S("z"))),
            $"combined call on an absent item with UPDATED_NEW returned {Py.Repr(got.GetValueOrDefault("hist"))} for 'hist', " +
            "expected the ADD list [z]");
        Ensure(!got.ContainsKey("ghost"), $"the no-op DELETE must not appear in UPDATED_NEW, got {Py.Repr(got)}");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(!item.ContainsKey("ghost"), $"the no-op DELETE must not create 'ghost', found {Py.Repr(item.GetValueOrDefault("ghost"))}");
        Ensure(
            (item.GetValueOrDefault("tags")?.SS ?? []).ToHashSet().SetEquals(["x", "y"]),
            $"the created item's 'tags' is {Py.Repr(item.GetValueOrDefault("tags"))}, expected {{x,y}}");
        Ensure(
            Py.Equal(item.GetValueOrDefault("hist"), L(S("z"))),
            $"the created item's 'hist' is {Py.Repr(item.GetValueOrDefault("hist"))}, expected [z]");
        Ensure(
            item.Keys.ToHashSet().SetEquals(["pk", "sk", "tags", "hist"]),
            $"the created item has attributes {Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList())}, expected only the key plus the two ADD results");
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
