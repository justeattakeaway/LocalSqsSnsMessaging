// Translated from behavioral/dynamodb/check_unbalanced_parenthesized_condition_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.unbalanced-parenthesized-condition-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnbalancedParenthesizedConditionExpressionRejected
{
    private static readonly (string Code, string Message) SyntaxError = (
        "ValidationException",
        "1 validation error detected: Invalid ConditionExpression: Syntax error; "
        + "token: \"<EOF>\", near: \":bb\"");

    private const string Unbalanced = "(a < :ab AND b > :bb";
    private const string Balanced = "(a < :ab AND (b > :bb))";

    private static Dictionary<string, AttributeValue> Values() => Item((":ab", N("10")), (":bb", N("5")));

    /// <summary>{'pk': {'S': pk}, **base, ...extra}, where base is {'a': {'N': '5'}, 'b': {'N': '10'}}.</summary>
    private static Dictionary<string, AttributeValue> WithBase(string pk, params (string Name, AttributeValue Value)[] extra) =>
        Item([("pk", S(pk)), ("a", N("5")), ("b", N("10")), .. extra]);

    /// <summary>Raises on an unexpected success; otherwise asserts the exact (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            Ensure(err.Code == expected.Code, $"expected code {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}");
            Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused with {expected.Code}, but it succeeded: {result}");
    }

    /// <summary>
    /// A ConditionExpression with an unbalanced open paren is a ValidationException on
    /// PutItem, DeleteItem and UpdateItem, and the targeted item is left unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.unbalanced-parenthesized-condition-expression-rejected")]
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

        async Task<Dictionary<string, AttributeValue>?> Read(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // PutItem: the balanced form is a positive control that succeeds; the unbalanced
        // form never reaches evaluation, so it is rejected whether or not it would hold.
        await ddb.PutItemAsync(table, WithBase("put-target"));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = WithBase("put-target", ("written", Bool(true))),
            ConditionExpression = Balanced,
            ExpressionAttributeValues = Values(),
        });
        Ensure(Py.Equal(await Read("put-target"), WithBase("put-target", ("written", Bool(true)))), "the balanced control PutItem did not apply");
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = WithBase("put-target", ("written", Bool(false))),
                ConditionExpression = Unbalanced,
                ExpressionAttributeValues = Values(),
            }),
            SyntaxError);
        Ensure(
            Py.Equal(await Read("put-target"), WithBase("put-target", ("written", Bool(true)))),
            "put-target changed despite the rejected unbalanced PutItem");

        // DeleteItem: same control/rejection pair.
        await ddb.PutItemAsync(table, WithBase("delete-target"));
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("delete-target"))),
                ConditionExpression = Unbalanced,
                ExpressionAttributeValues = Values(),
            }),
            SyntaxError);
        Ensure(Py.Equal(await Read("delete-target"), WithBase("delete-target")), "delete-target was removed despite the rejected unbalanced DeleteItem");
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("delete-target"))),
            ConditionExpression = Balanced,
            ExpressionAttributeValues = Values(),
        });
        Ensure(await Read("delete-target") is null, "the balanced control DeleteItem did not apply");

        // UpdateItem: same control/rejection pair.
        await ddb.PutItemAsync(table, WithBase("update-target"));
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("update-target"))),
                UpdateExpression = "SET c = :v",
                ConditionExpression = Unbalanced,
                ExpressionAttributeValues = Item((":ab", N("10")), (":bb", N("5")), (":v", S("x"))),
            }),
            SyntaxError);
        Ensure(Py.Equal(await Read("update-target"), WithBase("update-target")), "update-target changed despite the rejected unbalanced UpdateItem");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("update-target"))),
            UpdateExpression = "SET c = :v",
            ConditionExpression = Balanced,
            ExpressionAttributeValues = Item((":ab", N("10")), (":bb", N("5")), (":v", S("x"))),
        });
        Ensure(Py.Equal((await Read("update-target"))!["c"], S("x")), "the balanced control UpdateItem did not apply");
    }
}
