// Translated from behavioral/dynamodb/check_partiql_select_index_unprojected_filter_refusal_scoped_to_key_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-index-unprojected-filter-refusal-scoped-to-key-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectIndexUnprojectedFilterRefusalScopedToKeyCondition
{
    private const string CheckId = "partiql-select-index-unprojected-filter-refusal-scoped-to-key-condition";
    private const string Index = "gsi1";
    private const string Projected = "proj_attr";
    private const string NotProjected = "not_proj_attr";

    private static readonly (string Code, string Message) UnprojectedRefused = (
        "ValidationException",
        $"One or more parameter values were invalid: Secondary index {Index} does not project one or more filter attributes: [{NotProjected}]");

    // Four items under one GSI partition key, each carrying both the projected and the
    // not-projected attribute in the base table -- the index just does not carry the latter.
    private static readonly (string Gsk, string Proj, string NotProj)[] Rows =
    [
        ("a", "match", "np-a"),
        ("b", "other", "np-b"),
        ("c", "other", "np-c"),
        ("d", "other", "np-d"),
    ];

    private static readonly string[] AllPks = ["Pa", "Pb", "Pc", "Pd"];

    /// <summary>A poll's budget: capS from now, bounded by the runner's remaining time less 5 s.</summary>
    private static TimeSpan Deadline(FidelityContext ctx, double capS) =>
        TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0)));

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

    private static List<string> Pks(List<Dictionary<string, AttributeValue>> items) =>
        items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A PartiQL SELECT's unprojected-filter refusal fires only with a key condition on the index
    /// (partition key =, IN, or = with sort-key BETWEEN), for every predicate form including OR
    /// nesting; with no key condition the predicate is accepted and the attribute reads as absent.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-index-unprojected-filter-refusal-scoped-to-key-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlkey-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = [Projected] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        string? gsiStatus = null;
        var deadline = Deadline(ctx, 180.0);
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < deadline)
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            var gsis = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            gsiStatus = gsis.GetValueOrDefault(Index);
            if (gsiStatus == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus == "ACTIVE", $"{Index} never became ACTIVE: {Py.Repr(gsiStatus)}");

        foreach (var (gsk, proj, notProj) in Rows)
        {
            await ddb.PutItemAsync(table, Item(
                ("pk", S($"P{gsk}")), ("sk", S("only")), ("gpk", S("g1")), ("gsk", S(gsk)),
                (Projected, S(proj)), (NotProjected, S(notProj))));
        }

        // Positive control: the index-key equality alone reaches all four rows before any filter
        // narrows or refuses them.
        List<Dictionary<string, AttributeValue>> allRows = [];
        deadline = Deadline(ctx, 60.0);
        clock = FidelityTime.StartNew();
        while (clock.Elapsed < deadline)
        {
            allRows = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1'",
            })).Items ?? [];
            if (allRows.Count == 4)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(Pks(allRows).SequenceEqual(AllPks), $"expected all 4 index rows before filtering, got {Py.Repr(allRows)}");

        Task<ExecuteStatementResponse> Exec(string statement) =>
            ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement });

        // Keyed with `=` on the index partition key: every predicate form on the unprojected
        // attribute is refused, not silently ignored.
        await ExpectError(() => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND {NotProjected} IS MISSING"), UnprojectedRefused);
        await ExpectError(() => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND {NotProjected} IS NOT MISSING"), UnprojectedRefused);
        await ExpectError(() => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND NOT {NotProjected} = 'np-a'"), UnprojectedRefused);
        await ExpectError(() => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND begins_with({NotProjected}, 'np')"), UnprojectedRefused);

        // Nesting the unprojected predicate inside an OR does not exempt a keyed statement: the
        // refusal still fires with the partition-key equality present at the top level.
        await ExpectError(
            () => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND ({NotProjected} = 'np-a' OR {Projected} = 'match')"),
            UnprojectedRefused);

        // Keyed with IN on the index partition key: the refusal still fires.
        await ExpectError(() => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk IN ['g1'] AND {NotProjected} IS MISSING"), UnprojectedRefused);

        // Keyed with BETWEEN on the index sort key, alongside the partition key equality: the
        // refusal still fires.
        await ExpectError(
            () => Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND gsk BETWEEN 'a' AND 'b' AND {NotProjected} IS MISSING"),
            UnprojectedRefused);

        // IS NOT MISSING on the PROJECTED attribute, alongside the same key condition, is accepted:
        // the refusal is specific to the unprojected attribute, not to the predicate form.
        var projectedPresent = (await Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND {Projected} IS NOT MISSING")).Items ?? [];
        Ensure(
            Pks(projectedPresent).SequenceEqual(AllPks),
            $"IS NOT MISSING on the projected attribute must accept all 4 rows, got {Py.Repr(projectedPresent)}");

        // With no key condition at all (a full index scan), the same predicate on the unprojected
        // attribute is accepted, and the attribute reads as absent: NOT x = v is true for every row.
        var scanned = (await Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE NOT {NotProjected} = 'np-a'")).Items ?? [];
        Ensure(
            Pks(scanned).SequenceEqual(AllPks),
            $"unkeyed NOT {NotProjected} = v must be accepted and match every index row (the attribute reads as absent), got {Py.Repr(scanned)}");

        // Two unkeyed filters, one of them on the unprojected attribute: accepted, and the
        // unprojected attribute's absence makes the equality fail for every row, even though the
        // base table item it would otherwise match does carry that exact value.
        var twoFilters = (await Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE {Projected} = 'match' AND {NotProjected} = 'np-a'")).Items ?? [];
        Ensure(
            twoFilters.Count == 0,
            $"the unprojected attribute must read as absent even where the base item carries the matching value, got {Py.Repr(twoFilters)}");

        // An OR with no key condition anywhere in the WHERE clause is accepted, and the branch
        // touching the unprojected attribute reads as absent (false), so only the other branch's
        // row matches.
        var orBranch = (await Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE {NotProjected} = 'np-a' OR {Projected} = 'match'")).Items ?? [];
        Ensure(
            orBranch.Count == 1,
            $"an unkeyed OR touching the unprojected attribute must be accepted, matching only the other branch's row, got {Py.Repr(orBranch)}");
        Ensure(Py.Equal(orBranch[0]["pk"], S("Pa")), $"expected the OR's surviving row to be pk=Pa, got {Py.Repr(orBranch[0].GetValueOrDefault("pk"))}");

        // A sort-key BETWEEN with no partition-key equality is not a key condition on its own: the
        // statement is unkeyed, and the unprojected predicate is accepted and reads as absent (true
        // for every row BETWEEN scans), narrowing the result to the BETWEEN range alone.
        var sortBetweenUnkeyed = (await Exec($"SELECT * FROM \"{table}\".\"{Index}\" WHERE gsk BETWEEN 'a' AND 'b' AND {NotProjected} IS MISSING")).Items ?? [];
        var sortBetweenKeys = Pks(sortBetweenUnkeyed);
        Ensure(
            sortBetweenKeys.SequenceEqual(["Pa", "Pb"]),
            $"a sort-key BETWEEN with no partition-key equality must be accepted as unkeyed, got {Py.Repr(sortBetweenUnkeyed)}");

        // Every refused and accepted SELECT above is read-only: the base-table rows are exactly as
        // written.
        foreach (var (gsk, proj, notProj) in Rows)
        {
            var kept = (await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S($"P{gsk}")), ("sk", S("only"))),
                ConsistentRead = true,
            })).Item;
            Ensure(
                Py.Equal(kept, Item(
                    ("pk", S($"P{gsk}")), ("sk", S("only")), ("gpk", S("g1")), ("gsk", S(gsk)),
                    (Projected, S(proj)), (NotProjected, S(notProj)))),
                $"no SELECT above must have written anything; item now reads {Py.Repr(kept)}");
        }
    }
}
