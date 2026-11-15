// Translated from behavioral/dynamodb/check_put_item_vector_hash_search_schema_missing_vs_empty_string.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-vector-hash-search-schema-missing-vs-empty-string</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemVectorHashSearchSchemaMissingVsEmptyString
{
    private const string CheckId = "put-item-vector-hash-search-schema-missing-vs-empty-string";
    private const int Dimensions = 4;

    private static readonly (string Code, string Message) EmptyHashKey = (
        "ValidationException",
        "One or more parameter values are not valid. A value specified for a secondary index key "
        + "is not supported. The AttributeValue for a key attribute cannot contain an empty string "
        + "value. IndexName: idx, IndexKey: tenant");

    private static readonly double[] Zeros = [0.0, 0.0, 0.0, 0.0];

    /// <summary><c>[{"N": repr(float(v))} for v in values]</c>.</summary>
    private static List<AttributeValue> Vec(double[] values) => values.Select(v => N(PyFloat(v))).ToList();

    private static string PyFloat(double v)
    {
        var text = v.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    /// <summary>
    /// A PutItem omitting a vector index's HASH SearchSchema attribute is accepted but never
    /// returned by a SearchVectors scoped to any hash value; the same attribute set to an empty
    /// string is a ValidationException naming the index and the key, and leaves no item behind.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "Vector indexes are not emulated: CreateTable ignores VectorIndexes, so the SearchSchema attribute in AttributeDefinitions is refused as unused")]
    [DisplayName("dynamodb.put-item-vector-hash-search-schema-missing-vs-empty-string")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvechash-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("tenant", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = Dimensions,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "tenant", SearchSchemaElementType = "HASH" }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 240.0);
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        while (!(tbl.TableStatus?.ToString() == "ACTIVE" && tbl.VectorIndexes is { Count: > 0 }
                 && tbl.VectorIndexes[0].IndexStatus?.ToString() == "ACTIVE"))
        {
            Ensure(clock.Elapsed.TotalSeconds < deadline, $"table/vector index never became ACTIVE: {Py.Repr(tbl.TableName)} {Py.Repr(tbl.TableStatus)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            tbl = (await ddb.DescribeTableAsync(table)).Table;
        }

        // Accepted: an item with no value at all for the HASH search-schema attribute.
        await ddb.PutItemAsync(table, Item(("pk", S("no-tenant")), ("emb", new AttributeValue { L = Vec(Zeros) })));
        var written = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("no-tenant"))), ConsistentRead = true });
        Ensure(written.Item is not null, "the accepted write with no hash attribute left no item behind");
        Ensure(!written.Item.ContainsKey("tenant"), $"the item unexpectedly carries a tenant attribute: {Py.Repr(written.Item)}");

        // Two tenants, each with a vector at the query's own position, to search each hash value.
        await ddb.PutItemAsync(table, Item(("pk", S("tenant-a")), ("tenant", S("a")), ("emb", new AttributeValue { L = Vec(Zeros) })));
        await ddb.PutItemAsync(table, Item(("pk", S("tenant-b")), ("tenant", S("b")), ("emb", new AttributeValue { L = Vec(Zeros) })));

        async Task<List<string>> Search(string tenant)
        {
            var searchClock = FidelityTime.StartNew();
            var searchDeadline = BudgetS(ctx, 90.0);
            while (true)
            {
                var results = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
                {
                    TableName = table,
                    IndexName = "idx",
                    TopK = 10,
                    SearchVector = Vec(Zeros),
                    SearchConditionExpression = "tenant = :t",
                    ExpressionAttributeValues = Item((":t", S(tenant))),
                })).SearchResults ?? [];
                var pks = results.Select(r => r.Item["pk"].S).ToList();
                if (pks.Contains($"tenant-{tenant}"))
                {
                    return pks;
                }

                Ensure(
                    searchClock.Elapsed.TotalSeconds < searchDeadline,
                    $"the tenant-{tenant} vector never became searchable under tenant={Py.Repr(tenant)}");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }

        var forA = await Search("a");
        Ensure(forA.SequenceEqual(["tenant-a"]), $"search scoped to tenant=a returned {Py.Repr(forA)}");
        Ensure(!forA.Contains("no-tenant"), "an item with no hash attribute matched a scoped search");

        var forB = await Search("b");
        Ensure(forB.SequenceEqual(["tenant-b"]), $"search scoped to tenant=b returned {Py.Repr(forB)}");
        Ensure(!forB.Contains("no-tenant"), "an item with no hash attribute matched a scoped search");

        // Rejected: the same attribute, present as an empty string, naming the index and the key.
        await ExpectError(
            async () => await ddb.PutItemAsync(table, Item(("pk", S("empty-tenant")), ("tenant", S("")), ("emb", new AttributeValue { L = Vec(Zeros) }))),
            EmptyHashKey);
        var absent = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("empty-tenant"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the rejected write left an item behind: {Py.Repr(absent.Item)}");

        // Unchanged state: tenant a still answers exactly as before the refusal.
        var againA = await Search("a");
        Ensure(againA.SequenceEqual(["tenant-a"]), $"search scoped to tenant=a after the refusal returned {Py.Repr(againA)}");
    }
}
