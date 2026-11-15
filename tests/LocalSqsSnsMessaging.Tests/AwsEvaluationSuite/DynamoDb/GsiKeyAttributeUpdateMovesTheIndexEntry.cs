// Translated from behavioral/dynamodb/check_gsi_key_attribute_update_moves_the_index_entry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-key-attribute-update-moves-the-index-entry</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiKeyAttributeUpdateMovesTheIndexEntry
{
    /// <summary>
    /// Updating an item's GSI partition-key attribute from A to B moves the index entry: a GSI Query on B returns the
    /// item, a Query on A returns none, and the base table holds one item with the new value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-key-attribute-update-moves-the-index-entry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsimove-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
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
        Dictionary<string, string?> indexStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            indexStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (indexStatus.GetValueOrDefault("gsi1") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(indexStatus.GetValueOrDefault("gsi1") == "ACTIVE", $"gsi1 never became ACTIVE within 300s: {Py.Repr(indexStatus)}");

        const string pk = "ORDER#1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("S1")), ("gsi1pk", S("A")), ("payload", S("p"))));

        Task<QueryResponse> Query(string value) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S(value))),
        });

        // The index must first become populated under the ORIGINAL key value, otherwise the
        // disappearance asserted below would be indistinguishable from an index that never indexed.
        clock = FidelityTime.StartNew();
        var aBefore = await Query("A");
        while (aBefore.Items.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            aBefore = await Query("A");
        }

        Ensure(
            aBefore.Items.Select(i => i["pk"].S).SequenceEqual([pk]),
            $"the item never appeared under the original index key gsi1pk=A: {Py.Repr(aBefore.Items)}");
        var aCountScanned = aBefore.ScannedCount;

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk)), ("sk", S("S1"))),
            UpdateExpression = "SET gsi1pk = :new",
            ExpressionAttributeValues = Item((":new", S("B"))),
        });

        // The move is two effects, and an emulator can produce either one alone: the new entry
        // appearing, and the old one going away. Poll until BOTH hold.
        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> bItems = [];
        List<Dictionary<string, AttributeValue>?> aItems = [null];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            bItems = (await Query("B")).Items;
            aItems = [.. (await Query("A")).Items];
            if (bItems.Count > 0 && aItems.Count == 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            bItems.Select(i => i["pk"].S).SequenceEqual([pk]),
            $"the item never appeared under the new index key gsi1pk=B within 120s: {Py.Repr(bItems)}");
        Ensure(aItems.Count == 0, $"the stale index entry under gsi1pk=A survived the key update: {Py.Repr(aItems)}");
        Ensure(
            aCountScanned >= 1,
            $"the query against the old key never actually ran against a populated index, so its emptiness above proves nothing: ScannedCount was {Py.Repr(aCountScanned)}");

        var @base = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("S1"))), ConsistentRead = true });
        Ensure(@base.Item["gsi1pk"].S == "B", $"the base item was not updated: {Py.Repr(@base.Item)}");
        Ensure(@base.Item["payload"].S == "p", $"the key update clobbered a non-key attribute: {Py.Repr(@base.Item)}");
    }
}
