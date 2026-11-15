// Translated from behavioral/dynamodb/check_putitem_condition_expression_and_or_not_precedence.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-expression-and-or-not-precedence</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionExpressionAndOrNotPrecedence
{
    private const string CheckId = "putitem-condition-expression-and-or-not-precedence";
    private static readonly (string Code, string Message) CondCheckFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>Assert <c>fn</c> raises exactly <c>(code, message)</c>. RAISES on an unexpected success.</summary>
    private static async Task ExpectError(Func<Task> fn, (string Code, string Message) expected)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused with {expected.Code}, but it SUCCEEDED");
    }

    /// <summary>
    /// A PutItem ConditionExpression parses with the filter grammar's precedence: AND binds
    /// tighter than OR, and NOT binds tighter than AND, not left-to-right or outermost-first.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-expression-and-or-not-precedence")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("precedence")));
        await ddb.PutItemAsync(table, Item(("pk", S("precedence")), ("a", N("5")), ("b", N("10")), ("marker", S("seed"))));

        async Task<string> ReadMarker()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(item["a"], N("5")) && Py.Equal(item["b"], N("10")),
                $"a/b must stay 5/10 throughout, got a={Py.Repr(item.GetValueOrDefault("a"))} b={Py.Repr(item.GetValueOrDefault("b"))}");
            return item["marker"].S;
        }

        Task Put(string marker, string condExpr, Dictionary<string, AttributeValue> values) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("precedence")), ("a", N("5")), ("b", N("10")), ("marker", S(marker))),
                ConditionExpression = condExpr,
                ExpressionAttributeValues = values,
            });

        // AND binds tighter than OR: with A=(a=5) true, B=(a=10) false, C=(b=20) false,
        // the implicit `A OR B AND C` is `A OR (B AND C)` = true OR false = true -- admitted.
        var valsAndOr = Item((":five", N("5")), (":ten", N("10")), (":twenty", N("20")));
        await Put("op1-and-or-implicit-admitted", "a = :five OR a = :ten AND b = :twenty", valsAndOr);
        Ensure(
            await ReadMarker() == "op1-and-or-implicit-admitted",
            "`A OR B AND C` should apply as `A OR (B AND C)` = true OR false = true");

        // The explicit `(A OR B) AND C` = (true OR false) AND false = false -- rejected, and the
        // grouped parse's own effect (op1's marker) stays untouched.
        await ExpectError(
            () => Put("op2-grouped-should-not-apply", "(a = :five OR a = :ten) AND b = :twenty", valsAndOr),
            CondCheckFailed);
        Ensure(
            await ReadMarker() == "op1-and-or-implicit-admitted",
            "the rejected `(A OR B) AND C` write must not change the item");

        // NOT binds tighter than AND: with D=(a=5) true, E=(b=5) false, the bare
        // `NOT D AND E` parses as `(NOT D) AND E` = false AND false = false -- rejected.
        var valsNotAnd = Item((":five", N("5")));
        await ExpectError(
            () => Put("op3-bare-should-not-apply", "NOT a = :five AND b = :five", valsNotAnd),
            CondCheckFailed);
        Ensure(
            await ReadMarker() == "op1-and-or-implicit-admitted",
            "the rejected bare `NOT D AND E` write must not change the item");

        // The grouped `NOT (D AND E)` = NOT(true AND false) = NOT(false) = true -- admitted, proving
        // the bare form above really took the NOT-before-AND parse and not this one.
        await Put("op4-grouped-not-and-admitted", "NOT (a = :five AND b = :five)", valsNotAnd);
        Ensure(
            await ReadMarker() == "op4-grouped-not-and-admitted",
            "`NOT (D AND E)` should apply as NOT(true AND false) = true");
    }
}
