// Translated from behavioral/dynamodb/check_partiql_select_projects_nested_map_and_list_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-projects-nested-map-and-list-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectProjectsNestedMapAndListPaths
{
    private const string CheckId = "partiql-select-projects-nested-map-and-list-paths";

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly succeeded where an error was required: {Py.Repr(resp)}");
    }

    /// <summary>
    /// PartiQL SELECT of a nested map path, a list index and a list index inside a map returns
    /// each leaf keyed by its last path segment, or one attribute-less item when every path is
    /// absent; two selected paths sharing a last segment are refused.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-projects-nested-map-and-list-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbsel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        var statement = $"SELECT m.a.b, l[1], m.l[0].x FROM \"{table}\" WHERE pk = ?";

        // 1. Every named path present: each returns under its own last segment, nothing else.
        await ddb.PutItemAsync(table, Item(
            ("pk", S("full")),
            ("m", M(
                ("a", M(("b", S("ab-val")))),
                ("l", L(M(("x", S("mlx0")), ("b", S("mlb0"))))))),
            ("l", L(N("100"), N("200")))));
        var full = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [S("full")], ConsistentRead = true })).Items;
        var wantFull = new[] { Item(("b", S("ab-val")), ("l[1]", N("200")), ("x", S("mlx0"))) };
        Ensure(Py.Equal(full, wantFull), $"SELECT over a fully-populated item returned {Py.Repr(full)}, expected {Py.Repr(wantFull)}");

        // 2. None of the named paths present (the top-level attributes don't exist at all): one item,
        // no attributes — never an error, never an empty result set.
        await ddb.PutItemAsync(table, Item(("pk", S("missing")), ("keep", S("k"))));
        var missing = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [S("missing")], ConsistentRead = true })).Items;
        Ensure(Py.Equal(missing, new[] { Item() }), $"SELECT with every path absent returned {Py.Repr(missing)}, expected [{{}}]");

        // 3. A mix: the map path exists, the list and the nested list do not.
        await ddb.PutItemAsync(table, Item(("pk", S("partial")), ("m", M(("a", M(("b", S("partial-b"))))))));
        var partial = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [S("partial")], ConsistentRead = true })).Items;
        Ensure(Py.Equal(partial, new[] { Item(("b", S("partial-b"))) }), $"SELECT with only m.a.b present returned {Py.Repr(partial)}, expected only b");

        // 4. Two selected paths that share a last segment are rejected outright, not silently merged.
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT m.a.b, m.l[0].b FROM \"{table}\" WHERE pk = ?",
            Parameters = [S("full")],
            ConsistentRead = true,
        }));
        Ensure(code == "ValidationException", $"duplicate select identifiers answered {Py.Repr(code)}, expected ValidationException");
        Ensure(message == "Duplicate identifiers in select clause: b", $"refusal message was {Py.Repr(message)}");
    }
}
