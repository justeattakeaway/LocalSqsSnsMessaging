// Translated from behavioral/dynamodb/check_create_table_with_vector_index_is_described_back.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-with-vector-index-is-described-back</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableWithVectorIndexIsDescribedBack
{
    /// <summary>
    /// CreateTable with VectorIndexes is reflected by DescribeTable with the dimensions, the
    /// distance function and the vector attribute it was created with.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "CreateTable accepts VectorIndexes but drops them: DescribeTable reports none")]
    [DisplayName("dynamodb.create-table-with-vector-index-is-described-back")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var account = ctx.Account;
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
                    DistanceFunction = "COSINE",
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
            "CreateTable accepted VectorIndexes but DescribeTable reports none, so the vector index "
            + "was silently dropped");
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(120) && described[0].IndexStatus?.ToString() != "ACTIVE")
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            described = (await ddb.DescribeTableAsync(table)).Table.VectorIndexes ?? [];
            Ensure(described.Count > 0, "the vector index disappeared while it was backfilling");
        }

        Ensure(
            described[0].IndexStatus?.ToString() == "ACTIVE",
            $"the vector index never became ACTIVE: {Py.Repr(described[0].IndexStatus?.ToString())}");
        var idx = described[0];
        Ensure(idx.IndexName == "cos", $"expected IndexName cos, got {idx.IndexName}");
        Ensure(idx.Dimensions == 3, $"expected Dimensions 3, got {idx.Dimensions}");
        Ensure(idx.DistanceFunction?.ToString() == "COSINE", $"expected COSINE, got {idx.DistanceFunction}");
        Ensure(
            idx.VectorAttribute?.AttributeName == "embedding",
            $"expected the embedding attribute, got {Py.Repr(Attr(idx.VectorAttribute))}");
        Ensure(
            Py.Equal(Attr(idx.VectorAttribute), new Dictionary<string, object?> { ["AttributeName"] = "embedding" }),
            $"expected VectorAttribute to hold only AttributeName, got {Py.Repr(Attr(idx.VectorAttribute))}");
        var projection = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (idx.Projection?.ProjectionType is not null)
        {
            projection["ProjectionType"] = idx.Projection.ProjectionType.Value;
        }

        if (idx.Projection?.NonKeyAttributes is not null)
        {
            projection["NonKeyAttributes"] = idx.Projection.NonKeyAttributes;
        }

        Ensure(
            Py.Equal(projection, new Dictionary<string, object?> { ["ProjectionType"] = "ALL" }),
            $"expected Projection {{'ProjectionType': 'ALL'}}, got {Py.Repr(projection)}");
        Ensure(
            idx.SearchSchema is null,
            $"no SearchSchema was configured, but DescribeTable returned one: {Py.Repr(idx.SearchSchema?.Select(e => e.AttributeName).ToList())}");
        Ensure(
            idx.Backfilling is null,
            $"expected no Backfilling field once ACTIVE and backfill complete, got {Py.Repr(idx.Backfilling)}");
        Ensure(idx.IndexSizeBytes == 0, $"expected IndexSizeBytes 0 on an empty index, got {Py.Repr(idx.IndexSizeBytes)}");
        Ensure(idx.ItemCount == 0, $"expected ItemCount 0 on an empty index, got {Py.Repr(idx.ItemCount)}");
        var expectedArn = $"arn:aws:dynamodb:{ctx.Region}:{account}:table/{table}/index/cos";
        Ensure(idx.IndexArn == expectedArn, $"expected IndexArn {Py.Repr(expectedArn)}, got {Py.Repr(idx.IndexArn)}");
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        void Has(string name, object? value)
        {
            if (value is not null)
            {
                fields.Add(name);
            }
        }

        Has("IndexName", idx.IndexName);
        Has("Projection", idx.Projection);
        Has("VectorAttribute", idx.VectorAttribute);
        Has("Dimensions", idx.Dimensions);
        Has("DistanceFunction", idx.DistanceFunction);
        Has("IndexStatus", idx.IndexStatus);
        Has("IndexSizeBytes", idx.IndexSizeBytes);
        Has("ItemCount", idx.ItemCount);
        Has("IndexArn", idx.IndexArn);
        Has("SearchSchema", idx.SearchSchema);
        Has("Backfilling", idx.Backfilling);
        Ensure(
            fields.SetEquals(["IndexName", "Projection", "VectorAttribute", "Dimensions", "DistanceFunction", "IndexStatus", "IndexSizeBytes", "ItemCount", "IndexArn"]),
            $"unexpected VectorIndexDescription fields: {Py.Repr(fields.ToList())}");
    }

    private static Dictionary<string, object?>? Attr(VectorAttributeDefinition? attribute) =>
        attribute is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { ["AttributeName"] = attribute.AttributeName };
}
