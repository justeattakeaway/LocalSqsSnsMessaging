// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_include_projection_hides_unprojected_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-include-projection-hides-unprojected-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecIncludeProjectionHidesUnprojectedAttributes
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-include-projection-hides-unprojected-attributes";
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// An INCLUDE-projected vector index returns only keys, schema attributes and NonKeyAttributes.
    ///
    /// A ProjectionExpression naming an unprojected stored attribute silently omits it rather than
    /// reading it from the base table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-include-projection-hides-unprojected-attributes")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("tier", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx-incl",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = 4,
                    DistanceFunction = "EUCLIDEAN",
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["note"] },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "tier", SearchSchemaElementType = SearchSchemaElementType.INLINE_FILTER }],
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var tbl = await WaitVectorTableActive(ddb, table);
        var projection = tbl.VectorIndexes[0].Projection;
        var proj = projection is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ProjectionType"] = projection.ProjectionType?.Value,
            ["NonKeyAttributes"] = projection.NonKeyAttributes,
        };
        Ensure(
            Py.Equal(proj, new Dictionary<string, object?> { ["ProjectionType"] = "INCLUDE", ["NonKeyAttributes"] = new List<string> { "note" } }),
            $"index projection described back as {Py.Repr(proj)}");

        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S($"k{i}")),
                ("tier", S("gold")),
                ("note", S($"note-{i}")),
                ("secret", S($"secret-{i}")),
                ("emb", new AttributeValue { L = Vec([i, 0, 0, 0]) })));
        }

        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("k0"))), ConsistentRead = true })).Item;
        Ensure(
            stored.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "tier", "note", "secret", "emb"]),
            $"stored item is {Py.Repr(stored)}");

        List<SearchResultItem> res = [];
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "idx-incl",
                TopK = 10,
                SearchVector = Vec(Query),
            })).SearchResults ?? [];
            if (res.Count == 3)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            res.Select(r => r.Item["pk"].S).SequenceEqual(["k0", "k1", "k2"]),
            $"search returned {Py.Repr(res.Select(r => r.Item.GetValueOrDefault("pk")).ToList())}");
        foreach (var r in res)
        {
            var i = r.Item["pk"].S[1..];
            Ensure(
                r.Item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "tier", "note"]),
                $"INCLUDE[note] index returned attributes {Py.Repr(r.Item.Keys.Order(StringComparer.Ordinal).ToList())}; expected pk, tier, note");
            Ensure(Py.Equal(r.Item["note"], S($"note-{i}")), $"included note was {Py.Repr(r.Item["note"])}");
        }

        var widened = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
        {
            TableName = table,
            IndexName = "idx-incl",
            TopK = 10,
            SearchVector = Vec(Query),
            ProjectionExpression = "secret, note",
        })).SearchResults ?? [];
        Ensure(widened.Count == 3, $"projected search returned {widened.Count} items, expected 3");
        foreach (var r in widened)
        {
            Ensure(
                r.Item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["note"]),
                $"ProjectionExpression \"secret, note\" on an INCLUDE[note] index returned "
                + $"{Py.Repr(r.Item.Keys.Order(StringComparer.Ordinal).ToList())}; an unprojected attribute must not be retrievable");
        }
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
