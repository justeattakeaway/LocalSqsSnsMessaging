// Translated from behavioral/dynamodb/check_put_item_condition_expression_equality_n_attribute_vs_s_value_evaluates_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-expression-equality-n-attribute-vs-s-value-evaluates-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionExpressionEqualityNAttributeVsSValueEvaluatesFalse
{
    private const string Check = "put-item-condition-expression-equality-n-attribute-vs-s-value-evaluates-false";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A ConditionExpression `=` comparing a stored N attribute with an S value holding the same
    /// digits is false, so PutItem fails with ConditionalCheckFailedException and the item is
    /// unchanged; the same comparison against an N value of equal digits succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-expression-equality-n-attribute-vs-s-value-evaluates-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbeq-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("num", N("5"))));

        Task<PutItemResponse> PutConditioned(AttributeValue value) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("item")), ("num", N("5")), ("marker", S("applied"))),
                ConditionExpression = "num = :v",
                ExpressionAttributeValues = Item((":v", value)),
            });

        await ExpectError(() => PutConditioned(S("5")), Ccf);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, Item(("pk", S("item")), ("num", N("5")))),
            $"a PutItem rejected under num(N) = :v(S) must leave the item unchanged, got {Py.Repr(unchanged)}");

        // Positive control: the same comparator, matched types, lets the write land -- proving the
        // rejection above is about the type mismatch, not the comparator or the attribute name.
        await PutConditioned(N("5"));
        var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(landed, Item(("pk", S("item")), ("num", N("5")), ("marker", S("applied")))),
            $"a PutItem conditioned on num(N) = :v(N) of the same digits must land, got {Py.Repr(landed)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the conditional PutItem to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
