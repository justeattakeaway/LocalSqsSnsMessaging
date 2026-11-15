// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_consumed_capacity_reports_vector_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-consumed-capacity-reports-vector-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecConsumedCapacityReportsVectorBytes
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-consumed-capacity-reports-vector-bytes";
    private static readonly double[] Query = [1.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// PutItem with ReturnConsumedCapacity=INDEXES reports VectorWriteRequestBytes (1024 at 4 dims, 6151 at 1536)
    /// for every vector index the attribute feeds and none for a non-vector write; SearchVectors reports
    /// VectorSearchRequestBytes when asked for TOTAL and omits ConsumedCapacity otherwise.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable accepts VectorIndexes but drops them: DescribeTable reports none")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-consumed-capacity-reports-vector-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");

        static VectorIndex Vi(string name, string fn) => new()
        {
            IndexName = name,
            VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
            Dimensions = 4,
            DistanceFunction = fn,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
        };

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi("idx-cos", "COSINE"), Vi("idx-dot", "DOT_PRODUCT")],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitVectorTableActive(ddb, table);

        // A vector write: metered per vector index.
        var put = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec([1, 0, 0, 0]) })),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc = put.ConsumedCapacity;
        Ensure(cc?.TableName == table, $"PutItem ConsumedCapacity was {CapacityRepr(cc)}");
        var perIndex = cc.VectorIndexes ?? [];
        Ensure(
            perIndex.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["idx-cos", "idx-dot"]),
            $"a vector write must meter every index over the attribute; VectorIndexes was {Py.Repr(perIndex.ToDictionary(e => e.Key, e => (object?)e.Value.VectorWriteRequestBytes))}");
        foreach (var (name, cap) in perIndex)
        {
            Ensure(
                cap.VectorWriteRequestBytes == 1024.0,
                $"{name}: VectorWriteRequestBytes was {Py.Repr(cap.VectorWriteRequestBytes)} for dims=4");
        }

        // A large vector, over a dedicated table/index, to see whether the meter scales with dims.
        var table2 = FidelityContext.UniqueName("sim2real-behav-ddbvec-large-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table2,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes = [Vi2("idx-big", 1536, "COSINE")],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table2));
        await WaitVectorTableActive(ddb, table2);
        var put2 = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table2,
            Item = Item(("pk", S("v1")), ("emb", new AttributeValue { L = Vec(Enumerable.Repeat(1.0, 1536)) })),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc2 = put2.ConsumedCapacity?.VectorIndexes ?? [];
        var wb2 = cc2.TryGetValue("idx-big", out var big) ? big?.VectorWriteRequestBytes : null;
        Ensure(wb2 == 6151.0, $"idx-big: VectorWriteRequestBytes was {Py.Repr(wb2)} for dims=1536");

        // A non-vector write on the same table: no vector meter.
        var plain = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("plain")), ("note", S("no vector"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var pcc = plain.ConsumedCapacity;
        Ensure(
            pcc?.TableName == table && (pcc.CapacityUnits ?? 0) > 0,
            $"the non-vector PutItem reported ConsumedCapacity {CapacityRepr(pcc)}");
        Ensure(
            pcc.VectorIndexes is null,
            $"a write touching no vector attribute was metered against vector indexes: {CapacityRepr(pcc)}");

        // Search meter, requested.
        List<SearchResultItem> res = [];
        var clock = FidelityTime.StartNew();
        SearchVectorsResponse? output = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            output = await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-cos",
                TopK = 5,
                SearchVector = Vec(Query),
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            res = output.SearchResults ?? [];
            if (res.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            res.Select(r => r.Item["pk"].S).SequenceEqual(["v1"]),
            $"only the vector item is searchable; got {Py.Repr(res.Select(r => r.Item["pk"].S).ToList())}");
        var scc = output?.ConsumedCapacity;
        var sb = scc?.VectorSearchRequestBytes;
        Ensure(
            sb is > 0,
            $"SearchVectors ConsumedCapacity.VectorSearchRequestBytes was {Py.Repr(sb)} (whole: {Py.Repr(VectorCapacityDict(scc))})");

        // Search meter, not requested: absent.
        var quiet = await ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "idx-dot",
            TopK = 5,
            SearchVector = Vec(Query),
        });
        Ensure(
            (quiet.SearchResults ?? []).Select(r => r.Item["pk"].S).SequenceEqual(["v1"]),
            $"idx-dot search returned {Py.Repr((quiet.SearchResults ?? []).Select(r => r.Item).ToList())}");
        Ensure(
            quiet.ConsumedCapacity is null,
            $"ConsumedCapacity returned without ReturnConsumedCapacity: {Py.Repr(VectorCapacityDict(quiet.ConsumedCapacity))}");
    }

    private static VectorIndex Vi2(string name, int dims, string fn) => new()
    {
        IndexName = name,
        VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
        Dimensions = dims,
        DistanceFunction = fn,
        Projection = new Projection { ProjectionType = ProjectionType.ALL },
    };

    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static Dictionary<string, object?>? VectorCapacityDict(VectorCapacity? capacity) =>
        capacity is null ? null : new(StringComparer.Ordinal)
        {
            ["VectorSearchRequestBytes"] = capacity.VectorSearchRequestBytes,
            ["VectorWriteRequestBytes"] = capacity.VectorWriteRequestBytes,
        };

    private static string CapacityRepr(ConsumedCapacity? capacity) => capacity is null ? "{}" : Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["TableName"] = capacity.TableName,
        ["CapacityUnits"] = capacity.CapacityUnits,
        ["VectorIndexes"] = capacity.VectorIndexes?.ToDictionary(e => e.Key, e => VectorCapacityDict(e.Value), StringComparer.Ordinal),
    });

    private static async Task<TableDescription> WaitVectorTableActive(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            tbl.VectorIndexes is { Count: > 0 },
            "CreateTable accepted VectorIndexes but DescribeTable reports none - the vector indexes "
            + "were silently dropped");
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
            $"table/vector indexes never became ACTIVE: {Py.Repr(tbl.TableStatus?.Value)} "
            + $"{Py.Repr((tbl.VectorIndexes ?? []).Select(v => (v.IndexName, v.IndexStatus?.ToString())).ToList())}");
    }
}
