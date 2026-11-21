// Translated from behavioral/dynamodb/check_delete_item_condition_list_nested_ordering_not_in_parenthesized_forms_near_miss_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-list-nested-ordering-not-in-parenthesized-forms-near-miss-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionListNestedOrderingNotInParenthesizedFormsNearMissRejected
{
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// DeleteItem conditions on a list element, a nested numeric path, negated
    /// attribute_exists and ordering, IN and a parenthesized OR-under-AND each
    /// reject a near-miss item unchanged and delete a sibling item that matches.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-list-nested-ordering-not-in-parenthesized-forms-near-miss-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Stored(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // Put each item, assert the near-miss is refused and stays, then the holding delete lands.
        async Task CheckPair(
            string label,
            Dictionary<string, AttributeValue> missKey,
            Dictionary<string, AttributeValue> missItem,
            Dictionary<string, AttributeValue> holdKey,
            Dictionary<string, AttributeValue> holdItem,
            string condition,
            Dictionary<string, AttributeValue>? missValues,
            Dictionary<string, AttributeValue>? holdValues)
        {
            await ddb.PutItemAsync(table, missItem);
            await ExpectError(
                () => ddb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = table,
                    Key = missKey,
                    ConditionExpression = condition,
                    ExpressionAttributeValues = missValues,
                }),
                Ccfe);
            var kept = await Stored(missKey);
            Ensure(Py.Equal(kept, missItem), $"{label}: near-miss condition must leave the item unchanged, got {Py.Repr(kept)}");

            await ddb.PutItemAsync(table, holdItem);
            await ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = holdKey,
                ConditionExpression = condition,
                ExpressionAttributeValues = holdValues,
            });
            var gone = await Stored(holdKey);
            Ensure(gone is null, $"{label}: holding condition must delete the item, got {Py.Repr(gone)}");
        }

        Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> key, params (string Name, AttributeValue Value)[] more) =>
            Item([.. key.Select(kv => (kv.Key, kv.Value)), .. more]);

        // List element equality: l[0] = :v.
        var missKey = Item(("pk", S($"{table}-list-miss")));
        var holdKey = Item(("pk", S($"{table}-list-hold")));
        AttributeValue LList() => L(S("alpha"), S("beta"));
        await CheckPair(
            "l[0] = :v",
            missKey, With(missKey, ("l", LList())),
            holdKey, With(holdKey, ("l", LList())),
            "l[0] = :v",
            Item((":v", S("zzz"))),
            Item((":v", S("alpha"))));

        // Ordering comparison on a nested number: m.n >= :v.
        missKey = Item(("pk", S($"{table}-nested-ge-miss")));
        holdKey = Item(("pk", S($"{table}-nested-ge-hold")));
        AttributeValue MDoc() => M(("n", N("10")));
        await CheckPair(
            "m.n >= :v",
            missKey, With(missKey, ("m", MDoc())),
            holdKey, With(holdKey, ("m", MDoc())),
            "m.n >= :v",
            Item((":v", N("11"))),
            Item((":v", N("10"))));

        // NOT attribute_exists on an absent attribute.
        missKey = Item(("pk", S($"{table}-not-exists-miss")));
        holdKey = Item(("pk", S($"{table}-not-exists-hold")));
        await CheckPair(
            "NOT attribute_exists(ghost)",
            missKey, With(missKey, ("ghost", S("present"))),
            holdKey, With(holdKey),
            "NOT attribute_exists(ghost)",
            null,
            null);

        // Negated ordering comparison: NOT (n < :v) holds when n equals v.
        missKey = Item(("pk", S($"{table}-not-lt-miss")));
        holdKey = Item(("pk", S($"{table}-not-lt-hold")));
        await CheckPair(
            "NOT (n < :v)",
            missKey, With(missKey, ("n", N("5"))),
            holdKey, With(holdKey, ("n", N("5"))),
            "NOT (n < :v)",
            Item((":v", N("6"))),
            Item((":v", N("5"))));

        // n IN (list of numbers).
        missKey = Item(("pk", S($"{table}-in-miss")));
        holdKey = Item(("pk", S($"{table}-in-hold")));
        await CheckPair(
            "n IN (:a, :b, :c)",
            missKey, With(missKey, ("n", N("7"))),
            holdKey, With(holdKey, ("n", N("7"))),
            "n IN (:a, :b, :c)",
            Item((":a", N("1")), (":b", N("2")), (":c", N("3"))),
            Item((":a", N("5")), (":b", N("7")), (":c", N("9"))));

        // Parenthesized (A OR B) AND C.
        missKey = Item(("pk", S($"{table}-or-and-miss")));
        holdKey = Item(("pk", S($"{table}-or-and-hold")));
        await CheckPair(
            "(a = :x OR a = :y) AND b = :z",
            missKey, With(missKey, ("a", N("5")), ("b", N("10"))),
            holdKey, With(holdKey, ("a", N("5")), ("b", N("10"))),
            "(a = :x OR a = :y) AND b = :z",
            Item((":x", N("5")), (":y", N("99")), (":z", N("999"))),
            Item((":x", N("5")), (":y", N("99")), (":z", N("10"))));
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
