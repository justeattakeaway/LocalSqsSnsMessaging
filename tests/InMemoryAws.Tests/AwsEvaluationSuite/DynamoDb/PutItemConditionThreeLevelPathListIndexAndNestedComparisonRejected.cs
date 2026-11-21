// Translated from behavioral/dynamodb/check_put_item_condition_three_level_path_list_index_and_nested_comparison_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-three-level-path-list-index-and-nested-comparison-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionThreeLevelPathListIndexAndNestedComparisonRejected
{
    private const string Check = "put-item-condition-three-level-path-list-index-and-nested-comparison-rejected";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// PutItem's ConditionExpression evaluating a three-level nested map path, a list index
    /// then an aliased map child, a top-level list element, or a nested numeric comparison to
    /// false against the existing item is ConditionalCheckFailedException and the item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-three-level-path-list-index-and-nested-comparison-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-cond3-neg-");
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

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("ddb-put-cond3-neg")));
        var original = Item(
            ("pk", S("ddb-put-cond3-neg")),
            ("m", M(
                ("mm", M(("k", S("deep")))),
                ("n", L(N("42"), N("7"))))),
            ("l", L(M(("k", S("first"))), S("second"))));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        async Task Rejected(string condExpr, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
        {
            var (code, msg) = await Error(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("ddb-put-cond3-neg")), ("overwritten", Bool(true))),
                ConditionExpression = condExpr,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            }));
            Ensure((code, msg) == Ccf, $"PutItem under {Py.Repr(condExpr)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var got = await Read();
            Ensure(Py.Equal(got, original), $"PutItem under failing {Py.Repr(condExpr)} left {Py.Repr(got)}, expected {Py.Repr(original)}");
        }

        // Three-level nested map path equality against a different value.
        await Rejected("#m.#mm.#k = :other", Names(("#m", "m"), ("#mm", "mm"), ("#k", "k")), Item((":other", S("other"))));

        // List index then an aliased map child, compared against a different value.
        await Rejected("#l[0].#k = :other", Names(("#l", "l"), ("#k", "k")), Item((":other", S("other"))));

        // Top-level list index element against a different scalar.
        await Rejected("#l[1] = :other", Names(("#l", "l")), Item((":other", S("other"))));

        // Nested number comparison that does not hold: m.n[0] (42) is not >= 100.
        await Rejected("#m.#n[0] >= :toohigh", Names(("#m", "m"), ("#n", "n")), Item((":toohigh", N("100"))));

        // Positive control: the same mechanism applies the write once the nested condition holds.
        var replacement = Item(("pk", S("ddb-put-cond3-neg")), ("replaced", Bool(true)));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = replacement,
            ConditionExpression = "#m.#mm.#k = :deep",
            ExpressionAttributeNames = Names(("#m", "m"), ("#mm", "mm"), ("#k", "k")),
            ExpressionAttributeValues = Item((":deep", S("deep"))),
        });
        var got = await Read();
        Ensure(Py.Equal(got, replacement), $"the holding control left {Py.Repr(got)}, expected {Py.Repr(replacement)}");
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
