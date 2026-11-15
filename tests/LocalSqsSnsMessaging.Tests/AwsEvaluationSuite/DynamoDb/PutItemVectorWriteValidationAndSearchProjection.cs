// Translated from behavioral/dynamodb/check_put_item_vector_write_validation_and_search_projection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-vector-write-validation-and-search-projection</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemVectorWriteValidationAndSearchProjection
{
    private const string CheckId = "put-item-vector-write-validation-and-search-projection";
    private const int Dimensions = 4;
    private static readonly double[] Unit = [1.0, 0.0, 0.0, 0.0];

    /// <summary><c>[{'N': repr(float(v))} for v in values]</c>.</summary>
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

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }

    private static List<string> SortedKeys(Dictionary<string, AttributeValue> item) => item.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A non-numeric list element, a non-list vector attribute, or a SearchSchema element naming
    /// an undeclared attribute is a ValidationException that leaves no item or table behind.
    /// SearchVectors omits the vector attribute from a result unless ProjectionExpression names it.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "Vector indexes are not emulated: CreateTable ignores VectorIndexes, so the SearchSchema attribute in AttributeDefinitions is refused as unused")]
    [DisplayName("dynamodb.put-item-vector-write-validation-and-search-projection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvecwrite-");

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
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = Dimensions,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "tier", SearchSchemaElementType = "INLINE_FILTER" }],
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

        // A list element that is not a number.
        var (code, message) = await Error(async () => await ddb.PutItemAsync(table, Item(
            ("pk", S("bad-element")), ("tier", S("gold")),
            ("emb", L(N("1"), S("x"), N("2"), N("3"))))));
        Ensure(code == "ValidationException", $"non-numeric list element gave {Py.Repr(code)}");
        var expected = "One or more parameter values were invalid. Invalid type for parameter emb[1], "
            + "Expected: 32-bit floating point number, Actual: S. IndexName: idx";
        Ensure(message == expected, $"non-numeric list element message was {Py.Repr(message)}");
        var absent = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bad-element"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the rejected write left an item behind: {Py.Repr(absent.Item)}");

        // A vector attribute that is not a list.
        (code, message) = await Error(async () => await ddb.PutItemAsync(table, Item(
            ("pk", S("not-a-list")), ("tier", S("gold")), ("emb", N("1.0")))));
        Ensure(code == "ValidationException", $"non-list vector attribute gave {Py.Repr(code)}");
        expected = "One or more parameter values were invalid. Invalid type for parameter emb, "
            + "Expected: 32-bit floating point number list IndexName: idx";
        Ensure(message == expected, $"non-list vector attribute message was {Py.Repr(message)}");
        absent = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("not-a-list"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the rejected write left an item behind: {Py.Repr(absent.Item)}");

        // A SearchSchema element naming an attribute never declared in AttributeDefinitions (the
        // empty string is never declared, same as any other undeclared name).
        var badSchema = FidelityContext.UniqueName("sim2real-behav-ddbvecwrite-badschema-");
        (code, message) = await Error(async () => await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = badSchema,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            VectorIndexes =
            [
                new VectorIndex
                {
                    IndexName = "idx",
                    VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                    Dimensions = Dimensions,
                    DistanceFunction = "COSINE",
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    SearchSchema = [new SearchSchemaElement { AttributeName = "", SearchSchemaElementType = "INLINE_FILTER" }],
                },
            ],
        }));
        Ensure(code == "ValidationException", $"empty-string SearchSchema key gave {Py.Repr(code)}");
        expected = "One or more parameter values were invalid: One element in SearchSchema is not "
            + "defined in attribute definitions";
        Ensure(message == expected, $"empty-string SearchSchema key message was {Py.Repr(message)}");
        var (notFoundCode, _) = await Error(async () => await ddb.DescribeTableAsync(badSchema));
        Ensure(
            notFoundCode == "ResourceNotFoundException",
            $"CreateTable was refused but DescribeTable returned {Py.Repr(notFoundCode)} for {badSchema}");

        // Positive control, and the projection claim: a valid write is searchable, and the vector
        // attribute is absent from the result unless ProjectionExpression names it.
        await ddb.PutItemAsync(table, Item(("pk", S("good")), ("tier", S("gold")), ("emb", new AttributeValue { L = Vec(Unit) })));

        async Task<List<SearchResultItem>> Search(string? projectionExpression = null)
        {
            var searchClock = FidelityTime.StartNew();
            var searchDeadline = BudgetS(ctx, 90.0);
            while (true)
            {
                var request = new SearchVectorsRequest { TableName = table, IndexName = "idx", TopK = 5, SearchVector = Vec(Unit) };
                if (projectionExpression is not null)
                {
                    request.ProjectionExpression = projectionExpression;
                }

                var results = (await ddb.SearchVectorsAsync(request)).SearchResults ?? [];
                if (results.Count > 0)
                {
                    return results;
                }

                Ensure(searchClock.Elapsed.TotalSeconds < searchDeadline, "the written vector never became searchable");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        }

        var @default = await Search();
        Ensure(
            @default.Select(r => r.Item["pk"].S).SequenceEqual(["good"]),
            $"expected only the written item back, got {Py.Repr(@default.Select(r => r.Item).ToList())}");
        Ensure(
            SortedKeys(@default[0].Item).SequenceEqual(["pk", "tier"]),
            $"a default SearchVectors projection included the vector attribute: {Py.Repr(SortedKeys(@default[0].Item))}");

        var projected = await Search(projectionExpression: "emb, pk");
        Ensure(
            SortedKeys(projected[0].Item).SequenceEqual(["emb", "pk"]),
            $"ProjectionExpression naming the vector attribute did not return it: {Py.Repr(SortedKeys(projected[0].Item))}");
        Ensure(
            Py.Equal(projected[0].Item["emb"], new AttributeValue { L = Vec(Unit) }),
            $"the projected vector attribute did not round-trip: {Py.Repr(projected[0].Item["emb"])}");
    }
}
