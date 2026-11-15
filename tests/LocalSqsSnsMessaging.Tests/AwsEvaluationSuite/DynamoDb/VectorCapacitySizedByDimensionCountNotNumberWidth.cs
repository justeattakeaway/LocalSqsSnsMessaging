// Translated from behavioral/dynamodb/check_vector_capacity_sized_by_dimension_count_not_number_width.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-capacity-sized-by-dimension-count-not-number-width</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorCapacitySizedByDimensionCountNotNumberWidth
{
    private const string Check = "vector-capacity-sized-by-dimension-count-not-number-width";
    private const int LowDims = 254;
    private const int EdgeDims = 255;
    private const int HighDims = 4096;

    /// <summary>
    /// A vector index's write capacity scales at 4 bytes per dimension, floored at 1024 bytes,
    /// identically whatever digit width the stored numbers use; a strongly consistent GetItem's
    /// capacity tracks that same billed size rather than the numbers' real stored width.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "vector indexes are not emulated: CreateTable accepts VectorIndexes but DescribeTable reports none")]
    [DisplayName("dynamodb.vector-capacity-sized-by-dimension-count-not-number-width")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbveccap-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                Vi("idx-low", "a", LowDims),
                Vi("idx-edge", "b", EdgeDims),
                Vi("idx-big", "c", HighDims),
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table)));
        await WaitActive(ctx, ddb, table, 3);

        async Task<ConsumedCapacity> Put(string key, string attr, int dims, int digits)
        {
            var item = Item(("pk", S(key)), (attr, L([.. Enumerable.Range(0, dims).Select(_ => N(new string('9', digits)))])));
            var output = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item, ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
            return output.ConsumedCapacity;
        }

        // One dimension below the per-dimension threshold: the per-write floor of 1024 bytes applies,
        // and the table write costs exactly 1 WCU.
        var low = await Put("e1", "a", LowDims, 1);
        var lowBytes = VectorBytes(low, "idx-low");
        Ensure(lowBytes == 1024.0, $"a {LowDims}-dimension write's VectorWriteRequestBytes was {Py.Repr(lowBytes)}, expected the 1024-byte floor");
        Ensure(low.Table?.CapacityUnits == 1.0, $"table WCU for the floored write was {TableRepr(low)}, expected 1.0");

        // One dimension more crosses the floor by exactly one byte, and the table WCU doubles at
        // that 1024/1025-byte boundary.
        var edgeSmall = await Put("e2", "b", EdgeDims, 1);
        var edgeSmallBytes = VectorBytes(edgeSmall, "idx-edge");
        Ensure(
            edgeSmallBytes == 1025.0,
            $"a {EdgeDims}-dimension write's VectorWriteRequestBytes was {Py.Repr(edgeSmallBytes)}, expected 1025.0, one byte past the floor");
        Ensure(edgeSmall.Table?.CapacityUnits == 2.0, $"table WCU just past the floor was {TableRepr(edgeSmall)}, expected 2.0");

        // The same dimension count, same key, but numbers with far more significant digits: the
        // billed bytes do not move, because the per-dimension rate does not look at the numbers.
        var edgeBig = await Put("e3", "b", EdgeDims, 38);
        var edgeBigBytes = VectorBytes(edgeBig, "idx-edge");
        Ensure(
            edgeBigBytes == edgeSmallBytes && edgeSmallBytes == 1025.0,
            $"38-significant-digit numbers changed VectorWriteRequestBytes to {Py.Repr(edgeBigBytes)}, expected the same 1025.0 as single-digit numbers at the same dimension count");

        // At a much larger dimension count the same invariance holds, and establishes the billed
        // size that the read capacity below is compared against.
        var bigSmall = await Put("e4", "c", HighDims, 1);
        var bigSmallBytes = VectorBytes(bigSmall, "idx-big");
        Ensure(bigSmallBytes == 16389.0, $"a {HighDims}-dimension write's VectorWriteRequestBytes was {Py.Repr(bigSmallBytes)}, expected 16389.0");
        Ensure(bigSmall.Table?.CapacityUnits == 17.0, $"table WCU for the {HighDims}-dimension write was {TableRepr(bigSmall)}, expected 17.0");

        var bigBig = await Put("e5", "c", HighDims, 38);
        var bigBigBytes = VectorBytes(bigBig, "idx-big");
        Ensure(
            bigBigBytes == bigSmallBytes && bigSmallBytes == 16389.0,
            $"38-significant-digit numbers at {HighDims} dimensions changed VectorWriteRequestBytes to {Py.Repr(bigBigBytes)}, expected the same 16389.0 as single-digit numbers");

        // A strongly consistent GetItem's capacity tracks that same billed size, not the numbers'
        // real stored width: the single-digit and the 38-digit item cost the same to read.
        var readSmall = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table, Key = Item(("pk", S("e4"))), ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var readBig = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table, Key = Item(("pk", S("e5"))), ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var readSmallCu = readSmall.ConsumedCapacity?.CapacityUnits;
        var readBigCu = readBig.ConsumedCapacity?.CapacityUnits;
        Ensure(readSmallCu == 5.0, $"a consistent GetItem of the {HighDims}-dimension item cost {Py.Repr(readSmallCu)} RCU, expected 5.0");
        Ensure(
            readBigCu == readSmallCu && readSmallCu == 5.0,
            $"the 38-significant-digit item cost {Py.Repr(readBigCu)} RCU to read, expected the same 5.0 as the single-digit item at the same dimension count");
        Ensure(
            readSmall.Item["c"].L[0].N == "9",
            $"GetItem returned the single-digit item's first component as {Py.Repr(readSmall.Item["c"].L[0].N)}, expected '9'");
        Ensure(
            readBig.Item["c"].L[0].N == new string('9', 38),
            $"GetItem returned the 38-digit item's first component as {Py.Repr(readBig.Item["c"].L[0].N)}, expected 38 nines");
    }

    private static double? VectorBytes(ConsumedCapacity capacity, string index) =>
        capacity.VectorIndexes is { } indexes && indexes.TryGetValue(index, out var vector) ? vector.VectorWriteRequestBytes : null;

    private static string TableRepr(ConsumedCapacity capacity) =>
        capacity.Table is null ? "None" : $"{{'CapacityUnits': {Py.Repr(capacity.Table.CapacityUnits)}}}";

    private static VectorIndex Vi(string name, string attr, int dims) => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = attr },
        Dimensions = dims,
        DistanceFunction = VectorDistanceFunction.COSINE,
        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
    };

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<TableDescription> WaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table, int nIndexes, double capS = 240.0)
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
