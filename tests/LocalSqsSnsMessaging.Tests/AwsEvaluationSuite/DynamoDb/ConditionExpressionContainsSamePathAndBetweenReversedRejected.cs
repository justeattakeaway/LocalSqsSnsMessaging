// Translated from behavioral/dynamodb/check_condition_expression_contains_same_path_and_between_reversed_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-expression-contains-same-path-and-between-reversed-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionExpressionContainsSamePathAndBetweenReversedRejected
{
    private const string ErrValidation = "ValidationException";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// contains(path, path) and a BETWEEN with its bounds reversed are rejected by
    /// ConditionExpression parsing itself, not by the conditional check, and leave the
    /// existing item untouched.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-expression-contains-same-path-and-between-reversed-rejected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("tagset", SS("a", "b")), ("score", N("5"))));

        async Task<Dictionary<string, object?>> Read()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item"))), ConsistentRead = true })).Item;
            // DynamoDB does not guarantee the member order of a returned set, so normalize
            // the SS attribute to a frozenset before any equality comparison.
            var normalized = item.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
            normalized["tagset"] = FrozenSs(item["tagset"].SS);
            return normalized;
        }

        Task Update(string updateExpr, string condExpr, Dictionary<string, AttributeValue> values) =>
            ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("item"))),
                UpdateExpression = updateExpr,
                ConditionExpression = condExpr,
                ExpressionAttributeValues = values,
            });

        // The (Code, Message) of an update real AWS refuses. RAISES on unexpected success.
        async Task<(string? Code, string? Message)> RejectedUpdate(string updateExpr, string condExpr, Dictionary<string, AttributeValue> values)
        {
            var error = await FidelityContext.CaptureError(() => Update(updateExpr, condExpr, values));
            return error is not null
                ? (error.Code, error.Message)
                : throw Fail($"real AWS refuses the update conditioned on {Py.Repr(condExpr)}, but it SUCCEEDED");
        }

        var baseline = await Read();

        // contains(path, path): the two operands name the same attribute, which the
        // function rejects while parsing the expression, before any item is examined.
        var (code, message) = await RejectedUpdate("SET score = :nv", "contains(tagset, tagset)", Item((":nv", N("999"))));
        Ensure(code == ErrValidation, $"contains(tagset, tagset) should be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(code != ErrConditionalCheckFailed, "a same-path contains() is a parse-time defect, never a conditional-check failure");
        Ensure(
            message ==
            "1 validation error detected: Invalid ConditionExpression: The first operand must be " +
            "distinct from the remaining operands for this operator or function; operator: contains, " +
            "first operand: [tagset]",
            $"unexpected message for same-path contains(): {Py.Repr(message)}");
        var got = await Read();
        Ensure(Py.Equal(got, baseline), $"item must be unchanged after the rejected contains() update, got {Py.Repr(got)}");

        // BETWEEN whose lower bound is numerically above its upper bound: rejected by the
        // BETWEEN operator's own bound check, before any item is examined.
        (code, message) = await RejectedUpdate(
            "SET score = :nv",
            "score BETWEEN :hi AND :lo",
            Item((":nv", N("888")), (":hi", N("100")), (":lo", N("1"))));
        Ensure(code == ErrValidation, $"BETWEEN with lower>upper should be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(code != ErrConditionalCheckFailed, "a reversed BETWEEN is a parse-time defect, never a conditional-check failure");
        Ensure(
            message ==
            "1 validation error detected: Invalid ConditionExpression: The BETWEEN operator requires " +
            "upper bound to be greater than or equal to lower bound; lower bound operand: " +
            "AttributeValue: {N:100}, upper bound operand: AttributeValue: {N:1}",
            $"unexpected message for reversed BETWEEN: {Py.Repr(message)}");
        got = await Read();
        Ensure(Py.Equal(got, baseline), $"item must be unchanged after the rejected BETWEEN update, got {Py.Repr(got)}");

        // Positive control: contains() on two distinct attributes, and a correctly ordered
        // BETWEEN, both parse and admit the write — the item is not simply frozen.
        await Update(
            "SET tagset = :nv",
            "contains(tagset, :member) AND score BETWEEN :lo AND :hi",
            Item((":nv", SS("a", "b", "c")), (":member", S("a")), (":lo", N("1")), (":hi", N("100"))));
        got = await Read();
        Ensure(
            Py.Equal(got["tagset"], FrozenSs(["a", "b", "c"])),
            $"the positive-control update should have admitted the write, got {Py.Repr(got.GetValueOrDefault("tagset"))}");
    }

    /// <summary><c>{'SS': frozenset(members)}</c>: the members in a fixed order, so equality ignores theirs.</summary>
    private static Dictionary<string, object?> FrozenSs(IEnumerable<string> members) =>
        new() { ["SS"] = members.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
}
