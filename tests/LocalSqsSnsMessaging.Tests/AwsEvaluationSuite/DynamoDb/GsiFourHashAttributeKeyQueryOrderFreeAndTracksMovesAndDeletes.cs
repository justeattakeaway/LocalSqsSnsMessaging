// Translated from behavioral/dynamodb/check_gsi_four_hash_attribute_key_query_order_free_and_tracks_moves_and_deletes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-four-hash-attribute-key-query-order-free-and-tracks-moves-and-deletes</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiFourHashAttributeKeyQueryOrderFreeAndTracksMovesAndDeletes
{
    private const string Index = "by-four";
    private static readonly string[] Hash = ["ka", "kb", "kc", "kd"];
    private static readonly string[] Old = ["t1", "eu", "web", "blue"];
    private static readonly string[] New = ["t2", "us", "api", "red"];
    private static readonly string[] Other = ["t1", "eu", "web", "green"];
    private static readonly string[] ShuffledOrder = ["kc", "ka", "kd", "kb"];

    /// <summary>
    /// A GSI keyed on four HASH attributes is created; a Query naming them in any order finds the item,
    /// an overwrite changing the key moves it to the new composite partition, and a delete removes it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.html
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbMultiAttributeKeys, "a GSI keyed on four HASH attributes is refused at CreateTable with the KeySchema length validation")]
    [DisplayName("dynamodb.gsi-four-hash-attribute-key-query-order-free-and-tracks-moves-and-deletes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsi4-");
        List<KeySchemaElement> KeySchema() => [.. Hash.Select(n => new KeySchemaElement(n, KeyType.HASH))];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [.. new[] { "pk" }.Concat(Hash).Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex { IndexName = Index, KeySchema = KeySchema(), Projection = new Projection { ProjectionType = ProjectionType.ALL } },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        var desc = await WaitActive(ddb, ctx, table);
        var described = desc.GlobalSecondaryIndexes.Select(g => g.KeySchema.Select(k => (k.AttributeName, k.KeyType?.Value)).ToList()).ToList();
        var expectedSchema = Hash.Select(n => (n, (string?)"HASH")).ToList();
        Ensure(described.Count == 1 && described[0].SequenceEqual(expectedSchema), $"the four-HASH GSI describes its key as {Py.Repr(described)}");

        async Task<List<(string, string)>> Query(string[] values, string[] order)
        {
            var names = Hash.ToDictionary(n => $"#{n}", n => n, StringComparer.Ordinal);
            var vals = Hash.Zip(values).ToDictionary(p => $":{p.First}", p => S(p.Second), StringComparer.Ordinal);
            var cond = string.Join(" AND ", order.Select(n => $"#{n} = :{n}"));
            var items = new List<Dictionary<string, AttributeValue>>();
            var request = new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                KeyConditionExpression = cond,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = vals,
            };
            while (true)
            {
                var page = await ddb.QueryAsync(request);
                items.AddRange(page.Items);
                if (page.LastEvaluatedKey is not { Count: > 0 })
                {
                    break;
                }

                request.ExclusiveStartKey = page.LastEvaluatedKey;
            }

            return [.. items.Select(i => (i["pk"].S, i["data"].S)).Order()];
        }

        await ddb.PutItemAsync(table, ItemFor("p1", Old, "first"));
        await ddb.PutItemAsync(table, ItemFor("p2", Other, "sibling"));

        var got = await Poll(ctx, () => Query(Old, Hash), [("p1", "first")]);
        Ensure(got.SequenceEqual([("p1", "first")]), $"the four-attribute key in schema order selected {Py.Repr(got)}");
        var reversedOrder = Hash.Reverse().ToArray();
        got = await Poll(ctx, () => Query(Old, reversedOrder), [("p1", "first")]);
        Ensure(got.SequenceEqual([("p1", "first")]), $"the same key named in order {Py.Repr(reversedOrder)} selected {Py.Repr(got)}");
        got = await Poll(ctx, () => Query(Other, ShuffledOrder), [("p2", "sibling")]);
        Ensure(got.SequenceEqual([("p2", "sibling")]), $"the sibling key differing only in kd selected {Py.Repr(got)}");

        await ddb.PutItemAsync(table, ItemFor("p1", New, "moved"));
        got = await Poll(ctx, () => Query(New, reversedOrder), [("p1", "moved")]);
        Ensure(got.SequenceEqual([("p1", "moved")]), $"after the overwrite the new composite key selected {Py.Repr(got)}");
        got = await Poll(ctx, () => Query(Old, Hash), []);
        Ensure(got.Count == 0, $"after the overwrite the old composite key still selects {Py.Repr(got)}");

        await ddb.DeleteItemAsync(table, Item(("pk", S("p1"))));
        got = await Poll(ctx, () => Query(New, Hash), []);
        Ensure(got.Count == 0, $"after DeleteItem the index still holds {Py.Repr(got)}");
        got = await Query(Other, Hash);
        Ensure(got.SequenceEqual([("p2", "sibling")]), $"after deleting p1 the sibling key selected {Py.Repr(got)}");
        var @base = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1"))), ConsistentRead = true });
        Ensure(@base.Item is null, $"the deleted item still reads back: {Py.Repr(@base.Item)}");
    }

    private static Dictionary<string, AttributeValue> ItemFor(string pk, string[] values, string extra)
    {
        var item = Item(("pk", S(pk)), ("data", S(extra)));
        foreach (var (name, v) in Hash.Zip(values))
        {
            item[name] = S(v);
        }

        return item;
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var left = ctx.TimeLeftSeconds;
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, (left > 0 ? left : 300.0) - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>read() every 2 s until it equals want, bounded by the run budget.</summary>
    private static async Task<List<(string, string)>> Poll(FidelityContext ctx, Func<Task<List<(string, string)>>> read, List<(string, string)> want)
    {
        var clock = FidelityTime.StartNew();
        var left = ctx.TimeLeftSeconds;
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, (left > 0 ? left : 120.0) - 20.0));
        while (true)
        {
            var value = await read();
            if (value.SequenceEqual(want) || clock.Elapsed >= budget)
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
