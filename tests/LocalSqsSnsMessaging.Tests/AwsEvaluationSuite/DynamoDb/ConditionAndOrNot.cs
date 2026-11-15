// Translated from behavioral/dynamodb/check_condition_and_or_not.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-and-or-not</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionAndOrNot
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private const string ErrConditionalCheckFailedMessage = "The conditional request failed";

    /// <summary>ConditionExpression AND/OR/NOT each admit a true combination and reject a false one.</summary>
    [Test]
    [DisplayName("dynamodb.condition-and-or-not")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("logic")), ("a", N("5")), ("b", N("10"))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("logic"))), ConsistentRead = true })).Item;

        Task Update(string updateExpr, string condExpr, Dictionary<string, AttributeValue> values) =>
            ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("logic"))),
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

        // AND: true-true is admitted; true-false is rejected and leaves the field untouched.
        await Update("SET c_and = :v", "a < :ten AND b > :five", Item((":v", S("pos-and")), (":ten", N("10")), (":five", N("5"))));
        var got = await Read();
        Ensure(Py.Equal(got["c_and"], S("pos-and")), $"expected c_and=pos-and after AND true-true, got {Py.Repr(got.GetValueOrDefault("c_and"))}");

        var (code, message) = await RejectedUpdate("SET c_and = :v", "a < :ten AND b > :fifteen", Item((":v", S("neg-and")), (":ten", N("10")), (":fifteen", N("15"))));
        Ensure(code == ErrConditionalCheckFailed, $"AND true-false update should be rejected with {ErrConditionalCheckFailed}, got {Py.Repr(code)}");
        Ensure(message == ErrConditionalCheckFailedMessage, $"expected {Py.Repr(ErrConditionalCheckFailedMessage)}, got {Py.Repr(message)}");
        got = await Read();
        Ensure(Py.Equal(got["c_and"], S("pos-and")), $"c_and must be unchanged by the rejected AND update, got {Py.Repr(got.GetValueOrDefault("c_and"))}");

        // OR: false-true is admitted; false-false is rejected and leaves the field untouched.
        await Update("SET c_or = :v", "a > :hundred OR b > :five", Item((":v", S("pos-or")), (":hundred", N("100")), (":five", N("5"))));
        got = await Read();
        Ensure(Py.Equal(got["c_or"], S("pos-or")), $"expected c_or=pos-or after OR false-true, got {Py.Repr(got.GetValueOrDefault("c_or"))}");

        (code, message) = await RejectedUpdate("SET c_or = :v", "a > :hundred OR b > :fifteen", Item((":v", S("neg-or")), (":hundred", N("100")), (":fifteen", N("15"))));
        Ensure(code == ErrConditionalCheckFailed, $"OR false-false update should be rejected with {ErrConditionalCheckFailed}, got {Py.Repr(code)}");
        Ensure(message == ErrConditionalCheckFailedMessage, $"expected {Py.Repr(ErrConditionalCheckFailedMessage)}, got {Py.Repr(message)}");
        got = await Read();
        Ensure(Py.Equal(got["c_or"], S("pos-or")), $"c_or must be unchanged by the rejected OR update, got {Py.Repr(got.GetValueOrDefault("c_or"))}");

        // NOT: negates its operand — NOT(false) is admitted, NOT(true) is rejected.
        await Update("SET c_not = :v", "NOT (a > :hundred)", Item((":v", S("pos-not")), (":hundred", N("100"))));
        got = await Read();
        Ensure(Py.Equal(got["c_not"], S("pos-not")), $"expected c_not=pos-not after NOT false, got {Py.Repr(got.GetValueOrDefault("c_not"))}");

        (code, message) = await RejectedUpdate("SET c_not = :v", "NOT (a < :hundred)", Item((":v", S("neg-not")), (":hundred", N("100"))));
        Ensure(code == ErrConditionalCheckFailed, $"NOT true update should be rejected with {ErrConditionalCheckFailed}, got {Py.Repr(code)}");
        Ensure(message == ErrConditionalCheckFailedMessage, $"expected {Py.Repr(ErrConditionalCheckFailedMessage)}, got {Py.Repr(message)}");
        got = await Read();
        Ensure(Py.Equal(got["c_not"], S("pos-not")), $"c_not must be unchanged by the rejected NOT update, got {Py.Repr(got.GetValueOrDefault("c_not"))}");
    }
}
