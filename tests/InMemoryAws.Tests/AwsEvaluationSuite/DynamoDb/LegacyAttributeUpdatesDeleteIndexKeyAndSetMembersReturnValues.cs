// Translated from behavioral/dynamodb/check_legacy_attribute_updates_delete_index_key_and_set_members_return_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attribute-updates-delete-index-key-and-set-members-return-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributeUpdatesDeleteIndexKeyAndSetMembersReturnValues
{
    /// <summary>
    /// On a hash+range table, legacy DELETE of a GSI key attribute carries UPDATED_NEW empty and
    /// drops the item from the index; DELETE of some set members carries UPDATED_NEW with the
    /// remaining members, and DELETE of the last members carries it empty and drops the attribute.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attribute-updates-delete-index-key-and-set-members-return-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-legacyidx-");
        const string index = "gsi1idx";
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = index,
                    KeySchema = [new KeySchemaElement("gsi1", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> IdxKey() => Item(("pk", S("idx1")), ("sk", S("s1")));
        var gsiValue = S("g1");
        await ddb.PutItemAsync(table, Item(("pk", S("idx1")), ("sk", S("s1")), ("gsi1", gsiValue), ("other", S("o0"))));
        var clock = FidelityTime.StartNew();
        var before = await GsiCount(ddb, table, index, "gsi1", gsiValue);
        while (before != 1 && clock.Elapsed.TotalSeconds < 60.0)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            before = await GsiCount(ddb, table, index, "gsi1", gsiValue);
        }

        Ensure(before == 1, $"the item must be visible through the index before the delete, saw count {before}");

        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = IdxKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate> { ["gsi1"] = new() { Action = AttributeAction.DELETE } },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            got is null || got.Count == 0,
            $"legacy DELETE of an index-key attribute carries UPDATED_NEW {Py.Repr(got)}, expected nothing " +
            "(DELETE has no new value)");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = IdxKey(), ConsistentRead = true })).Item;
        Ensure(!item.ContainsKey("gsi1"), $"the index-key attribute is still present after DELETE: {Py.Repr(item.GetValueOrDefault("gsi1"))}");
        Ensure(Py.Equal(item.GetValueOrDefault("other"), S("o0")), $"the rest of the item must be untouched, got {Py.Repr(item)}");

        clock = FidelityTime.StartNew();
        var after = await GsiCount(ddb, table, index, "gsi1", gsiValue);
        while (after != 0 && clock.Elapsed.TotalSeconds < 60.0)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            after = await GsiCount(ddb, table, index, "gsi1", gsiValue);
        }

        Ensure(after == 0, $"the item must disappear from the index once its key attribute is deleted, saw count {after}");

        Dictionary<string, AttributeValue> SetKey() => Item(("pk", S("setitem")), ("sk", S("s1")));
        await ddb.PutItemAsync(table, Item(("pk", S("setitem")), ("sk", S("s1")), ("tags", SS("a", "b", "c"))));

        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = SetKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate> { ["tags"] = new() { Action = AttributeAction.DELETE, Value = SS("a") } },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            got is not null && (got.GetValueOrDefault("tags")?.SS ?? []).ToHashSet().SetEquals(["b", "c"]),
            $"legacy DELETE of one set member with UPDATED_NEW returned {Py.Repr(got)}, expected the remaining {{b,c}}");
        var item2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = SetKey(), ConsistentRead = true })).Item;
        Ensure(
            (item2.GetValueOrDefault("tags")?.SS ?? []).ToHashSet().SetEquals(["b", "c"]),
            $"after the one-member delete the stored set is {Py.Repr(item2.GetValueOrDefault("tags"))}, expected {{b,c}}");

        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = SetKey(),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate> { ["tags"] = new() { Action = AttributeAction.DELETE, Value = SS("b", "c") } },
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            got is null || got.Count == 0,
            $"legacy DELETE of a set's last members carries UPDATED_NEW {Py.Repr(got)}, expected nothing " +
            "(the attribute is gone, DELETE has no new value)");
        var item3 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = SetKey(), ConsistentRead = true })).Item;
        Ensure(!item3.ContainsKey("tags"), $"'tags' must be gone after its last members are deleted, found {Py.Repr(item3.GetValueOrDefault("tags"))}");
    }

    private static async Task<int?> GsiCount(IAmazonDynamoDB ddb, string table, string index, string keyName, AttributeValue value) =>
        (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "#k = :v",
            ExpressionAttributeNames = Names(("#k", keyName)),
            ExpressionAttributeValues = Item((":v", value)),
        })).Count;

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

    /// <summary>Poll DescribeTable every 2 s until the table and its index are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
