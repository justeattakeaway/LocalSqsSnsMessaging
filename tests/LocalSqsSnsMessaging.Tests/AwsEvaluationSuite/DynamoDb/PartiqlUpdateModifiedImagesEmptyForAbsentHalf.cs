// Translated from behavioral/dynamodb/check_partiql_update_modified_images_empty_for_absent_half.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-modified-images-empty-for-absent-half</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateModifiedImagesEmptyForAbsentHalf
{
    private const string CheckId = "partiql-update-modified-images-empty-for-absent-half";

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("s1"))), ConsistentRead = true })).Item;

    /// <summary>Python's <c>{**item, k: v}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, string name, AttributeValue value) =>
        new(item, StringComparer.Ordinal) { [name] = value };

    /// <summary>
    /// UPDATE ... RETURNING MODIFIED NEW * after a REMOVE carries no items, because a removed
    /// attribute has no new value; RETURNING MODIFIED OLD * after SET on an attribute that did not
    /// exist carries no items, because it has no old value. Each has a present-value counterpart.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-modified-images-empty-for-absent-half")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlmod-");
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

        var original = Item(("pk", S("p1")), ("sk", S("s1")), ("name", S("Ada")), ("qty", N("3")));
        await ddb.PutItemAsync(table, original);
        const string where = "WHERE pk = 'p1' AND sk = 's1'";

        // 1. REMOVE qty, RETURNING MODIFIED NEW *: the removed attribute has no new value, so the
        // statement carries no items even though it changed the item.
        var r1 = await Execute($"UPDATE \"{table}\" REMOVE qty {where} RETURNING MODIFIED NEW *");
        Ensure(r1.Items.Count == 0, $"RETURNING MODIFIED NEW * after REMOVE qty must carry no items, got {Py.Repr(r1.Items)}");
        var after1 = original.Where(e => e.Key != "qty").ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(await Stored(ddb, table), after1),
            $"after REMOVE qty the stored item must be {Py.Repr(after1)}, got {Py.Repr(await Stored(ddb, table))}");

        // 2. SET qty back (now a brand-new attribute, since it was just removed), RETURNING
        // MODIFIED OLD *: a brand-new attribute has no old value, so the statement carries no items.
        var r2 = await Execute($"UPDATE \"{table}\" SET qty = 5 {where} RETURNING MODIFIED OLD *");
        Ensure(r2.Items.Count == 0, $"RETURNING MODIFIED OLD * after SET on a brand-new attribute must carry no items, got {Py.Repr(r2.Items)}");
        var after2 = With(after1, "qty", N("5"));
        Ensure(
            Py.Equal(await Stored(ddb, table), after2),
            $"after SET qty = 5 the stored item must be {Py.Repr(after2)}, got {Py.Repr(await Stored(ddb, table))}");

        // 3. Positive control: SET on the now-existing qty, RETURNING MODIFIED OLD * carries the
        // one attribute's prior value.
        var r3 = await Execute($"UPDATE \"{table}\" SET qty = 9 {where} RETURNING MODIFIED OLD *");
        var want3 = new[] { Item(("qty", N("5"))) };
        Ensure(
            Py.Equal(r3.Items, want3),
            $"RETURNING MODIFIED OLD * on an attribute that already existed must carry its prior value {Py.Repr(want3)}, got {Py.Repr(r3.Items)}");
        var after3 = With(after2, "qty", N("9"));
        Ensure(
            Py.Equal(await Stored(ddb, table), after3),
            $"after SET qty = 9 the stored item must be {Py.Repr(after3)}, got {Py.Repr(await Stored(ddb, table))}");

        // 4. Positive control: SET on a brand-new attribute, RETURNING MODIFIED NEW * carries that
        // attribute's new value even though it did not exist before.
        var r4 = await Execute($"UPDATE \"{table}\" SET status = 'active' {where} RETURNING MODIFIED NEW *");
        var want4 = new[] { Item(("status", S("active"))) };
        Ensure(
            Py.Equal(r4.Items, want4),
            $"RETURNING MODIFIED NEW * on SET of a brand-new attribute must carry its new value {Py.Repr(want4)}, got {Py.Repr(r4.Items)}");
        var after4 = With(after3, "status", S("active"));
        Ensure(
            Py.Equal(await Stored(ddb, table), after4),
            $"after SET status = active the stored item must be {Py.Repr(after4)}, got {Py.Repr(await Stored(ddb, table))}");
    }
}
