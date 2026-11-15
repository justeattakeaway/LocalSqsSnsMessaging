// Translated from behavioral/dynamodb/check_partiql_update_returning_modified_images_on_nested_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-returning-modified-images-on-nested-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateReturningModifiedImagesOnNestedPaths
{
    private const string CheckId = "partiql-update-returning-modified-images-on-nested-paths";

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>
    /// RETURNING MODIFIED NEW * / MODIFIED OLD * on an UPDATE SETting a nested map path and a
    /// list index return only the attribute whose addressed element existed on that side; a SET
    /// past a list's end appends the element but is absent from both, even alone.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-returning-modified-images-on-nested-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbret-");
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

        await ddb.PutItemAsync(table, Item(
            ("pk", S("r1")),
            ("m", M(("a", S("orig_a")))),
            ("l", L(S("x0"), S("x1"))),
            ("untouched", S("same"))));

        async Task<List<Dictionary<string, AttributeValue>>> Execute(string statement, params AttributeValue[] parameters) =>
            (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = [.. parameters] })).Items;

        // 1. SET m.a (in-range) + SET l[5] (append past end, len=2) RETURNING MODIFIED NEW *: only
        // the nested map path is reported, since the list clause addressed an element that did not
        // exist on the new side's counterpart check below; untouched and the whole l are excluded.
        var r1 = await Execute($"UPDATE \"{table}\" SET m.a = ? SET l[5] = ? WHERE pk = ? RETURNING MODIFIED NEW *", S("new_a"), S("appended5"), S("r1"));
        Ensure(
            Py.Equal(r1, new[] { Item(("m", M(("a", S("new_a"))))) }),
            $"RETURNING MODIFIED NEW * after SET m.a + SET l[5] (append) returned {Py.Repr(r1)}");
        var stored1 = (await Stored(ddb, table, "r1"))!;
        Ensure(
            Py.Equal(stored1["l"], L(S("x0"), S("x1"), S("appended5"))),
            $"the append must still have happened: l is {Py.Repr(stored1.GetValueOrDefault("l"))}");
        Ensure(Py.Equal(stored1["untouched"], S("same")), "an unrelated attribute must survive untouched");

        // 2. SET l[1] (in-range, replacing an existing element) RETURNING MODIFIED OLD * returns the
        // single pre-update element, wrapped as a one-element list, not the whole old list.
        var r2 = await Execute($"UPDATE \"{table}\" SET l[1] = ? WHERE pk = ? RETURNING MODIFIED OLD *", S("replaced1"), S("r1"));
        Ensure(
            Py.Equal(r2, new[] { Item(("l", L(S("x1")))) }),
            $"RETURNING MODIFIED OLD * after an in-range SET l[1] returned {Py.Repr(r2)}, expected the old x1");
        var stored2 = (await Stored(ddb, table, "r1"))!;
        Ensure(
            Py.Equal(stored2["l"], L(S("x0"), S("replaced1"), S("appended5"))),
            $"stored list after SET l[1] is {Py.Repr(stored2.GetValueOrDefault("l"))}");

        // 3. SET l[10] alone (append far past the end, len=3) RETURNING MODIFIED OLD *: the clause
        // addressed nothing that existed, so Items is empty — not a one-item answer with an empty map.
        var r3 = await Execute($"UPDATE \"{table}\" SET l[10] = ? WHERE pk = ? RETURNING MODIFIED OLD *", S("farappend"), S("r1"));
        Ensure(r3.Count == 0, $"RETURNING MODIFIED OLD * for an append-only SET returned {Py.Repr(r3)}, expected []");
        var stored3 = (await Stored(ddb, table, "r1"))!;
        Ensure(
            Py.Equal(stored3["l"], L(S("x0"), S("replaced1"), S("appended5"), S("farappend"))),
            $"the append must still land at the end: l is {Py.Repr(stored3.GetValueOrDefault("l"))}");

        // 4. The same shape with MODIFIED NEW * instead of MODIFIED OLD *: still empty, even though
        // the new value plainly exists once stored.
        var r4 = await Execute($"UPDATE \"{table}\" SET l[10] = ? WHERE pk = ? RETURNING MODIFIED NEW *", S("farappend2"), S("r1"));
        Ensure(r4.Count == 0, $"RETURNING MODIFIED NEW * for an append-only SET returned {Py.Repr(r4)}, expected []");
        var stored4 = (await Stored(ddb, table, "r1"))!;
        Ensure(
            Py.Equal(stored4["l"], L(S("x0"), S("replaced1"), S("appended5"), S("farappend"), S("farappend2"))),
            $"the second append must also land at the end: l is {Py.Repr(stored4.GetValueOrDefault("l"))}");
    }
}
