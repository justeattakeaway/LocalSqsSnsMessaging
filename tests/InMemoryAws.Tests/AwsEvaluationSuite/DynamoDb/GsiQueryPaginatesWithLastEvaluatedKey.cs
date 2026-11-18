// Translated from behavioral/dynamodb/check_gsi_query_paginates_with_last_evaluated_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-paginates-with-last-evaluated-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryPaginatesWithLastEvaluatedKey
{
    private const string CheckId = "gsi-query-paginates-with-last-evaluated-key";
    private const string Index = "by-owner";
    private const int Page = 7;
    private const int Items = 25;
    private static readonly string[] Owners = ["alice", "bob"];
    private static readonly string[] LekAttributes = ["id", "owner", "rank"];

    /// <summary>
    /// A GSI Query with Limit pages through LastEvaluatedKey to exactly one partition's items,
    /// ordered by the index sort key, at most Limit per page, reversed by ScanIndexForward=False.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-paginates-with-last-evaluated-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("id", ScalarAttributeType.S),
                new AttributeDefinition("owner", ScalarAttributeType.S),
                new AttributeDefinition("rank", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("owner", KeyType.HASH), new KeySchemaElement("rank", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, table));
        await WaitActive(ddb, table);

        // Items alternate between two owners; ranks are written out of order.
        var expected = new Dictionary<string, List<(int Rank, string Id, string Payload)>>(StringComparer.Ordinal)
        {
            ["alice"] = [],
            ["bob"] = [],
        };
        for (var i = 0; i < Items; i++)
        {
            var owner = i % 2 == 0 ? "alice" : "bob";
            var rank = i * 11 % 97;
            var itemId = "item-" + i.ToString("00", CultureInfo.InvariantCulture);
            await ddb.PutItemAsync(table, Item(
                ("id", S(itemId)),
                ("owner", S(owner)),
                ("rank", N(rank.ToString(CultureInfo.InvariantCulture))),
                ("payload", S($"payload-{i}"))));
            expected[owner].Add((rank, itemId, $"payload-{i}"));
        }

        foreach (var owner in expected.Keys)
        {
            expected[owner] = [.. expected[owner].OrderBy(e => e.Rank).ThenBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Payload, StringComparer.Ordinal)];
        }

        foreach (var owner in Owners)
        {
            var want = expected[owner];
            var forward = await PollFull(ddb, table, owner, want.Count, forward: true);
            Ensure(
                forward.SequenceEqual(want),
                $"GSI Query for {owner} with Limit={Page} collected {Py.Repr(forward)}, expected the {want.Count} items in ascending rank {Py.Repr(want)}");
            var backward = await Collect(ddb, table, owner, forward: false);
            Ensure(
                backward.SequenceEqual(Enumerable.Reverse(want)),
                $"ScanIndexForward=False for {owner} collected {Py.Repr(backward)}, expected the reverse");
        }

        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var e = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
            if (e is not null)
            {
                Ensure(e.Code == "ResourceNotFoundException", $"DescribeTable on the deleted table answered {Py.Repr(e.Code)}");
                Ensure(
                    e.Message == $"Requested resource not found: Table: {table} not found",
                    $"expected AWS's exact ResourceNotFoundException message, got {Py.Repr(e.Message)}");
                Ensure((int)e.StatusCode is >= 400 and < 500, "assert 400 <= e.response[\"ResponseMetadata\"][\"HTTPStatusCode\"] < 500");
                break;
            }

            Ensure(clock.Elapsed < TimeSpan.FromSeconds(120), "the deleted table still describes after 120s");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Every item of one GSI partition, following LastEvaluatedKey; each page holds &lt;= Limit.</summary>
    private static async Task<List<(int Rank, string Id, string Payload)>> Collect(IAmazonDynamoDB ddb, string table, string owner, bool forward)
    {
        var output = new List<(int Rank, string Id, string Payload)>();
        Dictionary<string, AttributeValue>? start = null;
        var pages = 0;
        while (true)
        {
            var page = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                Limit = Page,
                KeyConditionExpression = "#o = :o",
                ExpressionAttributeNames = Names(("#o", "owner")),
                ExpressionAttributeValues = Item((":o", S(owner))),
                ScanIndexForward = forward,
                ExclusiveStartKey = start,
            });
            var items = page.Items ?? [];
            Ensure(items.Count <= Page, $"a page held {items.Count} items, more than Limit={Page}");
            Ensure(page.Count == items.Count, $"page Count {Py.Repr(page.Count)} differs from its {items.Count} items");
            output.AddRange(items.Select(i => (int.Parse(i["rank"].N, CultureInfo.InvariantCulture), i["id"].S, i["payload"].S)));
            pages++;
            Ensure(pages <= 20, "LastEvaluatedKey never ran out after 20 pages");
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return output;
            }

            Ensure(
                start.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LekAttributes),
                $"a GSI LastEvaluatedKey carries the table and index keys, got {Py.Repr(start.Keys.Order(StringComparer.Ordinal).ToList())}");
        }
    }

    /// <summary>GSIs are eventually consistent: collect until the partition holds all n items.</summary>
    private static async Task<List<(int Rank, string Id, string Payload)>> PollFull(IAmazonDynamoDB ddb, string table, string owner, int n, bool forward)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var got = await Collect(ddb, table, owner, forward);
            if (got.Count >= n || clock.Elapsed >= TimeSpan.FromSeconds(60))
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 180.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its GSI did not become ACTIVE: {Py.Repr(last)}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string table)
    {
        try
        {
            await ddb.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
