// Translated from behavioral/dynamodb/check_update_item_nested_arithmetic_mixed_clauses_and_if_not_exists_parent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-nested-arithmetic-mixed-clauses-and-if-not-exists-parent</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemNestedArithmeticMixedClausesAndIfNotExistsParent
{
    private const string CheckId = "update-item-nested-arithmetic-mixed-clauses-and-if-not-exists-parent";

    /// <summary>
    /// SET and ADD on list and map paths apply together in one call, several nested SETs
    /// addressed with one placeholder per path segment touch only their own leaves, and SET on
    /// if_not_exists(path, {}) creates an absent parent map a later call sets a leaf under.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-nested-arithmetic-mixed-clauses-and-if-not-exists-parent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-nestarith-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("doc")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("doc")),
            ("a", L(N("10"), N("20"), N("30"))),
            ("info", M(("visits", N("100"))))));

        async Task<Dictionary<string, AttributeValue>> Update(
            string expr, Dictionary<string, AttributeValue> values, ReturnValue rv, Dictionary<string, string>? names = null)
        {
            var request = new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = expr,
                ExpressionAttributeValues = values,
                ReturnValues = rv,
            };
            if (names is not null)
            {
                request.ExpressionAttributeNames = names;
            }

            return (await ddb.UpdateItemAsync(request)).Attributes;
        }

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // SET on a list index evaluates a parenthesised arithmetic operand: a[0] = (a[0] + :x)
        // with a[0]=10, :x=5 computes to 15.
        var got = await Update("SET a[0] = (a[0] + :x)", Item((":x", N("5"))), ReturnValue.UPDATED_NEW);
        var want = Item(("a", L(N("15"))));
        Ensure(Py.Equal(got, want), $"SET a[0] = (a[0] + :x) with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        var item = await Read();
        Ensure(Py.Equal(item["a"], L(N("15"), N("20"), N("30"))), $"after SET a[0] = (a[0] + :x) the list is {Py.Repr(item.GetValueOrDefault("a"))}");
        Ensure(Py.Equal(item["info"], M(("visits", N("100")))), $"info must be untouched: {Py.Repr(item.GetValueOrDefault("info"))}");

        // SET on a list index and ADD on a map path apply together inside one UpdateExpression;
        // UPDATED_NEW lists only the two touched leaves.
        got = await Update(
            "SET a[1] = a[1] + :inc ADD info.visits :one",
            Item((":inc", N("5")), (":one", N("1"))),
            ReturnValue.UPDATED_NEW);
        want = Item(("a", L(N("25"))), ("info", M(("visits", N("101")))));
        Ensure(
            Py.Equal(got, want),
            $"SET a[1] = a[1] + :inc ADD info.visits :one with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        item = await Read();
        Ensure(Py.Equal(item["a"], L(N("15"), N("25"), N("30"))), $"after the mixed SET/ADD call the list is {Py.Repr(item.GetValueOrDefault("a"))}");
        Ensure(Py.Equal(item["info"], M(("visits", N("101")))), $"after the mixed SET/ADD call info is {Py.Repr(item.GetValueOrDefault("info"))}");

        // Two nested SETs in one call, each addressed with its own placeholder per path segment
        // (#i for the shared parent, #r and #p for each leaf): only the two new leaves are returned.
        got = await Update(
            "SET #i.#r = :r, #i.#p = :p",
            Item((":r", N("4.5")), (":p", S("A quiet thriller."))),
            ReturnValue.UPDATED_NEW,
            names: Names(("#i", "info"), ("#r", "rating"), ("#p", "plot")));
        want = Item(("info", M(("rating", N("4.5")), ("plot", S("A quiet thriller.")))));
        Ensure(Py.Equal(got, want), $"SET #i.#r = :r, #i.#p = :p with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        item = await Read();
        Ensure(
            Py.Equal(item["info"], M(("visits", N("101")), ("rating", N("4.5")), ("plot", S("A quiet thriller.")))),
            $"after the two per-segment-aliased nested SETs info is {Py.Repr(item.GetValueOrDefault("info"))}");

        // SET meta = if_not_exists(meta, :empty) creates the absent top-level map "meta".
        got = await Update("SET meta = if_not_exists(meta, :empty)", Item((":empty", M())), ReturnValue.UPDATED_NEW);
        want = Item(("meta", M()));
        Ensure(Py.Equal(got, want), $"SET meta = if_not_exists(meta, :empty) with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        item = await Read();
        Ensure(Py.Equal(item["meta"], M()), $"after creating the parent map, meta is {Py.Repr(item.GetValueOrDefault("meta"))}");

        // A later call sets a nested leaf under the map the previous call created.
        got = await Update("SET meta.flag = :v", Item((":v", Bool(true))), ReturnValue.UPDATED_NEW);
        want = Item(("meta", M(("flag", Bool(true)))));
        Ensure(Py.Equal(got, want), $"SET meta.flag = :v with UPDATED_NEW returned {Py.Repr(got)}, expected {Py.Repr(want)}");

        var final = await Read();
        var expectedFinal = Item(
            ("pk", S("doc")),
            ("a", L(N("15"), N("25"), N("30"))),
            ("info", M(("visits", N("101")), ("rating", N("4.5")), ("plot", S("A quiet thriller.")))),
            ("meta", M(("flag", Bool(true)))));
        Ensure(Py.Equal(final, expectedFinal), $"final item is {Py.Repr(final)}, expected {Py.Repr(expectedFinal)}");
    }

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }
}
