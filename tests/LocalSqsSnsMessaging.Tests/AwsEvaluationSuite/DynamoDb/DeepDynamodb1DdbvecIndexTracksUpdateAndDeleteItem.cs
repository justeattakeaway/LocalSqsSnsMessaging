// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_index_tracks_update_and_delete_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-index-tracks-update-and-delete-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecIndexTracksUpdateAndDeleteItem
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-index-tracks-update-and-delete-item";
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// After UpdateItem moves x4 to 0.5, DeleteItem removes x1 and REMOVE strips x2's vector, a EUCLIDEAN search
    /// returns exactly [x4, x3] with scores 0.5 and 3, while x2 stays in the table without its vector.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable accepts VectorIndexes but drops them: DescribeTable reports none")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-index-tracks-update-and-delete-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-euc",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);
        foreach (var x in new[] { 1, 2, 3, 4 })
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"x{x}")), ("tag", S("keep")), ("emb", new AttributeValue { L = Vec([x, 0, 0, 0]) })));
        }

        async Task<List<(string Pk, double Score)>> Search() =>
            ((await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-euc",
                TopK = 10,
                SearchVector = Vec(Query),
            })).SearchResults ?? []).Select(r => (r.Item["pk"].S, (double)r.Score!)).ToList();

        async Task<List<(string Pk, double Score)>> Until(string[] wantIds)
        {
            List<(string Pk, double Score)> got = [];
            var clock = FidelityTime.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                got = await Search();
                if (got.Select(g => g.Pk).SequenceEqual(wantIds))
                {
                    return got;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            return got;
        }

        var before = await Until(["x1", "x2", "x3", "x4"]);
        Ensure(before.Select(b => b.Pk).SequenceEqual(["x1", "x2", "x3", "x4"]), $"initial search {Py.Repr(before)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x4"))),
            UpdateExpression = "SET emb = :e",
            ExpressionAttributeValues = Item((":e", new AttributeValue { L = Vec([0.5, 0, 0, 0]) })),
        });
        await ddb.DeleteItemAsync(table, Item(("pk", S("x1"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x2"))),
            UpdateExpression = "REMOVE emb",
        });

        var still = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x2"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(still, Item(("pk", S("x2")), ("tag", S("keep")))),
            $"x2 should remain in the table without its vector, GetItem gave {Py.Repr(still)}");

        var after = await Until(["x4", "x3"]);
        Ensure(
            after.Select(a => a.Pk).SequenceEqual(["x4", "x3"]),
            $"after update/delete/remove the index returned {Py.Repr(after)}, expected [x4, x3]");
        Ensure(Math.Abs(after[0].Score - 0.5) < 1e-4, $"moved item x4 scored {Py.Repr(after[0].Score)}, expected 0.5");
        Ensure(Math.Abs(after[1].Score - 3.0) < 1e-4, $"x3 scored {Py.Repr(after[1].Score)}, expected 3.0");
    }
    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector index "
            + "was silently dropped");
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            if (tbl.TableStatus == TableStatus.ACTIVE && (tbl.VectorIndexes ?? []).All(v => v.IndexStatus?.ToString() == "ACTIVE"))
            {
                return tbl;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }
}
