// Translated from behavioral/dynamodb/check_partiql_select_gsi_include_projection_nonprojected_filter_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-gsi-include-projection-nonprojected-filter-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectGsiIncludeProjectionNonprojectedFilterRejected
{
    private const string CheckId = "partiql-select-gsi-include-projection-nonprojected-filter-rejected";
    private const string Index = "gsi1";
    private const string Projected = "proj_attr";
    private const string NotProjected = "not_proj_attr";

    /// <summary>A poll's budget: capS from now, bounded by the runner's remaining time less 5 s.</summary>
    private static TimeSpan Deadline(FidelityContext ctx, double capS) =>
        TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0)));

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL SELECT on a GSI with an index-key equality and a WHERE term on an attribute the
    /// INCLUDE-projection index does not project is a ValidationException; the same statement filtering
    /// a projected attribute narrows the index's own items and leaves them unwritten.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-gsi-include-projection-nonprojected-filter-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlincl-");
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
        }

        Ensure(gsiStatus == "ACTIVE", $"{Index} never became ACTIVE: {Py.Repr(gsiStatus)}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("P1")), ("sk", S("s1")), ("gpk", S("g1")), ("gsk", S("a")),
            (Projected, S("match")), (NotProjected, S("np1"))));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("P2")), ("sk", S("s2")), ("gpk", S("g1")), ("gsk", S("b")),
            (Projected, S("other")), (NotProjected, S("np2"))));

        // Positive control: the GSI partition key equality alone must see BOTH items before the
        // projected-attribute filter below can prove it narrowed the set rather than returning
        // everything by coincidence.
        List<Dictionary<string, AttributeValue>> both = [];
        deadline = Deadline(ctx, 60.0);
        clock = FidelityTime.StartNew();
        while (clock.Elapsed < deadline)
        {
            both = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1'",
            })).Items ?? [];
            if (both.Count == 2)
            {
                break;
            }
        }

        Ensure(both.Count == 2, $"the index must hold both items before the filter can narrow them: {Py.Repr(both)}");

        // A WHERE term on an attribute the INCLUDE projection does not project is rejected, not
        // silently ignored or served from the base table.
        var (code, message) = await ErrorCode(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND {NotProjected} = 'np1'",
        }));
        Ensure(
            code == "ValidationException",
            $"a WHERE term on a non-projected attribute of an INCLUDE-projection index must be refused with ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(
            message == $"One or more parameter values were invalid: Secondary index {Index} does not project one or more filter attributes: [{NotProjected}]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // The same statement filtering on the PROJECTED attribute instead is legal and narrows the
        // two-item index down to the single matching item.
        var filtered = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"{Index}\" WHERE gpk = 'g1' AND {Projected} = 'match'",
        })).Items ?? [];
        Ensure(
            filtered.Count == 1,
            $"a WHERE term on a projected attribute must narrow the 2-item index to the 1 matching row, got {filtered.Count} items: {Py.Repr(filtered)}");
        Ensure(Py.Equal(filtered[0]["pk"], S("P1")), $"expected the surviving item's pk to be P1, got {Py.Repr(filtered[0].GetValueOrDefault("pk"))}");
        Ensure(Py.Equal(filtered[0][Projected], S("match")), $"expected {Projected}=match, got {Py.Repr(filtered[0].GetValueOrDefault(Projected))}");

        // The rejected SELECT is read-only: both base-table items are exactly as written, proving
        // the refusal above had no side effect to undo.
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("P1")), ("sk", S("s1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(kept, Item(
                ("pk", S("P1")), ("sk", S("s1")), ("gpk", S("g1")), ("gsk", S("a")),
                (Projected, S("match")), (NotProjected, S("np1")))),
            $"the rejected SELECT must not have written anything; item now reads {Py.Repr(kept)}");
    }
}
