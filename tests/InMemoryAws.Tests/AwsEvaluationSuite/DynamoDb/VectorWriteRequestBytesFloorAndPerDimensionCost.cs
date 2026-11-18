// Translated from behavioral/dynamodb/check_vector_write_request_bytes_floor_and_per_dimension_cost.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-write-request-bytes-floor-and-per-dimension-cost</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorWriteRequestBytesFloorAndPerDimensionCost
{
    private const string Check = "vector-write-request-bytes-floor-and-per-dimension-cost";
    private const int SmallDimensions = 4;
    private const int LargeDimensions = 4096;

    private static AttributeValue Vec(int n) => L([.. Enumerable.Range(0, n).Select(_ => N("0.5"))]);

    /// <summary>
    /// A vector write's per-index VectorWriteRequestBytes is exact, not just positive: a vector at
    /// or below the floor bills the floor, and one past it bills dimensions*4 plus fixed overhead.
    /// A later write that changes only one of the two vector attributes meters only that index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.vector-write-request-bytes-floor-and-per-dimension-cost")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecbytes-");

        static VectorIndex Vi(string name, string attr, int dims) => new()
        {
            IndexName = name,
            VectorAttribute = new VectorAttributeDefinition { AttributeName = attr },
            Dimensions = dims,
            DistanceFunction = VectorDistanceFunction.COSINE,
            Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
        };

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                Vi("idx-small", "emb_small", SmallDimensions),
                Vi("idx-large", "emb_large", LargeDimensions),
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ctx, ddb, table, 2);

        var item = Item(
            ("pk", S("v1")),
            ("emb_small", Vec(SmallDimensions)),
            ("emb_large", Vec(LargeDimensions)));

        async Task<ConsumedCapacity> Put(Dictionary<string, AttributeValue> writeItem)
        {
            var output = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = writeItem, ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
            return output.ConsumedCapacity;
        }

        var first = await Put(item);
        var perIndex = first.VectorIndexes ?? [];
        var smallBytes = perIndex.TryGetValue("idx-small", out var small) ? small.VectorWriteRequestBytes : null;
        var largeBytes = perIndex.TryGetValue("idx-large", out var large) ? large.VectorWriteRequestBytes : null;
        var tableWcu = first.Table?.CapacityUnits;

        Ensure(
            smallBytes == 1024.0,
            $"a {SmallDimensions}-dimension write is below the per-write floor; VectorWriteRequestBytes was {Py.Repr(smallBytes)}, expected the 1024-byte floor");
        Ensure(
            largeBytes == 16397.0,
            $"a {LargeDimensions}-dimension write; VectorWriteRequestBytes was {Py.Repr(largeBytes)}, expected 16397.0");
        Ensure(tableWcu == 17.0, $"table WCU for this write was {Py.Repr(tableWcu)}, expected 17.0");

        // A later write that changes only the small vector's value re-meters only idx-small: the
        // large vector attribute is unchanged, so it is untouched by this write.
        var changedItem = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal)
        {
            ["emb_small"] = L([.. Enumerable.Range(0, SmallDimensions).Select(_ => N("0.75"))]),
        };
        var second = await Put(changedItem);
        var secondPerIndex = second.VectorIndexes ?? [];
        Ensure(
            secondPerIndex.TryGetValue("idx-small", out var secondSmall) && secondSmall.VectorWriteRequestBytes == 1024.0,
            $"the changed small vector was not metered against idx-small: {Py.Repr(secondPerIndex.Keys.ToList())}");
        Ensure(
            !secondPerIndex.ContainsKey("idx-large"),
            $"the large vector's value did not change, but idx-large was metered anyway: {Py.Repr(secondPerIndex.Keys.ToList())}");
    }

    private static async Task<TableDescription> WaitVectorTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table, int nIndexes, double capS = 240.0)
    {
        var left = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(capS, left - 5.0)));
        var clock = FidelityTime.StartNew();
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        while (true)
        {
            tbl = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = tbl.VectorIndexes ?? [];
            if (tbl.TableStatus == TableStatus.ACTIVE && indexes.Count == nIndexes && indexes.All(v => v.IndexStatus == IndexStatus.ACTIVE))
            {
                return tbl;
            }

            if (clock.Elapsed >= deadline)
            {
                throw Fail(
                    $"table/vector indexes never became ACTIVE in time: status {Py.Repr(tbl.TableStatus?.Value)}, indexes "
                    + Py.Repr(indexes.Select(v => (v.IndexName, v.IndexStatus?.Value)).ToList()));
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
