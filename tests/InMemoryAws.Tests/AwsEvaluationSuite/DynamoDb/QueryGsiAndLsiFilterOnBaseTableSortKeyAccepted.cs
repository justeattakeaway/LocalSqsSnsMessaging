// Translated from behavioral/dynamodb/check_query_gsi_and_lsi_filter_on_base_table_sort_key_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-gsi-and-lsi-filter-on-base-table-sort-key-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryGsiAndLsiFilterOnBaseTableSortKeyAccepted
{
    private const string Check = "query-gsi-and-lsi-filter-on-base-table-sort-key-accepted";
    private const string Gsi = "gsi1";
    private const string Lsi = "lsi1";
    private const int VisibleS = 180;

    private static readonly (string Code, string Message) GsiKeyInFilter = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: gh");

    private static readonly (string Code, string Message) LsiKeyInFilter = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: lsk");

    /// <summary>
    /// A Query against a GSI or an LSI accepts a FilterExpression on the base table's own sort
    /// key, not a key of either index, and filters the page by it; naming the queried index's own
    /// key attribute in the filter is refused with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-gsi-and-lsi-filter-on-base-table-sort-key-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-basekeyfilt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gh", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gh", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        double Monotonic() => clock.Elapsed.TotalSeconds;
        var deadline = Monotonic() + VisibleS;
        while (true)
        {
            var gsis = (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes ?? [];
            if (gsis.Count > 0 && gsis[0].IndexStatus == IndexStatus.ACTIVE)
            {
                break;
            }

            Ensure(Monotonic() < deadline, $"{Gsi} did not reach ACTIVE, last saw {Py.Repr(gsis.Select(g => g.IndexStatus).ToList())}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("keep")), ("gh", S("g")), ("lsk", S("l1"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("skip")), ("gh", S("g")), ("lsk", S("l2"))));

        // GSI arm: wait for both items to propagate, then prove a filter on the base table's sort
        // key -- not a key of gsi1 -- actually selects between them, not merely passes through.
        deadline = Monotonic() + VisibleS;
        QueryResponse unfiltered;
        while (true)
        {
            unfiltered = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gh = :g",
                ExpressionAttributeValues = Item((":g", S("g"))),
            });
            if (unfiltered.Count == 2 || Monotonic() >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(unfiltered.Count == 2, $"expected both seeded items visible on {Gsi} within {VisibleS}s, got Count={unfiltered.Count}");

        var gsiFiltered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gh = :g",
            FilterExpression = "sk = :keep",
            ExpressionAttributeValues = Item((":g", S("g")), (":keep", S("keep"))),
        });
        Ensure(
            Py.Equal(gsiFiltered.Items.Select(i => i["sk"]).ToList(), new[] { S("keep") }),
            $"GSI Query filtered on the base table's sort key returned {Py.Repr(gsiFiltered.Items)}, " +
            "expected only the item whose sk is 'keep'");

        // LSI arm: the same base-table sort-key filter, strongly consistent -- no propagation wait.
        var lsiFiltered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Lsi,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "sk = :keep",
            ExpressionAttributeValues = Item((":p", S("p1")), (":keep", S("keep"))),
        });
        Ensure(
            Py.Equal(lsiFiltered.Items.Select(i => i["sk"]).ToList(), new[] { S("keep") }),
            $"LSI Query filtered on the base table's sort key returned {Py.Repr(lsiFiltered.Items)}, " +
            "expected only the item whose sk is 'keep'");

        // Break exactly one thing: name the queried index's own key attribute in the filter instead.
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gh = :g",
                FilterExpression = "gh = :g2",
                ExpressionAttributeValues = Item((":g", S("g")), (":g2", S("g"))),
            }),
            GsiKeyInFilter.Code,
            GsiKeyInFilter.Message);
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Lsi,
                ConsistentRead = true,
                KeyConditionExpression = "pk = :p",
                FilterExpression = "lsk = :l",
                ExpressionAttributeValues = Item((":p", S("p1")), (":l", S("l1"))),
            }),
            LsiKeyInFilter.Code,
            LsiKeyInFilter.Message);

        // Neither rejected call wrote or deleted anything: the base table still holds only the seed.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(
            final.Select(i => i["sk"].S).Order(StringComparer.Ordinal).SequenceEqual(["keep", "skip"]),
            $"expected only the two seeded items to remain, got {Py.Repr(final)}");
    }

    /// <summary><paramref name="fn"/> is refused with exactly (<paramref name="code"/>, <paramref name="message"/>); an unexpected success fails.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(
            (error.Code, error.Message) == (code, message),
            $"expected {code}: {Py.Repr(message)}, got {error.Code}: {Py.Repr(error.Message)}");
    }
}
