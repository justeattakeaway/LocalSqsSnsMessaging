// Translated from behavioral/dynamodb/check_update_item_many_clause_combo_updated_new_omits_out_of_bounds_list_append.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-many-clause-combo-updated-new-omits-out-of-bounds-list-append</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemManyClauseComboUpdatedNewOmitsOutOfBoundsListAppend
{
    private const string Check = "update-item-many-clause-combo-updated-new-omits-out-of-bounds-list-append";

    private const string Expr =
        "SET a[0] = a[0] + :inc, a[9] = :append ADD info.visits :one REMOVE info.tmp DELETE tags :rm";

    private static Dictionary<string, AttributeValue> Values() => Item(
        (":inc", N("5")),
        (":append", N("100")),
        (":one", N("1")),
        (":rm", SS("alpha")));

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    private static async Task<Dictionary<string, AttributeValue>> Seed(IAmazonDynamoDB ddb, string table, string pk)
    {
        await ddb.PutItemAsync(table, Item(
            ("pk", S(pk)),
            ("a", L(N("1"), N("2"))),
            ("info", M(("visits", N("5")), ("tmp", S("old")))),
            ("tags", SS("alpha", "beta", "gamma"))));
        return Item(("pk", S(pk)));
    }

    /// <summary>
    /// One UpdateExpression combining SET on in-bounds and out-of-bounds list indices, ADD on a
    /// nested map number, REMOVE of a nested map key and DELETE from a string set applies every
    /// clause; UPDATED_NEW omits the out-of-bounds append entirely, even though it is stored.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-many-clause-combo-updated-new-omits-out-of-bounds-list-append")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-combo-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> Final(string pk) => Item(
            ("pk", S(pk)),
            ("a", L(N("6"), N("2"), N("100"))),
            ("info", M(("visits", N("6")))),
            ("tags", SS("beta", "gamma")));

        // ALL_NEW over the whole combo proves every clause actually applied: a[0] += 5 and a[9]
        // (past the end) appends 100 at index 2; info.visits += 1 and info.tmp is gone; "alpha"
        // left the string set.
        var key1 = await Seed(ddb, table, "combo-all-new");
        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key1,
            UpdateExpression = Expr,
            ExpressionAttributeValues = Values(),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        var want = Final("combo-all-new");
        Ensure(Py.Equal(got, want), $"ALL_NEW after the many-clause combo returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key1, ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, want), $"stored item after the many-clause combo is {Py.Repr(item)}, expected {Py.Repr(want)}");

        // The identical combo against an identical fresh item, read back with UPDATED_NEW instead:
        // the in-bounds a[0] SET shows up as a one-element list holding only its own new value,
        // the out-of-bounds a[9] append is not in "a" at all, REMOVE never appears, and ADD/DELETE
        // show their usual new leaves.
        var key2 = await Seed(ddb, table, "combo-updated-new");
        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key2,
            UpdateExpression = Expr,
            ExpressionAttributeValues = Values(),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        var wantUpdatedNew = Item(
            ("a", L(N("6"))),
            ("info", M(("visits", N("6")))),
            ("tags", SS("beta", "gamma")));
        Ensure(
            Py.Equal(got, wantUpdatedNew),
            $"UPDATED_NEW after the many-clause combo returned {Py.Repr(got)}, expected {Py.Repr(wantUpdatedNew)} " +
            "(the out-of-bounds append must be absent from 'a')");

        // The out-of-bounds append is nonetheless persisted: a later read shows the full three-element
        // list, matching the ALL_NEW case exactly.
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key2, ConsistentRead = true })).Item;
        var wantFinal = Final("combo-updated-new");
        Ensure(
            Py.Equal(final, wantFinal),
            $"stored item after the UPDATED_NEW combo call is {Py.Repr(final)}, expected {Py.Repr(wantFinal)}");
    }
}
