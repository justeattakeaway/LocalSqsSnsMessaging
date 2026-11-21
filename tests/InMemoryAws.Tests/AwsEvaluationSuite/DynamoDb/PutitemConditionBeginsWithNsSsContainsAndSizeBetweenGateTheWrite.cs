// Translated from behavioral/dynamodb/check_putitem_condition_begins_with_ns_ss_contains_and_size_between_gate_the_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-begins-with-ns-ss-contains-and-size-between-gate-the-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionBeginsWithNsSsContainsAndSizeBetweenGateTheWrite
{
    private const string Check = "putitem-condition-begins-with-ns-ss-contains-and-size-between-gate-the-write";

    private static readonly (string Code, string Message) CondFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A PutItem ConditionExpression of begins_with on a string, contains() on an NS or SS set,
    /// or size() BETWEEN bounds is refused with ConditionalCheckFailedException, item unchanged,
    /// when the function evaluates false, and applies the write, item replaced, when it is true.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-begins-with-ns-ss-contains-and-size-between-gate-the-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-putcond-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Task<PutItemResponse> Put(Dictionary<string, AttributeValue> item, string condition, Dictionary<string, string> names, Dictionary<string, AttributeValue> values) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });

        // --- begins_with on a string attribute ---
        var bwKey = Item(("pk", S("begins-with-target")));
        var bwInitial = Item(("pk", S("begins-with-target")), ("code", S("orders-2024")));
        await ddb.PutItemAsync(table, bwInitial);

        await ExpectError(
            () => Put(
                Item(("pk", S("begins-with-target")), ("code", S("orders-2024")), ("written", Bool(true))),
                "begins_with(#c, :p)", Names(("#c", "code")), Item((":p", S("invoices")))),
            CondFailed);
        var unchanged = await Get(bwKey);
        Ensure(Py.Equal(unchanged, bwInitial), $"PutItem under a failing begins_with() condition left {Py.Repr(unchanged)}, expected {Py.Repr(bwInitial)} unchanged");

        await Put(
            Item(("pk", S("begins-with-target")), ("code", S("orders-2024")), ("written", Bool(true))),
            "begins_with(#c, :p)", Names(("#c", "code")), Item((":p", S("orders"))));
        var replaced = await Get(bwKey);
        var bwExpected = Item(("pk", S("begins-with-target")), ("code", S("orders-2024")), ("written", Bool(true)));
        Ensure(Py.Equal(replaced, bwExpected), $"PutItem under a holding begins_with() condition left {Py.Repr(replaced)}, expected {Py.Repr(bwExpected)}");

        // --- contains() on a number set (NS) ---
        var nsKey = Item(("pk", S("ns-contains-target")));
        var nsInitial = Item(("pk", S("ns-contains-target")), ("codes", NS("10", "25", "40")));
        await ddb.PutItemAsync(table, nsInitial);

        await ExpectError(
            () => Put(
                Item(("pk", S("ns-contains-target")), ("codes", NS("10", "25", "40")), ("written", Bool(true))),
                "contains(#n, :v)", Names(("#n", "codes")), Item((":v", N("99")))),
            CondFailed);
        unchanged = await Get(nsKey);
        Ensure(
            Py.Equal(UnorderedSets(unchanged), UnorderedSets(nsInitial)),
            $"PutItem under a failing NS contains() condition left {Py.Repr(unchanged)}, expected {Py.Repr(nsInitial)} unchanged");

        await Put(
            Item(("pk", S("ns-contains-target")), ("codes", NS("10", "25", "40")), ("written", Bool(true))),
            "contains(#n, :v)", Names(("#n", "codes")), Item((":v", N("25"))));
        replaced = await Get(nsKey);
        var nsExpected = Item(("pk", S("ns-contains-target")), ("codes", NS("10", "25", "40")), ("written", Bool(true)));
        Ensure(
            Py.Equal(UnorderedSets(replaced), UnorderedSets(nsExpected)),
            $"PutItem under a holding NS contains() condition left {Py.Repr(replaced)}, expected {Py.Repr(nsExpected)}");

        // --- contains() on a string set (SS) ---
        var ssKey = Item(("pk", S("ss-contains-target")));
        var ssInitial = Item(("pk", S("ss-contains-target")), ("tags", SS("alpha", "beta")));
        await ddb.PutItemAsync(table, ssInitial);

        await ExpectError(
            () => Put(
                Item(("pk", S("ss-contains-target")), ("tags", SS("alpha", "beta")), ("written", Bool(true))),
                "contains(#t, :v)", Names(("#t", "tags")), Item((":v", S("gamma")))),
            CondFailed);
        unchanged = await Get(ssKey);
        Ensure(
            Py.Equal(UnorderedSets(unchanged), UnorderedSets(ssInitial)),
            $"PutItem under a failing SS contains() condition left {Py.Repr(unchanged)}, expected {Py.Repr(ssInitial)} unchanged");

        await Put(
            Item(("pk", S("ss-contains-target")), ("tags", SS("alpha", "beta")), ("written", Bool(true))),
            "contains(#t, :v)", Names(("#t", "tags")), Item((":v", S("beta"))));
        replaced = await Get(ssKey);
        var ssExpected = Item(("pk", S("ss-contains-target")), ("tags", SS("alpha", "beta")), ("written", Bool(true)));
        Ensure(
            Py.Equal(UnorderedSets(replaced), UnorderedSets(ssExpected)),
            $"PutItem under a holding SS contains() condition left {Py.Repr(replaced)}, expected {Py.Repr(ssExpected)}");

        // --- size(string) BETWEEN bounds ---
        var szKey = Item(("pk", S("size-between-target")));
        var szInitial = Item(("pk", S("size-between-target")), ("label", S("midsize")));
        await ddb.PutItemAsync(table, szInitial);

        await ExpectError(
            () => Put(
                Item(("pk", S("size-between-target")), ("label", S("midsize")), ("written", Bool(true))),
                "size(#l) BETWEEN :lo AND :hi", Names(("#l", "label")), Item((":lo", N("10")), (":hi", N("20")))),
            CondFailed);
        unchanged = await Get(szKey);
        Ensure(Py.Equal(unchanged, szInitial), $"PutItem under a failing size() BETWEEN condition left {Py.Repr(unchanged)}, expected {Py.Repr(szInitial)} unchanged");

        await Put(
            Item(("pk", S("size-between-target")), ("label", S("midsize")), ("written", Bool(true))),
            "size(#l) BETWEEN :lo AND :hi", Names(("#l", "label")), Item((":lo", N("3")), (":hi", N("10"))));
        replaced = await Get(szKey);
        var szExpected = Item(("pk", S("size-between-target")), ("label", S("midsize")), ("written", Bool(true)));
        Ensure(Py.Equal(replaced, szExpected), $"PutItem under a holding size() BETWEEN condition left {Py.Repr(replaced)}, expected {Py.Repr(szExpected)}");
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
