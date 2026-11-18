// Translated from behavioral/dynamodb/check_partiql_update_and_delete_returning_images.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-and-delete-returning-images</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateAndDeleteReturningImages
{
    private const string CheckId = "partiql-update-and-delete-returning-images";

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>Python's <c>{**item, k: v, ...}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] entries)
    {
        var copy = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            copy[name] = value;
        }

        return copy;
    }

    /// <summary>
    /// UPDATE RETURNING ALL OLD *, MODIFIED NEW * and ALL NEW *, and DELETE RETURNING ALL OLD *, each
    /// return the documented image of the item, and a consistent read matches the state each write left.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-and-delete-returning-images")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlret-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        Task<ExecuteStatementResponse> Execute(string statement) =>
            ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement });

        var key = Item(("pk", S("p1")), ("sk", S("s1")));
        var original = With(key, ("name", S("Ada")), ("qty", N("3")), ("tags", L(S("x"), N("7"))));
        await ddb.PutItemAsync(table, original);
        const string where = "WHERE pk = 'p1' AND sk = 's1'";

        // 1. ALL OLD *: the whole item as it was before the write, without the attribute it adds.
        var r1 = await Execute($"UPDATE \"{table}\" SET qty = 4 SET note = 'n1' {where} RETURNING ALL OLD *");
        Ensure(
            Py.Equal(r1.Items, new[] { original }),
            $"RETURNING ALL OLD * must return the complete pre-update item and nothing else.\nwant: {Py.Repr(new[] { original })}\ngot:  {Py.Repr(r1.Items)}");
        var after1 = With(original, ("qty", N("4")), ("note", S("n1")));
        Ensure(
            Py.Equal(await Stored(ddb, table, key), after1),
            $"after SET qty = 4 SET note = n1 the stored item must be {Py.Repr(after1)}, got {Py.Repr(await Stored(ddb, table, key))}");

        // 2. MODIFIED NEW *: only the attributes this statement set, at their new values.
        var r2 = await Execute($"UPDATE \"{table}\" SET qty = 5 SET name = 'Bea' {where} RETURNING MODIFIED NEW *");
        var want2 = new[] { Item(("qty", N("5")), ("name", S("Bea"))) };
        Ensure(
            Py.Equal(r2.Items, want2),
            $"RETURNING MODIFIED NEW * must return exactly the two set attributes at their new values (no key, no untouched attribute).\nwant: {Py.Repr(want2)}\ngot:  {Py.Repr(r2.Items)}");
        var after2 = With(after1, ("qty", N("5")), ("name", S("Bea")));
        Ensure(
            Py.Equal(await Stored(ddb, table, key), after2),
            $"after SET qty = 5 SET name = Bea the stored item must be {Py.Repr(after2)}, got {Py.Repr(await Stored(ddb, table, key))}");

        // 3. ALL NEW *: the whole post-update item, which no longer holds the removed attribute.
        var r3 = await Execute($"UPDATE \"{table}\" REMOVE note {where} RETURNING ALL NEW *");
        var after3 = after2.Where(e => e.Key != "note").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(r3.Items, new[] { after3 }),
            $"RETURNING ALL NEW * after REMOVE note must return the complete post-update item.\nwant: {Py.Repr(new[] { after3 })}\ngot:  {Py.Repr(r3.Items)}");
        Ensure(
            Py.Equal(await Stored(ddb, table, key), after3),
            $"after REMOVE note the stored item must be {Py.Repr(after3)}, got {Py.Repr(await Stored(ddb, table, key))}");

        // 4. DELETE ... RETURNING ALL OLD *: the removed item, whole; then the key reads absent.
        var r4 = await Execute($"DELETE FROM \"{table}\" {where} RETURNING ALL OLD *");
        Ensure(
            Py.Equal(r4.Items, new[] { after3 }),
            $"DELETE ... RETURNING ALL OLD * must return the complete item it removed.\nwant: {Py.Repr(new[] { after3 })}\ngot:  {Py.Repr(r4.Items)}");
        var gone = await Stored(ddb, table, key);
        Ensure(gone is null, $"the deleted item must be absent from a consistent GetItem, got {Py.Repr(gone)}");

        // 5. A second DELETE of the same key succeeds and returns no image: there was nothing to delete.
        var r5 = await Execute($"DELETE FROM \"{table}\" {where} RETURNING ALL OLD *");
        Ensure(r5.Items.Count == 0, $"DELETE of an absent key with RETURNING ALL OLD * returns no items, got {Py.Repr(r5.Items)}");

        // 6-9: MODIFIED NEW/OLD narrow to the exact nested leaf touched, not the whole top-level
        // attribute, when the SET target is a nested map path or a list index.
        var key2 = Item(("pk", S("p2")), ("sk", S("s2")));
        const string where2 = "WHERE pk = 'p2' AND sk = 's2'";
        var nestedOriginal = With(
            key2,
            ("meta", M(("nested", S("v0")), ("other", N("1")))),
            ("tags", L(S("a"), S("b"), S("c"))));
        await ddb.PutItemAsync(table, nestedOriginal);

        // 6. MODIFIED OLD * over a nested map path: only the touched leaf, not the sibling attribute.
        var r6 = await Execute($"UPDATE \"{table}\" SET meta.nested = 'v1' {where2} RETURNING MODIFIED OLD *");
        var want6 = new[] { Item(("meta", M(("nested", S("v0"))))) };
        Ensure(
            Py.Equal(r6.Items, want6),
            $"RETURNING MODIFIED OLD * over a nested map path must return only the pre-update leaf, nested under its top-level attribute, with no sibling.\nwant: {Py.Repr(want6)}\ngot:  {Py.Repr(r6.Items)}");

        // 7. MODIFIED NEW * over the same nested map path: only the touched leaf at its new value.
        var r7 = await Execute($"UPDATE \"{table}\" SET meta.nested = 'v2' {where2} RETURNING MODIFIED NEW *");
        var want7 = new[] { Item(("meta", M(("nested", S("v2"))))) };
        Ensure(
            Py.Equal(r7.Items, want7),
            $"RETURNING MODIFIED NEW * over a nested map path must return only the post-update leaf, nested under its top-level attribute, with no sibling.\nwant: {Py.Repr(want7)}\ngot:  {Py.Repr(r7.Items)}");

        // 8. MODIFIED OLD * over a list index: only the touched element, not the sibling elements.
        var r8 = await Execute($"UPDATE \"{table}\" SET tags[0] = 'z' {where2} RETURNING MODIFIED OLD *");
        var want8 = new[] { Item(("tags", L(S("a")))) };
        Ensure(
            Py.Equal(r8.Items, want8),
            $"RETURNING MODIFIED OLD * over a list index must return only the pre-update element, as a single-element list under its top-level attribute.\nwant: {Py.Repr(want8)}\ngot:  {Py.Repr(r8.Items)}");

        // 9. MODIFIED NEW * over the same list index: only the touched element at its new value.
        var r9 = await Execute($"UPDATE \"{table}\" SET tags[0] = 'y' {where2} RETURNING MODIFIED NEW *");
        var want9 = new[] { Item(("tags", L(S("y")))) };
        Ensure(
            Py.Equal(r9.Items, want9),
            $"RETURNING MODIFIED NEW * over a list index must return only the post-update element, as a single-element list under its top-level attribute.\nwant: {Py.Repr(want9)}\ngot:  {Py.Repr(r9.Items)}");
        var stored2 = await Stored(ddb, table, key2);
        var wantStored2 = With(
            key2,
            ("meta", M(("nested", S("v2")), ("other", N("1")))),
            ("tags", L(S("y"), S("b"), S("c"))));
        Ensure(
            Py.Equal(stored2, wantStored2),
            $"after the nested-path and list-index SETs the stored item must be {Py.Repr(wantStored2)}, got {Py.Repr(stored2)}");
    }
}
