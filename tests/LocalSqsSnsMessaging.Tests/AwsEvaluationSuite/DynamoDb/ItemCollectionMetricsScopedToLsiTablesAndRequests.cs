// Translated from behavioral/dynamodb/check_item_collection_metrics_scoped_to_lsi_tables_and_requests.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-collection-metrics-scoped-to-lsi-tables-and-requests</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemCollectionMetricsScopedToLsiTablesAndRequests
{
    private const string CheckId = "item-collection-metrics-scoped-to-lsi-tables-and-requests";
    private static readonly string[] LsiAttributes = ["pk", "sk", "l1"];
    private static readonly string[] PlainAttributes = ["pk", "sk"];

    private static Dictionary<string, object?>? Repr(ItemCollectionMetrics? metrics) => metrics is null
        ? null
        : new Dictionary<string, object?> { ["ItemCollectionKey"] = metrics.ItemCollectionKey, ["SizeEstimateRangeGB"] = metrics.SizeEstimateRangeGB };

    /// <summary>
    /// With ReturnItemCollectionMetrics=SIZE on an LSI table, Put/Update/DeleteItem report
    /// ItemCollectionMetrics keyed by the partition key alone; NONE, an omitted parameter, or a table
    /// without an LSI report nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-collection-metrics-scoped-to-lsi-tables-and-requests")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var withLsi = FidelityContext.UniqueName("sim2real-behav-icm-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = withLsi,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [.. LsiAttributes.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("l1", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(withLsi));

        var noLsi = FidelityContext.UniqueName("sim2real-behav-icm-nolsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = noLsi,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [.. PlainAttributes.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(noLsi));

        await ddb.WaitForTableExists(withLsi, delaySeconds: 2, maxAttempts: 250);
        await ddb.WaitForTableExists(noLsi, delaySeconds: 2, maxAttempts: 250);

        // The key schema is read, not assumed: the assertion below is an identity against the table's
        // own declared hash key, so it cannot pass by coincidence of naming.
        var schema = (await ddb.DescribeTableAsync(withLsi)).Table.KeySchema.ToDictionary(k => k.KeyType.Value, k => k.AttributeName, StringComparer.Ordinal);
        Ensure(
            schema.GetValueOrDefault("HASH") == "pk" && schema.GetValueOrDefault("RANGE") == "sk",
            $"setup: the LSI table must have a composite key schema to make this assertion meaningful, got {Py.Repr(schema)}");

        Dictionary<string, AttributeValue> AssertCollection(ItemCollectionMetrics? metrics, string op)
        {
            Ensure(metrics is not null, $"{op} with ReturnItemCollectionMetrics=SIZE on an LSI-bearing table must report ItemCollectionMetrics");
            var key = metrics.ItemCollectionKey;
            var members = new List<string>();
            if (metrics.ItemCollectionKey is not null)
            {
                members.Add("ItemCollectionKey");
            }

            if (metrics.SizeEstimateRangeGB is not null)
            {
                members.Add("SizeEstimateRangeGB");
            }

            Ensure(key is not null, $"{op} must report the collection it touched under ItemCollectionKey, got members {Py.Repr(members)}");
            Ensure(
                key.Keys.SequenceEqual([schema["HASH"]]),
                $"{op} must identify the item collection by the PARTITION key alone ({Py.Repr(schema["HASH"])}); the range key {Py.Repr(schema["RANGE"])} is not part of a collection identity. Got {Py.Repr(key)}");
            var size = metrics.SizeEstimateRangeGB;
            Ensure(size is { Count: 2 }, $"{op} must report SizeEstimateRangeGB as exactly [lower, upper], got {Py.Repr(size)}");
            Ensure(size[0] <= size[1], $"{op} reported SizeEstimateRangeGB bounds out of order: {Py.Repr(size)}");
            return key;
        }

        // --- reported by all three mutating operations on the LSI table ---
        var put = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = withLsi,
            Item = Item(("pk", S("coll-a")), ("sk", S("s1")), ("l1", S("x"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        });
        var putKey = AssertCollection(put.ItemCollectionMetrics, "PutItem");
        Ensure(Py.Equal(putKey, Item((schema["HASH"], S("coll-a")))), $"PutItem must report the partition key VALUE it wrote to, got {Py.Repr(putKey)}");

        var upd = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = withLsi,
            Key = Item(("pk", S("coll-a")), ("sk", S("s1"))),
            UpdateExpression = "SET extra = :e",
            ExpressionAttributeValues = Item((":e", S("1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        });
        AssertCollection(upd.ItemCollectionMetrics, "UpdateItem");

        await ddb.PutItemAsync(withLsi, Item(("pk", S("coll-b")), ("sk", S("s1"))));
        var dele = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = withLsi,
            Key = Item(("pk", S("coll-b")), ("sk", S("s1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        });
        var delKey = AssertCollection(dele.ItemCollectionMetrics, "DeleteItem");
        Ensure(Py.Equal(delKey, Item((schema["HASH"], S("coll-b")))), $"DeleteItem must report the partition key VALUE it removed from, got {Py.Repr(delKey)}");

        // --- not reported when it was not asked for ---
        var none = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = withLsi,
            Item = Item(("pk", S("coll-c")), ("sk", S("s1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.NONE,
        });
        Ensure(
            none.ItemCollectionMetrics is null,
            $"ReturnItemCollectionMetrics=NONE must suppress the member entirely, got {Py.Repr(Repr(none.ItemCollectionMetrics))}");
        var omitted = await ddb.PutItemAsync(withLsi, Item(("pk", S("coll-d")), ("sk", S("s1"))));
        Ensure(
            omitted.ItemCollectionMetrics is null,
            $"omitting ReturnItemCollectionMetrics must suppress the member entirely, got {Py.Repr(Repr(omitted.ItemCollectionMetrics))}");

        // --- not reported at all where there is no item collection to measure ---
        var plainPut = (await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = noLsi,
            Item = Item(("pk", S("plain")), ("sk", S("s1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        })).ItemCollectionMetrics;
        var plainUpdate = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = noLsi,
            Key = Item(("pk", S("plain")), ("sk", S("s1"))),
            UpdateExpression = "SET extra = :e",
            ExpressionAttributeValues = Item((":e", S("1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        })).ItemCollectionMetrics;
        var plainDelete = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = noLsi,
            Key = Item(("pk", S("plain")), ("sk", S("s1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        })).ItemCollectionMetrics;
        foreach (var (op, resp) in new[] { ("PutItem", plainPut), ("UpdateItem", plainUpdate), ("DeleteItem", plainDelete) })
        {
            Ensure(
                resp is null,
                $"{op} on a table with NO local secondary index has no item collection to size and must report nothing even when SIZE is requested, got {Py.Repr(Repr(resp))}");
        }

        // --- the writes those metrics described really happened ---
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = withLsi, Key = Item(("pk", S("coll-a")), ("sk", S("s1"))), ConsistentRead = true })).Item ?? [];
        Ensure(Py.Equal(got.GetValueOrDefault("extra"), S("1")), $"the PutItem and UpdateItem that reported metrics must have taken effect, got {Py.Repr(got)}");
        var removed = await ddb.GetItemAsync(new GetItemRequest { TableName = withLsi, Key = Item(("pk", S("coll-b")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(removed.Item is null, $"the DeleteItem that reported metrics must have removed the item, got {Py.Repr(removed.Item)}");
    }
}
