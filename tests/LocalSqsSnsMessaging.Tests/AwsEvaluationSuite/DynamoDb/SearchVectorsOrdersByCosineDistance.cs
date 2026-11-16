// Translated from behavioral/dynamodb/check_search_vectors_orders_by_cosine_distance.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.search-vectors-orders-by-cosine-distance</summary>
[Category("AwsEvaluationSuite")]
public sealed class SearchVectorsOrdersByCosineDistance
{
    /// <summary>
    /// SearchVectors returns nearest-first by cosine DISTANCE: an identical vector scores 0, an
    /// orthogonal one 1 and an opposite one 2, so ordering and scale are both pinned.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.search-vectors-orders-by-cosine-distance")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
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
                    IndexName = "cos",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "embedding" },
                    Dimensions = 3,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        // Fail FAST when CreateTable ignored `VectorIndexes` outright: an index that is absent
        // immediately after create is never going to appear. Poll only while the
        // index EXISTS but is still backfilling, which is the state a real create passes through.
        var described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
        Ensure(
            described.Count > 0,
            "CreateTable accepted VectorIndexes but DescribeTable reports none, so the vector index was silently dropped");
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(120) && described[0].IndexStatus?.Value != "ACTIVE")
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
            Ensure(described.Count > 0, "the vector index disappeared while it was backfilling");
        }

        Ensure(
            described[0].IndexStatus?.Value == "ACTIVE",
            $"the vector index never became ACTIVE: {Py.Repr(described[0].IndexStatus?.Value)}");
        (string Pk, int[] V)[] vectors = [("a", [1, 0, 0]), ("b", [0, 1, 0]), ("c", [-1, 0, 0])];
        foreach (var (pk, v) in vectors)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S(pk)),
                ("embedding", new AttributeValue { L = [.. v.Select(x => N(x.ToString(CultureInfo.InvariantCulture)))] })));
        }

        clock = FidelityTime.StartNew();
        List<SearchResultItem> results = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            results = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "cos",
                TopK = 3,
                SearchVector = [N("1"), N("0"), N("0")],
            })).SearchResults ?? [];
            if (results.Count == 3)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var scored = results.Select(r => (r.Item["pk"].S, r.Score ?? 0)).ToList();
        Ensure(
            scored.Select(s => s.S).SequenceEqual(["a", "b", "c"]),
            $"expected nearest-first a,b,c, got {Py.Repr(scored)}");
        Ensure(Math.Abs(scored[0].Item2 - 0.0) < 1e-4, $"an identical vector has cosine distance 0, got {Py.Repr(scored[0].Item2)}");
        Ensure(Math.Abs(scored[1].Item2 - 1.0) < 1e-4, $"an orthogonal vector has cosine distance 1, got {Py.Repr(scored[1].Item2)}");
        Ensure(Math.Abs(scored[2].Item2 - 2.0) < 1e-4, $"an opposite vector has cosine distance 2, got {Py.Repr(scored[2].Item2)}");
    }
}
