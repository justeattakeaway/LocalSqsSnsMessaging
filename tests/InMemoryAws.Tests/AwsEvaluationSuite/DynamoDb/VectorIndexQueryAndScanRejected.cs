// Translated from behavioral/dynamodb/check_vector_index_query_and_scan_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.vector-index-query-and-scan-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class VectorIndexQueryAndScanRejected
{
    private const string Check = "vector-index-query-and-scan-rejected";

    /// <summary>
    /// Query and Scan naming a vector index are rejected with ValidationException;
    /// SearchVectors on the same index still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.vector-index-query-and-scan-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbvecqs-");
        const string index = "vec-idx";

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = index,
                    Dimensions = 2,
                    DistanceFunction = VectorDistanceFunction.COSINE,
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "Embedding" },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(table)).Table,
            t => t.TableStatus == TableStatus.ACTIVE
                && t.VectorIndexes is { Count: > 0 }
                && t.VectorIndexes[0].IndexStatus == IndexStatus.ACTIVE,
            t => $"{{'TableStatus': {Py.Repr(t.TableStatus?.Value)}, 'VectorIndexes': {Py.Repr(t.VectorIndexes?.Select(v => v.IndexName).ToList())}}}",
            timeoutS: 300,
            intervalS: 5);
        Ensure(
            described.VectorIndexes[0].IndexName == index,
            $"described vector index name {Py.Repr(described.VectorIndexes[0].IndexName)}, expected {Py.Repr(index)}");

        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("item-a")), ("Embedding", L(N("1.0"), N("0.0")))) });
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("item-b")), ("Embedding", L(N("0.0"), N("1.0")))) });

        var queryRejected = ("ValidationException", "Query operation not supported on this index type.");
        var queryGot = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("item-a"))),
        }));
        Ensure(queryGot == queryRejected, $"Query naming the vector index raised {Py.Repr(queryGot)}, expected {Py.Repr(queryRejected)}");

        var scanRejected = ("ValidationException", "Scan operation not supported on this index type.");
        var scanGot = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = index }));
        Ensure(scanGot == scanRejected, $"Scan naming the vector index raised {Py.Repr(scanGot)}, expected {Py.Repr(scanRejected)}");

        // Positive control: SearchVectors on the very same index succeeds, so the rejections
        // above are about the operation naming a vector index, not about the index itself.
        var results = await PollUntil(
            async () => (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = index,
                SearchVector = [N("1.0"), N("0.0")],
                TopK = 2,
            })).SearchResults ?? [],
            r => r.Count == 2,
            r => Py.Repr(r.Select(x => x.Item).ToList()),
            timeoutS: 180,
            intervalS: 5);
        var pks = results.Select(r => r.Item["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(pks.SequenceEqual(["item-a", "item-b"]), $"SearchVectors on the same index returned pks {Py.Repr(pks)}, expected ['item-a', 'item-b']");

        // Positive control: Query and Scan against the base table (naming no index) still work.
        var baseQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("item-a"))),
            ConsistentRead = true,
        });
        Ensure(baseQuery.Count == 1, $"a base-table Query for item-a returned Count {baseQuery.Count}, expected 1");
        var baseScan = await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true });
        Ensure(baseScan.Count == 2, $"a base-table Scan returned Count {baseScan.Count}, expected 2");
    }

    /// <summary>The AWS (code, message) fn() raises. RAISES when the call unexpectedly succeeds.</summary>
    private static async Task<(string?, string?)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s, last value: {(last is null ? "None" : repr(last))}");
    }
}
