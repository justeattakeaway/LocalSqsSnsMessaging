// Translated from behavioral/dynamodb/check_condition_nested_document_paths_that_hold_apply_the_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-nested-document-paths-that-hold-apply-the-write</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class ConditionNestedDocumentPathsThatHoldApplyTheWrite
{
    private const string CondFailed = "The conditional request failed";

    private static readonly Dictionary<string, string> NamesMap = Names(
        ("#m", "m"), ("#k", "k"), ("#n", "n"), ("#mm", "mm"), ("#l", "l"), ("#z", "z"), ("#u", "u"),
        ("#t", "t"), ("#M", "M"), ("#L", "L"), ("#S", "S"), ("#zero", "0"), ("#g", "g"));

    private static readonly string[] Holds =
    [
        "attribute_exists(#m.#k)",
        "attribute_not_exists(#m.#z)",
        "#m.#k = :inner",
        "begins_with(#m.#k, :in)",
        "contains(#m.#k, :ner)",
        "attribute_type(#m.#k, :S)",
        "attribute_type(#m.#mm, :M)",
        "#m.#mm.#k = :deep",
        "#l[0].#k = :first",
        "#m.#n[1] = :b",
        "#l[1] = :s",
        "attribute_exists(#m.#k) AND #m.#mm.#k = :deep AND #l[0].#k = :first",
        "NOT attribute_exists(#m.#mm.#z)",
        "#t.#M = :tm",
        "#t.#L = :tl",
        "#t.#S = :ts",
        "#t.#zero = :t0",
        "#g[2][1] = :g21",
    ];

    private static Dictionary<string, AttributeValue> ValuesMap() => Item(
        (":inner", S("inner")), (":deep", S("deep")), (":first", S("first")), (":b", S("b")),
        (":in", S("in")), (":ner", S("ner")), (":S", S("S")), (":M", S("M")), (":s", S("s")),
        (":tm", S("tm")), (":tl", S("tl")), (":ts", S("ts")), (":t0", S("t0")),
        (":g21", S("g21")));

    private static Dictionary<string, AttributeValue> Doc() => Item(
        ("m", M(
            ("k", S("inner")),
            ("n", L(N("10"), S("b"))),
            ("mm", M(("k", S("deep")))))),
        ("l", L(M(("k", S("first"))), S("s"))),
        ("t", M(("M", S("tm")), ("L", S("tl")), ("S", S("ts")), ("0", S("t0")))),
        ("g", L(S("g0"), S("g1"), L(S("g20"), S("g21")))));

    /// <summary>
    /// UpdateItem and DeleteItem conditions on map members, list elements, nested lists and members
    /// named like type tags or digits that hold apply the write; a near-miss path fails the condition.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.Attributes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-nested-document-paths-that-hold-apply-the-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-nested-hold-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("nested")), ("sk", S("item")));
        Dictionary<string, AttributeValue> KeyAndDoc(params (string Name, AttributeValue Value)[] extra)
        {
            var item = Key();
            foreach (var (name, value) in Doc().Select(kv => (kv.Key, kv.Value)).Concat(extra))
            {
                item[name] = value;
            }

            return item;
        }

        await ddb.PutItemAsync(table, KeyAndDoc());

        for (var i = 0; i < Holds.Length; i++)
        {
            var condition = Holds[i];
            var (names, values) = Maps("SET #u = :x", condition);
            values ??= [];
            values[":x"] = S($"u{i.ToString(CultureInfo.InvariantCulture)}");
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #u = :x",
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            var want = KeyAndDoc(("u", S($"u{i.ToString(CultureInfo.InvariantCulture)}")));
            Ensure(Py.Equal(item, want), $"UpdateItem under holding condition {Py.Repr(condition)} left {Py.Repr(item)}");
        }

        const string nearMiss = "#m.#mm.#k = :inner";
        var (missNames, missValues) = Maps("SET #u = :x", nearMiss);
        missValues![":x"] = S("miss");
        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #u = :x",
            ConditionExpression = nearMiss,
            ExpressionAttributeNames = missNames,
            ExpressionAttributeValues = missValues,
        }));
        Ensure(
            (code, msg) == ("ConditionalCheckFailedException", CondFailed),
            $"UpdateItem under failing condition {Py.Repr(nearMiss)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        var wantAfter = KeyAndDoc(("u", S($"u{(Holds.Length - 1).ToString(CultureInfo.InvariantCulture)}")));
        Ensure(Py.Equal(after, wantAfter), $"UpdateItem under failing condition {Py.Repr(nearMiss)} left {Py.Repr(after)}");

        const string deleteCondition = "#m.#mm.#k = :deep AND #l[0].#k = :first AND #g[2][1] = :g21";
        var (deleteNames, deleteValues) = Maps(deleteCondition);
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = deleteCondition,
            ExpressionAttributeNames = deleteNames,
            ExpressionAttributeValues = deleteValues,
        });
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(gone is null, $"DeleteItem under holding condition {Py.Repr(deleteCondition)} left {Py.Repr(gone)}");
    }

    /// <summary>The EAN/EAV entries the given expressions reference.</summary>
    private static (Dictionary<string, string> Names, Dictionary<string, AttributeValue>? Values) Maps(params string[] exprs)
    {
        var tokens = TokenPattern().Matches(string.Join(" ", exprs)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var names = NamesMap.Where(kv => tokens.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var values = ValuesMap().Where(kv => tokens.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return (names, values.Count > 0 ? values : null);
    }

    [GeneratedRegex("[#:][A-Za-z0-9_]+")]
    private static partial Regex TokenPattern();

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null ? (error.Code, error.Message) : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
