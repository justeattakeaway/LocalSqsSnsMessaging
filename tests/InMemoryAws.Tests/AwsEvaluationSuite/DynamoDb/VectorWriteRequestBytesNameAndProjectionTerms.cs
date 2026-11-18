// Translated from behavioral/dynamodb/check_vector_write_request_bytes_name_and_projection_terms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-write-request-bytes-name-and-projection-terms</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorWriteRequestBytesNameAndProjectionTerms
{
    private const string Check = "vector-write-request-bytes-name-and-projection-terms";
    private const int FloorDims = 4;
    private const int LargeDims = 300;

    private static AttributeValue Vec(int n) => L([.. Enumerable.Range(0, n).Select(_ => N("0.5"))]);

    private static VectorIndex Vi(string name, string attr, int dims, Projection projection) => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = attr },
        Dimensions = dims,
        DistanceFunction = VectorDistanceFunction.COSINE,
        Projection = projection,
    };

    /// <summary>
    /// A vector index's VectorWriteRequestBytes adds the vector attribute name's byte length and
    /// every projected non-vector attribute's item size on top of the per-dimension cost. Two
    /// indexes each below the 1 KB floor in the same PutItem are floored independently.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.vector-write-request-bytes-name-and-projection-terms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecbytes2-");

        static Projection KeysOnly() => new() { ProjectionType = ProjectionType.KEYS_ONLY };
        static Projection IncludeExtra() => new() { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["extra"] };

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                Vi("idx-s1", "vs1", FloorDims, KeysOnly()),
                Vi("idx-s2", "vs2", FloorDims, KeysOnly()),
                Vi("idx-l1", "vl1", LargeDims, KeysOnly()),
                Vi("idx-l2", "vLongAttr", LargeDims, KeysOnly()),
                Vi("idx-p", "vp", LargeDims, IncludeExtra()),
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ctx, ddb, table, 5);

        async Task<Dictionary<string, VectorCapacity>> Put(string pk, Dictionary<string, AttributeValue>? extraAttrs = null)
        {
            var item = Item(
                ("pk", S(pk)),
                ("vs1", Vec(FloorDims)),
                ("vs2", Vec(FloorDims)),
                ("vl1", Vec(LargeDims)),
                ("vLongAttr", Vec(LargeDims)),
                ("vp", Vec(LargeDims)));
            foreach (var (name, value) in extraAttrs ?? [])
            {
                item[name] = value;
            }

            var output = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item, ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES });
            return output.ConsumedCapacity.VectorIndexes ?? [];
        }

        static double? Bytes(Dictionary<string, VectorCapacity> perIndex, string index) =>
            perIndex.TryGetValue(index, out var capacity) ? capacity.VectorWriteRequestBytes : null;

        // Two indexes below the floor in the same PutItem: each floors independently.
        var perIndexA = await Put("p1");
        var s1 = Bytes(perIndexA, "idx-s1");
        var s2 = Bytes(perIndexA, "idx-s2");
        Ensure(s1 == 1024.0, $"idx-s1 ({FloorDims} dims, below floor): {Py.Repr(s1)}, expected 1024.0");
        Ensure(
            s2 == 1024.0,
            $"idx-s2 ({FloorDims} dims, below floor, same request as idx-s1): {Py.Repr(s2)}, expected 1024.0 independently of idx-s1");

        // Two above-floor indexes, identical dims and projection, differing only in the vector
        // attribute's name length ('vl1' = 3 bytes, 'vLongAttr' = 9 bytes).
        var l1 = Bytes(perIndexA, "idx-l1");
        var l2 = Bytes(perIndexA, "idx-l2");
        Ensure(l1 == 1207.0, $"idx-l1: VectorWriteRequestBytes was {Py.Repr(l1)}, expected 1207.0");
        Ensure(l2 == 1213.0, $"idx-l2: VectorWriteRequestBytes was {Py.Repr(l2)}, expected 1213.0");
        Ensure(
            l2 - l1 == 6.0,
            $"idx-l1 and idx-l2 differ only in the name length of the vector attribute (3 vs 9 bytes); the VectorWriteRequestBytes delta was {Py.Repr(l2 - l1)}, expected 6.0");

        // idx-p projects a non-vector attribute (INCLUDE ['extra']); the first item has no 'extra'.
        var pA = Bytes(perIndexA, "idx-p");
        Ensure(pA == 1206.0, $"idx-p with no projected non-vector attribute: {Py.Repr(pA)}, expected 1206.0");

        // A second item, same vector, that also carries the projected 'extra' attribute.
        var perIndexB = await Put("p2", extraAttrs: Item(("extra", S("0123456789"))));
        var pB = Bytes(perIndexB, "idx-p");
        Ensure(
            pB == 1221.0,
            $"idx-p with a projected 'extra' attribute ({{'S': '0123456789'}}, 15 item-size bytes): {Py.Repr(pB)}, expected 1221.0");
        Ensure(
            pB - pA == 15.0,
            $"the only difference between the two items is the projected 'extra' attribute, whose item size is 5 (name) + 10 (value) = 15 bytes; the VectorWriteRequestBytes delta was {Py.Repr(pB - pA)}");

        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p2"))), ConsistentRead = true })).Item;
        var extra = stored.GetValueOrDefault("extra");
        Ensure(Py.Equal(extra, S("0123456789")), $"GetItem on p2 returned extra={Py.Repr(extra)}, expected {{'S': '0123456789'}}");
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
