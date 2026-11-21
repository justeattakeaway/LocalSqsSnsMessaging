// Translated from behavioral/dynamodb/check_partiql_select_lsi_unprojected_filter_scoping_unkeyed_or_between.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-lsi-unprojected-filter-scoping-unkeyed-or-between</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectLsiUnprojectedFilterScopingUnkeyedOrBetween
{
    private const string CheckId = "partiql-select-lsi-unprojected-filter-scoping-unkeyed-or-between";
    private const string Index = "lsi1";
    private const string Projected = "proj_attr";
    private const string NotProjected = "not_proj_attr";

    private static readonly (string Code, string Message) UnprojectedRefused = (
        "ValidationException",
        $"One or more parameter values were invalid: Secondary index {Index} does not project "
        + $"one or more filter attributes: [{NotProjected}]");

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == expected.Message, $"expected error message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static List<string> Sks(List<Dictionary<string, AttributeValue>> items) => [.. items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A PartiQL SELECT on an LSI accepts an unprojected-attribute filter unless the index's
    /// sort key sits in a top-level AND; a key equality inside an OR branch does not count, but a
    /// top-level BETWEEN on the sort key does and the filter is refused.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.select.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-lsi-unprojected-filter-scoping-unkeyed-or-between")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqllsior-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = [Projected] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        // Four rows under one partition key, each carrying both the projected and the not-projected
        // attribute in the base table -- the LSI's INCLUDE projection just does not carry the latter.
        (string Sk, int Lsk, string Proj, string NotProj)[] rows =
        [
            ("s1", 1, "other1", "np-1"),
            ("s2", 2, "other2", "np-2"),
            ("s3", 3, "match3", "np-3"),
            ("s4", 4, "other4", "np-4"),
        ];
        Dictionary<string, AttributeValue> Row((string Sk, int Lsk, string Proj, string NotProj) row) => Item(
            ("pk", S("P")),
            ("sk", S(row.Sk)),
            ("lsk", N(row.Lsk.ToString(CultureInfo.InvariantCulture))),
            (Projected, S(row.Proj)),
            (NotProjected, S(row.NotProj)));
        foreach (var row in rows)
        {
            await ddb.PutItemAsync(table, Row(row));
        }

        async Task<List<Dictionary<string, AttributeValue>>> Select(string statement) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement })).Items ?? [];

        // Positive control: a projected-attribute filter with no key condition reaches the index and
        // narrows correctly, proving the unkeyed scan below is actually evaluating the WHERE clause.
        var scanProjected = await Select($"SELECT * FROM \"{table}\".\"{Index}\" WHERE {Projected} = 'match3'");
        Ensure(
            Sks(scanProjected).SequenceEqual(["s3"]),
            $"unkeyed scan filtering on the projected attribute must match only sk=s3, got {Py.Repr(scanProjected)}");

        // With no key condition at all, a filter on the unprojected attribute is accepted (not
        // refused), and because the index never carries that attribute it reads as absent, so an
        // equality test against it matches nothing.
        var scanUnprojected = await Select($"SELECT * FROM \"{table}\".\"{Index}\" WHERE {NotProjected} = 'np-1'");
        Ensure(
            scanUnprojected.Count == 0,
            "an unkeyed filter on the unprojected attribute must be accepted and match nothing "
            + $"(the attribute reads as absent through the index), got {Py.Repr(scanUnprojected)}");

        // An OR whose branches each carry the index's own key equality is accepted even where one
        // branch also filters on the unprojected attribute: the key equality inside an OR branch is
        // not a top-level AND key condition, so the unprojected filter is scoped as unkeyed rather
        // than refused. The branch pairing the key with the unprojected filter reads that attribute
        // as absent (false); the other branch, pairing the key with the projected attribute, matches
        // normally.
        var orBranches = await Select(
            $"SELECT * FROM \"{table}\".\"{Index}\" WHERE "
            + $"(pk = 'P' AND lsk = 1 AND {NotProjected} = 'np-1') OR "
            + $"(pk = 'P' AND lsk = 3 AND {Projected} = 'match3')");
        Ensure(
            Sks(orBranches).SequenceEqual(["s3"]),
            "an OR with the index key inside each branch must be accepted, matching only the "
            + $"branch whose filter is on the projected attribute, got {Py.Repr(orBranches)}");

        // Positive control: the index's own key equality plus BETWEEN on its sort key, with no
        // filter, answers normally.
        var betweenOnly = await Select($"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' AND lsk BETWEEN 1 AND 3");
        Ensure(
            Sks(betweenOnly).SequenceEqual(["s1", "s2", "s3"]),
            $"pk = and sort-key BETWEEN with no filter must answer with sk s1, s2 and s3, got {Py.Repr(betweenOnly)}");

        // BETWEEN on the index's own sort key, at the top level alongside the partition-key equality,
        // counts as a key condition: a filter on the unprojected attribute is refused, not silently
        // evaluated as absent.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE pk = 'P' "
                    + $"AND lsk BETWEEN 1 AND 3 AND {NotProjected} = 'np-1'",
            }),
            UnprojectedRefused);

        // Every SELECT above, accepted or refused, is read-only: the base-table rows are exactly as
        // written.
        foreach (var row in rows)
        {
            var kept = (await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("P")), ("sk", S(row.Sk))),
                ConsistentRead = true,
            })).Item;
            Ensure(Py.Equal(kept, Row(row)), $"no SELECT above must have written anything; item now reads {Py.Repr(kept)}");
        }
    }
}
