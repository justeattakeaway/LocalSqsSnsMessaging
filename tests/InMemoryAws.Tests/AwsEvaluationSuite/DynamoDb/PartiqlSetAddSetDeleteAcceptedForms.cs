// Translated from behavioral/dynamodb/check_partiql_set_add_set_delete_accepted_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-add-set-delete-accepted-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetAddSetDeleteAcceptedForms
{
    private const string CheckId = "partiql-set-add-set-delete-accepted-forms";

    /// <summary>
    /// A Set attribute <c>{'SS': [...]}</c>/<c>{'NS': [...]}</c> as <c>(type, frozenset(members))</c>.
    /// DynamoDB does not document member order for a Set, so every multi-member comparison here
    /// goes through this normal form instead of comparing the raw list.
    /// </summary>
    private static (string Type, SortedSet<string> Members) AsSet(AttributeValue attr)
    {
        var boto = Py.Boto(attr);
        var (vtype, members) = boto.Single();
        return (vtype, new SortedSet<string>(((IEnumerable<string>)members!).ToList(), StringComparer.Ordinal));
    }

    private static bool SetIs(AttributeValue attr, string type, params string[] members)
    {
        var (t, m) = AsSet(attr);
        return t == type && m.SetEquals(members);
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription?> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var left = ctx.TimeLeftSeconds;
        var budgetS = Math.Max(5.0, Math.Min(90.0, left - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetS}s: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>
    /// set_add on an absent attribute creates the set; set_delete on an absent one is a no-op.
    /// SET_ADD is case-insensitive, two SET clauses can each call set_add, and RETURNING ALL NEW *
    /// then returns their union; SET a = &lt;&lt;'x','y'&gt;&gt; assigns a string set from a literal directly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-add-set-delete-accepted-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ctx, ddb, t);

        var key = Item(("pk", S("s1")));
        await ddb.PutItemAsync(t, Item(("pk", S("s1"))));

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // 1. set_add(tags, <<'x'>>) on an attribute that does not yet exist creates the set.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'x'>>) WHERE pk = ?",
            Parameters = [S("s1")],
        });
        var item = await Stored();
        Ensure(
            SetIs(item["tags"], "SS", "x"),
            $"set_add on the absent attribute 'tags' must create {{'SS': ['x']}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // 2. set_delete on an attribute that still does not exist leaves it absent, no-op.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET absentattr = set_delete(absentattr, <<'x'>>) WHERE pk = ?",
            Parameters = [S("s1")],
        });
        item = await Stored();
        Ensure(!item.ContainsKey("absentattr"), $"set_delete on the absent attribute 'absentattr' must leave it absent, got {Py.Repr(item)}");
        Ensure(
            SetIs(item["tags"], "SS", "x"),
            $"the unrelated attribute 'tags' must be unchanged by the set_delete no-op, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // 3. SET_ADD (uppercase) is the same function as set_add: case-insensitive.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET tags = SET_ADD(tags, <<'y'>>) WHERE pk = ?",
            Parameters = [S("s1")],
        });
        item = await Stored();
        Ensure(
            SetIs(item["tags"], "SS", "x", "y"),
            $"SET_ADD (uppercase) must behave as set_add and store {{'SS': ['x','y']}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // 4. Two SET clauses in one statement can each call set_add, and RETURNING ALL NEW * returns
        //    the post-update item with both attributes at their unioned values.
        var resp = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'z'>>) SET nums = set_add(nums, <<5>>) WHERE pk = ? RETURNING ALL NEW *",
            Parameters = [S("s1")],
        });
        Ensure(resp.Items.Count == 1, $"RETURNING ALL NEW * must return exactly one item, got {Py.Repr(resp.Items)}");
        var returned = resp.Items[0];
        Ensure(Py.Equal(returned["pk"], S("s1")), $"returned item must keep the key, got {Py.Repr(returned)}");
        Ensure(
            SetIs(returned["tags"], "SS", "x", "y", "z"),
            $"RETURNING ALL NEW * after two SET clauses must return tags as the union {{'x','y','z'}}, got {Py.Repr(returned.GetValueOrDefault("tags"))}");
        Ensure(
            SetIs(returned["nums"], "NS", "5"),
            $"RETURNING ALL NEW * after two SET clauses must return nums {{'NS': ['5']}}, got {Py.Repr(returned.GetValueOrDefault("nums"))}");
        item = await Stored();
        Ensure(Py.Equal(item["pk"], S("s1")), $"stored item must keep the key, got {Py.Repr(item)}");
        Ensure(
            SetIs(item["tags"], "SS", "x", "y", "z"),
            $"the stored tags after both set_add calls must be the union {{'x','y','z'}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");
        Ensure(
            SetIs(item["nums"], "NS", "5"),
            $"the stored nums after both set_add calls must be {{'NS': ['5']}}, got {Py.Repr(item.GetValueOrDefault("nums"))}");

        // 5. SET a = <<'x','y'>> assigns a literal set directly, creating a String Set with no function.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET lit = <<'x', 'y'>> WHERE pk = ?",
            Parameters = [S("s1")],
        });
        item = await Stored();
        Ensure(
            SetIs(item["lit"], "SS", "x", "y"),
            $"SET lit = <<'x','y'>> must store a String Set {{'SS': ['x','y']}}, got {Py.Repr(item.GetValueOrDefault("lit"))}");
    }
}
