// Translated from behavioral/dynamodb/check_update_item_lsi_sort_key_move_and_combined_gsi_lsi_move.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-lsi-sort-key-move-and-combined-gsi-lsi-move</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemLsiSortKeyMoveAndCombinedGsiLsiMove
{
    /// <summary>
    /// UpdateItem that changes an LSI sort-key attribute moves the LSI entry: a strongly
    /// consistent Query on the old value returns nothing, the new value returns the item. A single
    /// UpdateItem that changes a GSI key and an LSI key together moves both index entries.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-lsi-sort-key-move-and-combined-gsi-lsi-move")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsimove-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsisk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsisk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string> gsiStatus = [];
        while (clock.Elapsed.TotalSeconds < 300)
        {
            var described = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            gsiStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus.Value, StringComparer.Ordinal);
            if (gsiStatus.GetValueOrDefault("gsi1") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus.GetValueOrDefault("gsi1") == "ACTIVE", $"gsi1 never became ACTIVE within 300s: {Py.Repr(gsiStatus)}");

        Task<QueryResponse> LsiQuery(string pk, string lsiskValue) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND lsisk = :v",
            ExpressionAttributeValues = Item((":p", S(pk)), (":v", S(lsiskValue))),
        });

        Task<QueryResponse> GsiQuery(string gsi1pkValue) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S(gsi1pkValue))),
        });

        static List<string> Pks(List<Dictionary<string, AttributeValue>> items) => items.Select(i => i["pk"].S).ToList();

        // Part 1: an UpdateItem that changes only the LSI sort key moves only that entry.
        const string pk1 = "ORDER#1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk1)), ("sk", S("S1")), ("lsisk", S("A")), ("payload", S("p1"))));

        var before = await LsiQuery(pk1, "A");
        Ensure(Pks(before.Items).SequenceEqual([pk1]), $"the item never appeared under the original LSI sort key lsisk=A: {Py.Repr(before.Items)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk1)), ("sk", S("S1"))),
            UpdateExpression = "SET lsisk = :new",
            ExpressionAttributeValues = Item((":new", S("B"))),
        });

        var oldAfter = await LsiQuery(pk1, "A");
        Ensure(oldAfter.Items.Count == 0, $"the stale LSI entry under lsisk=A survived the sort-key update: {Py.Repr(oldAfter.Items)}");
        var newAfter = await LsiQuery(pk1, "B");
        Ensure(Pks(newAfter.Items).SequenceEqual([pk1]), $"the item never appeared under the new LSI sort key lsisk=B: {Py.Repr(newAfter.Items)}");

        var base1 = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk1)), ("sk", S("S1"))), ConsistentRead = true });
        Ensure(base1.Item["lsisk"].S == "B", $"the base item was not updated: {Py.Repr(base1.Item)}");
        Ensure(base1.Item["payload"].S == "p1", $"the sort-key update clobbered a non-key attribute: {Py.Repr(base1.Item)}");

        // Part 2: one UpdateItem that changes a GSI key and the LSI sort key together moves both.
        const string pk2 = "ORDER#2";
        await ddb.PutItemAsync(table, Item(("pk", S(pk2)), ("sk", S("S2")), ("lsisk", S("C")), ("gsi1pk", S("X")), ("payload", S("p2"))));

        var lsiBefore2 = await LsiQuery(pk2, "C");
        Ensure(
            Pks(lsiBefore2.Items).SequenceEqual([pk2]),
            $"the second item never appeared under its original LSI sort key lsisk=C: {Py.Repr(lsiBefore2.Items)}");
        var gsiClock = FidelityTime.StartNew();
        var gsiBefore2 = await GsiQuery("X");
        while (gsiBefore2.Items.Count == 0 && gsiClock.Elapsed.TotalSeconds < 120)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            gsiBefore2 = await GsiQuery("X");
        }

        Ensure(
            Pks(gsiBefore2.Items).SequenceEqual([pk2]),
            $"the second item never appeared under its original GSI key gsi1pk=X: {Py.Repr(gsiBefore2.Items)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk2)), ("sk", S("S2"))),
            UpdateExpression = "SET lsisk = :newlsi, gsi1pk = :newgsi",
            ExpressionAttributeValues = Item((":newlsi", S("D")), (":newgsi", S("Y"))),
        });

        var lsiOldAfter2 = await LsiQuery(pk2, "C");
        Ensure(lsiOldAfter2.Items.Count == 0, $"the stale LSI entry under lsisk=C survived the combined update: {Py.Repr(lsiOldAfter2.Items)}");
        var lsiNewAfter2 = await LsiQuery(pk2, "D");
        Ensure(Pks(lsiNewAfter2.Items).SequenceEqual([pk2]), $"the item never appeared under the new LSI sort key lsisk=D: {Py.Repr(lsiNewAfter2.Items)}");

        var gsiMoveClock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> gsiYItems = [];
        List<Dictionary<string, AttributeValue>?> gsiXItems = [null];
        while (gsiMoveClock.Elapsed.TotalSeconds < 120)
        {
            gsiYItems = (await GsiQuery("Y")).Items;
            gsiXItems = [.. (await GsiQuery("X")).Items];
            if (gsiYItems.Count > 0 && gsiXItems.Count == 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(Pks(gsiYItems).SequenceEqual([pk2]), $"the item never appeared under the new GSI key gsi1pk=Y within 120s: {Py.Repr(gsiYItems)}");
        Ensure(gsiXItems.Count == 0, $"the stale GSI entry under gsi1pk=X survived the combined update: {Py.Repr(gsiXItems)}");

        var base2 = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk2)), ("sk", S("S2"))), ConsistentRead = true });
        Ensure(base2.Item["lsisk"].S == "D", $"the base item LSI key was not updated: {Py.Repr(base2.Item)}");
        Ensure(base2.Item["gsi1pk"].S == "Y", $"the base item GSI key was not updated: {Py.Repr(base2.Item)}");
        Ensure(base2.Item["payload"].S == "p2", $"the combined key update clobbered a non-key attribute: {Py.Repr(base2.Item)}");
    }
}
