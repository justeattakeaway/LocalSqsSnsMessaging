// Translated from behavioral/dynamodb/check_delete_item_condition_expression_non_distinct_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-expression-non-distinct-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionExpressionNonDistinctOperandsRejected
{
    private const string Check = "delete-item-condition-expression-non-distinct-operands-rejected";

    private static readonly (string Code, string Message) NotDistinctEqScore = (
        "ValidationException",
        "1 validation error detected: Invalid ConditionExpression: The first operand must be " +
        "distinct from the remaining operands for this operator or function; operator: =, " +
        "first operand: [score]");

    private static readonly (string Code, string Message) NotDistinctEqAliased = (
        "ValidationException",
        "1 validation error detected: Invalid ConditionExpression: The first operand must be " +
        "distinct from the remaining operands for this operator or function; operator: =, " +
        "first operand: [x]");

    private static readonly (string Code, string Message) NotDistinctEqNested = (
        "ValidationException",
        "1 validation error detected: Invalid ConditionExpression: The first operand must be " +
        "distinct from the remaining operands for this operator or function; operator: =, " +
        "first operand: [info, label]");

    private static readonly (string Code, string Message) NotDistinctBeginsWith = (
        "ValidationException",
        "1 validation error detected: Invalid ConditionExpression: The first operand must be " +
        "distinct from the remaining operands for this operator or function; operator: begins_with, " +
        "first operand: [tag]");

    /// <summary>
    /// A DeleteItem ConditionExpression naming the same attribute as both operands of = or
    /// begins_with is refused, through a bare path, a nested path or two aliases
    /// resolving to one name, leaving the item untouched; a distinct-operand condition deletes it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-expression-non-distinct-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbdnd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item1")));
        var item = Item(
            ("pk", S("item1")),
            ("score", N("5")),
            ("x", S("hello")),
            ("info", M(("label", S("world")))),
            ("tag", S("prefix-value")));
        await ddb.PutItemAsync(table, item);

        async Task<Dictionary<string, AttributeValue>?> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        Task<DeleteItemResponse> Delete(string cond, Dictionary<string, string>? names = null, Dictionary<string, AttributeValue>? values = null) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Key(),
                ConditionExpression = cond,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });

        var baseline = await Read();
        Ensure(Py.Equal(baseline, item), $"expected the seeded item back, got {Py.Repr(baseline)}");

        await ExpectError(() => Delete("score = score"), NotDistinctEqScore);
        Ensure(Py.Equal(await Read(), baseline), "item must survive the rejected 'score = score' delete");

        await ExpectError(() => Delete("#n = #v", names: Names(("#n", "x"), ("#v", "x"))), NotDistinctEqAliased);
        Ensure(Py.Equal(await Read(), baseline), "item must survive the rejected '#n = #v' delete");

        await ExpectError(() => Delete("info.label = info.label"), NotDistinctEqNested);
        Ensure(Py.Equal(await Read(), baseline), "item must survive the rejected 'info.label = info.label' delete");

        await ExpectError(() => Delete("begins_with(tag, tag)"), NotDistinctBeginsWith);
        Ensure(Py.Equal(await Read(), baseline), "item must survive the rejected 'begins_with(tag, tag)' delete");

        // Positive control: an identical DeleteItem conditioned on a distinct value deletes the item.
        await Delete("score = :v", values: Item((":v", N("5"))));
        Ensure(await Read() is null, "expected the item to be gone after the 'score = :v' delete");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
