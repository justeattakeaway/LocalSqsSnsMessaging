// Translated from behavioral/dynamodb/check_put_item_condition_three_level_path_list_index_and_nested_comparison_hold.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-three-level-path-list-index-and-nested-comparison-hold</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionThreeLevelPathListIndexAndNestedComparisonHold
{
    private const string Check = "put-item-condition-three-level-path-list-index-and-nested-comparison-hold";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// PutItem's ConditionExpression evaluates a three-level nested map path, a list index
    /// then an aliased map child, a top-level list element and a nested numeric comparison
    /// against the existing item; each holding form lets the write apply.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-three-level-path-list-index-and-nested-comparison-hold")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-cond3-");
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("ddb-put-cond3")));
        Dictionary<string, AttributeValue> ItemWithSeq(string seq) => Item(
            ("pk", S("ddb-put-cond3")),
            ("m", M(
                ("mm", M(("k", S("deep")))),
                ("n", L(N("42"), N("7"))))),
            ("l", L(M(("k", S("first"))), S("second"))),
            ("seq", N(seq)));
        await ddb.PutItemAsync(table, ItemWithSeq("0"));

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        async Task PutHolding(int seq, string condExpr, Dictionary<string, string>? names, Dictionary<string, AttributeValue>? values)
        {
            var item = ItemWithSeq(seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = condExpr,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });
            var got = await Read();
            Ensure(Py.Equal(got, item), $"PutItem under holding condition {Py.Repr(condExpr)} left {Py.Repr(got)}, expected {Py.Repr(item)}");
        }

        // Three-level nested map path equality: m -> mm -> k.
        await PutHolding(1, "#m.#mm.#k = :deep", Names(("#m", "m"), ("#mm", "mm"), ("#k", "k")), Item((":deep", S("deep"))));

        // List index then an aliased map child: l[0] is a map, then its "k" member.
        await PutHolding(2, "#l[0].#k = :first", Names(("#l", "l"), ("#k", "k")), Item((":first", S("first"))));

        // Top-level list index element equality against a scalar.
        await PutHolding(3, "#l[1] = :second", Names(("#l", "l")), Item((":second", S("second"))));

        // Nested number comparison: m.n[0] (42) >= 40.
        await PutHolding(4, "#m.#n[0] >= :threshold", Names(("#m", "m"), ("#n", "n")), Item((":threshold", N("40"))));

        // A near-miss: the nested numeric comparison does not hold against the stored value.
        var before = await Read();
        var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = ItemWithSeq("9"),
            ConditionExpression = "#m.#n[0] >= :toohigh",
            ExpressionAttributeNames = Names(("#m", "m"), ("#n", "n")),
            ExpressionAttributeValues = Item((":toohigh", N("100"))),
        }));
        Ensure((code, msg) == Ccf, $"PutItem under a failing nested comparison gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var after = await Read();
        Ensure(Py.Equal(after, before), $"PutItem under a failing nested comparison left {Py.Repr(after)}, expected {Py.Repr(before)}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>The (code, message) of the ClientError <c>fn()</c> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
