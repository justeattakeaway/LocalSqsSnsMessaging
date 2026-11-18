// Translated from behavioral/dynamodb/check_partiql_update_set_remove_nested_map_and_list_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-set-remove-nested-map-and-list-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateSetRemoveNestedMapAndListPaths
{
    private const string CheckId = "partiql-update-set-remove-nested-map-and-list-paths";

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly succeeded where an error was required: {resp}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>
    /// PartiQL UPDATE SET through a missing parent map is refused and writes nothing; once the
    /// parent exists, SET on a nested path, SET past a list's end (an append), REMOVE in range,
    /// REMOVE out of range (a no-op) and REMOVE on a nested path each leave the exact stored item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-set-remove-nested-map-and-list-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbupd-");
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

        await ddb.PutItemAsync(table, Item(("pk", S("u1")), ("keep", S("k"))));

        // 1. SET through a nested path whose parent map does not exist is refused, and writes nothing.
        var (code, message) = await Error(async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET m.a.b = ? WHERE pk = ?",
            Parameters = [S("x"), S("u1")],
        }));
        Ensure(code == "ValidationException", $"SET through a missing parent map answered {Py.Repr(code)}");
        Ensure(
            message == "The document path provided in the update expression is invalid for update",
            $"refusal message was {Py.Repr(message)}");
        var afterRefusal = await Stored(ddb, table, "u1");
        Ensure(
            Py.Equal(afterRefusal, Item(("pk", S("u1")), ("keep", S("k")))),
            $"a refused SET must not auto-create the intermediate map; item is {Py.Repr(afterRefusal)}");

        // Positive control: once the parent map and the list exist, the identical nested SET succeeds.
        await ddb.PutItemAsync(table, Item(
            ("pk", S("u1")),
            ("keep", S("k")),
            ("m", M(("a", M(("b", S("orig")))))),
            ("l", L(S("z0")))));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET m.a.b = ? WHERE pk = ?",
            Parameters = [S("newb"), S("u1")],
        });
        var stored = (await Stored(ddb, table, "u1"))!;
        Ensure(
            Py.Equal(stored["m"], M(("a", M(("b", S("newb")))))),
            $"SET m.a.b did not update the nested path, stored m is {Py.Repr(stored.GetValueOrDefault("m"))}");
        Ensure(Py.Equal(stored["l"], L(S("z0"))), $"SET m.a.b touched l unexpectedly: {Py.Repr(stored.GetValueOrDefault("l"))}");

        // 2. SET on a list index past the list's end (len=1, index=2) appends rather than erroring
        // or leaving a hole.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET l[2] = ? WHERE pk = ?",
            Parameters = [S("appended"), S("u1")],
        });
        stored = (await Stored(ddb, table, "u1"))!;
        Ensure(
            Py.Equal(stored["l"], L(S("z0"), S("appended"))),
            $"SET l[2] past the end gave {Py.Repr(stored.GetValueOrDefault("l"))}, expected an append to index 1");

        // 3. REMOVE on the now in-range index 1 deletes the appended element.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" REMOVE l[1] WHERE pk = ?", Parameters = [S("u1")] });
        stored = (await Stored(ddb, table, "u1"))!;
        Ensure(Py.Equal(stored["l"], L(S("z0"))), $"REMOVE l[1] gave {Py.Repr(stored.GetValueOrDefault("l"))}, expected [z0]");

        // 4. REMOVE on an out-of-range index (len=1, index=5) is a silent no-op, not an error.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" REMOVE l[5] WHERE pk = ?", Parameters = [S("u1")] });
        stored = (await Stored(ddb, table, "u1"))!;
        Ensure(
            Py.Equal(stored["l"], L(S("z0"))),
            $"REMOVE l[5] out of range changed the list to {Py.Repr(stored.GetValueOrDefault("l"))}; it must be a no-op");

        // 5. REMOVE on a nested map path drops that key, leaving the parent map (empty, not absent).
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" REMOVE m.a WHERE pk = ?", Parameters = [S("u1")] });
        var final = await Stored(ddb, table, "u1");
        Ensure(
            Py.Equal(final, Item(("pk", S("u1")), ("keep", S("k")), ("m", M()), ("l", L(S("z0"))))),
            $"after REMOVE m.a the item is {Py.Repr(final)}, expected an empty map m and l untouched");
    }
}
