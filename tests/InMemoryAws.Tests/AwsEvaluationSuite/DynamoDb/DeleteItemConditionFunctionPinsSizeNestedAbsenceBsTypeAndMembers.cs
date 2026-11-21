// Translated from behavioral/dynamodb/check_delete_item_condition_function_pins_size_nested_absence_bs_type_and_members.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-function-pins-size-nested-absence-bs-type-and-members</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionFunctionPinsSizeNestedAbsenceBsTypeAndMembers
{
    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A DeleteItem ConditionExpression gates the delete on the stored value of size() BETWEEN
    /// bounds and size() = length on a String, attribute_not_exists() on an absent nested map child,
    /// attribute_type() naming BS, begins_with() with a Binary prefix, and contains() on a Binary Set.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-function-pins-size-nested-absence-bs-type-and-members")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-del-fns-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        async Task<Dictionary<string, AttributeValue>?> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Task<DeleteItemResponse> Delete(Dictionary<string, AttributeValue> key, string condition, (Dictionary<string, string> Names, Dictionary<string, AttributeValue>? Values) namesValues) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = key,
                ConditionExpression = condition,
                ExpressionAttributeNames = namesValues.Names,
                ExpressionAttributeValues = namesValues.Values,
            });

        // Put item, prove condition refuses under badNamesValues, then delete it
        // under namesValues and prove the delete landed.
        async Task Pin(
            Dictionary<string, AttributeValue> key,
            Dictionary<string, AttributeValue> item,
            string condition,
            (Dictionary<string, string> Names, Dictionary<string, AttributeValue>? Values) namesValues,
            (Dictionary<string, string> Names, Dictionary<string, AttributeValue>? Values) badNamesValues)
        {
            await ddb.PutItemAsync(table, item);
            await ExpectError(() => Delete(key, condition, badNamesValues), CondFailed);
            var still = await Get(key);
            Ensure(
                Py.Equal(UnorderedSets(still), UnorderedSets(item)),
                $"DeleteItem refused under {Py.Repr(condition)} left {Py.Repr(still)}, expected {Py.Repr(item)}");

            await Delete(key, condition, namesValues);
            var gone = await Get(key);
            Ensure(gone is null, $"DeleteItem under holding {Py.Repr(condition)} left {Py.Repr(gone)}");
        }

        // --- size(string) BETWEEN bounds holds: "AnyCompany" has length 10. ---
        var key1 = Item(("pk", S("size-between")));
        await Pin(
            key1, Item(("pk", S("size-between")), ("name", S("AnyCompany"))),
            "size(#n) BETWEEN :lo AND :hi",
            (Names(("#n", "name")), Item((":lo", N("5")), (":hi", N("15")))),
            (Names(("#n", "name")), Item((":lo", N("11")), (":hi", N("20")))));

        // --- size(string) equals its length: "Example Corp" has length 12. ---
        var key2 = Item(("pk", S("size-eq")));
        await Pin(
            key2, Item(("pk", S("size-eq")), ("name", S("Example Corp"))),
            "size(#n) = :len",
            (Names(("#n", "name")), Item((":len", N("12")))),
            (Names(("#n", "name")), Item((":len", N("99")))));

        // --- attribute_not_exists() on an absent nested map child holds; the same path refuses when
        // the child is present on a sibling item. ---
        var key3 = Item(("pk", S("nested-absent")));
        var presentItem = Item(("pk", S("nested-absent")), ("m", M(("k", S("present")), ("child", S("here")))));
        await ddb.PutItemAsync(table, presentItem);
        await ExpectError(
            () => Delete(key3, "attribute_not_exists(#m.#c)", (Names(("#m", "m"), ("#c", "child")), null)),
            CondFailed);
        var still = await Get(key3);
        Ensure(Py.Equal(still, presentItem), $"DeleteItem refused under attribute_not_exists left {Py.Repr(still)}");

        var absentItem = Item(("pk", S("nested-absent")), ("m", M(("k", S("present")))));
        await ddb.PutItemAsync(table, absentItem);
        await Delete(key3, "attribute_not_exists(#m.#c)", (Names(("#m", "m"), ("#c", "child")), null));
        var gone = await Get(key3);
        Ensure(gone is null, $"DeleteItem under holding attribute_not_exists(m.child) left {Py.Repr(gone)}");

        // --- attribute_type() naming BS matches a stored Binary Set. ---
        var key4 = Item(("pk", S("attrtype-bs")));
        await Pin(
            key4, Item(("pk", S("attrtype-bs")), ("bs", BS([0x01], [0x02]))),
            "attribute_type(#bs, :t)",
            (Names(("#bs", "bs")), Item((":t", S("BS")))),
            (Names(("#bs", "bs")), Item((":t", S("S")))));

        // --- begins_with() with a Binary prefix on a plain (non-key) attribute. ---
        var key5 = Item(("pk", S("beginswith-b")));
        await Pin(
            key5, Item(("pk", S("beginswith-b")), ("blob", B([0x01, 0x02, 0x03]))),
            "begins_with(#b, :p)",
            (Names(("#b", "blob")), Item((":p", B([0x01, 0x02])))),
            (Names(("#b", "blob")), Item((":p", B([0x09])))));

        // --- contains() testing a Binary Set member. ---
        var key6 = Item(("pk", S("contains-bs")));
        await Pin(
            key6, Item(("pk", S("contains-bs")), ("bs", BS([0xaa], [0xbb]))),
            "contains(#bs, :m)",
            (Names(("#bs", "bs")), Item((":m", B([0xaa])))),
            (Names(("#bs", "bs")), Item((":m", B([0xcc])))));
    }

    /// <summary><paramref name="item"/> with every set member list sorted: DynamoDB sets carry no member order.</summary>
    private static Dictionary<string, object?>? UnorderedSets(Dictionary<string, AttributeValue>? item) =>
        item?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value switch
            {
                { SS: { } ss } => new Dictionary<string, object?> { ["SS"] = ss.Order(StringComparer.Ordinal).ToList() },
                { NS: { } ns } => new Dictionary<string, object?> { ["NS"] = ns.Order(StringComparer.Ordinal).ToList() },
                { BS: { } bs } => new Dictionary<string, object?> { ["BS"] = bs.Select(b => b.ToArray()).OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList() },
                var other => (object?)other,
            },
            StringComparer.Ordinal);

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
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

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
