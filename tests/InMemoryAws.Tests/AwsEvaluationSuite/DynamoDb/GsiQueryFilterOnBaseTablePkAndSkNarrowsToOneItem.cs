// Translated from behavioral/dynamodb/check_gsi_query_filter_on_base_table_pk_and_sk_narrows_to_one_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-filter-on-base-table-pk-and-sk-narrows-to-one-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryFilterOnBaseTablePkAndSkNarrowsToOneItem
{
    private const string Check = "gsi-query-filter-on-base-table-pk-and-sk-narrows-to-one-item";
    private const string Gsi = "gsi1";
    private const string Gh = "shared";
    private const int VisibleS = 180;

    private static Dictionary<string, AttributeValue> ItemOf(string pk, string sk) => Item(("pk", S(pk)), ("sk", S(sk)), ("gh", S(Gh)));

    private static readonly Dictionary<string, AttributeValue> Alpha = ItemOf("pk-alpha", "sk-alpha");
    private static readonly Dictionary<string, AttributeValue> Beta = ItemOf("pk-beta", "sk-beta");
    private static readonly Dictionary<string, AttributeValue> Gamma = ItemOf("pk-gamma", "sk-gamma");

    /// <summary>
    /// A GSI Query's FilterExpression on the base table's partition key, and separately on
    /// its sort key -- neither a key of the queried index -- each narrows the page to the one
    /// item carrying that value, while ScannedCount still counts the whole index partition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-filter-on-base-table-pk-and-sk-narrows-to-one-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-basekeys-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gh", ScalarAttributeType.S),
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

        foreach (var item in new[] { Alpha, Beta, Gamma })
        {
            await ddb.PutItemAsync(table, item);
        }

        // GSIs are eventually consistent: wait for all three items to be visible on gsi1
        // before trusting a filtered page below to mean anything.
        deadline = Monotonic() + VisibleS;
        QueryResponse unfiltered;
        while (true)
        {
            unfiltered = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gh = :g",
                ExpressionAttributeValues = Item((":g", S(Gh))),
            });
            if (unfiltered.Count == 3 || Monotonic() >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            unfiltered.Count == 3,
            $"expected all three seeded items visible on {Gsi} within {VisibleS}s, " +
            $"got Count={unfiltered.Count}");

        var bySk = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gh = :g",
            FilterExpression = "sk = :s",
            ExpressionAttributeValues = Item((":g", S(Gh)), (":s", S("sk-beta"))),
        });
        Ensure(
            Py.Equal(bySk.Items, new[] { Beta }),
            $"filtering gsi1 on the base table's sort key returned {Py.Repr(bySk.Items)}, " +
            $"expected only {Py.Repr(Beta)}");
        Ensure(bySk.Count == 1, $"expected Count 1 for the sort-key filter, got {Py.Repr(bySk.Count)}");
        Ensure(
            bySk.ScannedCount == 3,
            "expected ScannedCount 3 (the whole gh partition) for the sort-key filter, " +
            $"got {Py.Repr(bySk.ScannedCount)}");

        var byPk = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gh = :g",
            FilterExpression = "pk = :p",
            ExpressionAttributeValues = Item((":g", S(Gh)), (":p", S("pk-gamma"))),
        });
        Ensure(
            Py.Equal(byPk.Items, new[] { Gamma }),
            $"filtering gsi1 on the base table's partition key returned {Py.Repr(byPk.Items)}, " +
            $"expected only {Py.Repr(Gamma)}");
        Ensure(byPk.Count == 1, $"expected Count 1 for the partition-key filter, got {Py.Repr(byPk.Count)}");
        Ensure(
            byPk.ScannedCount == 3,
            "expected ScannedCount 3 (the whole gh partition) for the partition-key filter, " +
            $"got {Py.Repr(byPk.ScannedCount)}");
    }
}
