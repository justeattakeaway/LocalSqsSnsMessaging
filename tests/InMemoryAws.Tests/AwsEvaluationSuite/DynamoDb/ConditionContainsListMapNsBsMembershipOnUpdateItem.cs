// Translated from behavioral/dynamodb/check_condition_contains_list_map_ns_bs_membership_on_update_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-contains-list-map-ns-bs-membership-on-update-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionContainsListMapNsBsMembershipOnUpdateItem
{
    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    private static readonly Dictionary<string, string> NamesByPath = Names(
        ("#tags", "tags"), ("#members", "members"), ("#nums", "nums"), ("#blobs", "blobs"),
        ("#codes", "codes"), ("#u", "u"));

    /// <summary>
    /// contains() in an UpdateItem ConditionExpression tests List element, NS, BS and SS member
    /// equality, never substring or partial-map matching, across List, List-of-Map, NS, BS and SS.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-contains-list-map-ns-bs-membership-on-update-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-contains-mix-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item")));
        var baseItem = Item(
            ("pk", S("item")),
            ("tags", L(S("administrator"), S("viewer"))),
            ("members", L(M(("name", S("admin")), ("role", S("owner"))), M(("name", S("viewer"))))),
            ("nums", NS("10", "20", "30")),
            ("blobs", BS([0x01], [0x02])),
            ("codes", SS("alpha-admin", "beta")));
        await ddb.PutItemAsync(table, baseItem);

        // The item with each set attribute as a frozenset, so equality ignores member order.
        async Task<Dictionary<string, object?>> Read()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            var normalized = item.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
            foreach (var setAttr in new[] { "nums", "blobs", "codes" })
            {
                normalized[setAttr] = FrozenSet(item[setAttr]);
            }

            return normalized;
        }

        Task<UpdateItemResponse> Update(string path, AttributeValue value, string mark) =>
            ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #u = :mark",
                ConditionExpression = $"contains({path}, :v)",
                ExpressionAttributeNames = Names(("#u", "u"), (path, NamesByPath[path])),
                ExpressionAttributeValues = Item((":v", value), (":mark", S(mark))),
            });

        var baseline = await Read();

        // --- List: a substring of an element is not an element; exact element match is ---
        await ExpectError(() => Update("#tags", S("admin"), "list-substring"), CondFailed);
        var got = await Read();
        Ensure(Py.Equal(got, baseline), $"UpdateItem under a failing list-substring contains() left {Py.Repr(got)}");

        await Update("#tags", S("administrator"), "list-exact");
        got = await Read();
        Ensure(
            Py.Equal(got.GetValueOrDefault("u"), S("list-exact")),
            $"UpdateItem under a holding list-element contains() left u={Py.Repr(got.GetValueOrDefault("u"))}");
        var marked = new Dictionary<string, object?>(baseline, StringComparer.Ordinal) { ["u"] = S("list-exact") };

        // --- List-of-Map: a map missing one key of an element is not that element; the whole,
        // exactly matching map is ---
        await ExpectError(() => Update("#members", M(("name", S("admin"))), "map-partial"), CondFailed);
        got = await Read();
        Ensure(Py.Equal(got, marked), $"UpdateItem under a failing partial-map contains() left {Py.Repr(got)}");

        await Update("#members", M(("name", S("admin")), ("role", S("owner"))), "map-exact");
        got = await Read();
        Ensure(
            Py.Equal(got.GetValueOrDefault("u"), S("map-exact")),
            $"UpdateItem under a holding whole-map-element contains() left u={Py.Repr(got.GetValueOrDefault("u"))}");
        marked = new Dictionary<string, object?>(marked, StringComparer.Ordinal) { ["u"] = S("map-exact") };

        // --- NS: contains() matches a Number Set member by exact value ---
        await Update("#nums", N("20"), "ns-member");
        got = await Read();
        Ensure(
            Py.Equal(got.GetValueOrDefault("u"), S("ns-member")),
            $"UpdateItem under a holding NS-member contains() left u={Py.Repr(got.GetValueOrDefault("u"))}");
        marked = new Dictionary<string, object?>(marked, StringComparer.Ordinal) { ["u"] = S("ns-member") };

        // --- BS: contains() matches a Binary Set member by exact value ---
        await Update("#blobs", B([0x02]), "bs-member");
        got = await Read();
        Ensure(
            Py.Equal(got.GetValueOrDefault("u"), S("bs-member")),
            $"UpdateItem under a holding BS-member contains() left u={Py.Repr(got.GetValueOrDefault("u"))}");
        marked = new Dictionary<string, object?>(marked, StringComparer.Ordinal) { ["u"] = S("bs-member") };

        // --- SS: a substring of a member is not a member; the exact member is ---
        await ExpectError(() => Update("#codes", S("admin"), "ss-substring"), CondFailed);
        got = await Read();
        Ensure(Py.Equal(got, marked), $"UpdateItem under a failing SS-substring contains() left {Py.Repr(got)}");

        await Update("#codes", S("alpha-admin"), "ss-exact");
        got = await Read();
        Ensure(
            Py.Equal(got.GetValueOrDefault("u"), S("ss-exact")),
            $"UpdateItem under a holding SS-member contains() left u={Py.Repr(got.GetValueOrDefault("u"))}");
    }

    /// <summary><c>{type: frozenset(members)}</c>: the members in a fixed order, so equality ignores theirs.</summary>
    private static Dictionary<string, object?> FrozenSet(AttributeValue value) => value.BS is { } bs
        ? new() { ["BS"] = bs.Select(b => b.ToArray()).DistinctBy(Convert.ToHexString).OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList() }
        : new() { [value.NS is not null ? "NS" : "SS"] = (value.NS ?? value.SS).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };

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
