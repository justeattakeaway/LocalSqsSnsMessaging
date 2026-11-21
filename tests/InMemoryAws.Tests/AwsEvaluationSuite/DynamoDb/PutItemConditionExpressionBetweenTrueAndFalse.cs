// Translated from behavioral/dynamodb/check_put_item_condition_expression_between_true_and_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-condition-expression-between-true-and-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemConditionExpressionBetweenTrueAndFalse
{
    private const string Check = "put-item-condition-expression-between-true-and-false";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A PutItem ConditionExpression `v BETWEEN :lo AND :hi` that holds lets the write land, and
    /// one that does not holds the item unchanged with ConditionalCheckFailedException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-condition-expression-between-true-and-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbcebtw-");
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

        async Task<Dictionary<string, AttributeValue>?> ItemOf(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        var inRange = Item(("pk", S("in-range")), ("v", N("5")));
        var outRange = Item(("pk", S("out-range")), ("v", N("11")));
        await ddb.PutItemAsync(table, inRange);
        await ddb.PutItemAsync(table, outRange);

        Task<PutItemResponse> Put(Dictionary<string, AttributeValue> item, string marker) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", item["pk"]), ("v", item["v"]), ("marker", S(marker))),
                ConditionExpression = "v BETWEEN :lo AND :hi",
                ExpressionAttributeValues = Item((":lo", N("1")), (":hi", N("10"))),
            });

        // BETWEEN holds (5 is within [1, 10]): the write lands.
        await Put(inRange, "landed");
        var got = await ItemOf("in-range");
        Ensure(
            Py.Equal(got, Item(("pk", S("in-range")), ("v", N("5")), ("marker", S("landed")))),
            $"PutItem under a holding 'v BETWEEN 1 AND 10' (v = 5) did not land: {Py.Repr(got)}");

        // BETWEEN fails (11 is outside [1, 10]): ConditionalCheckFailedException, item unchanged.
        await ExpectError(() => Put(outRange, "should-not-land"), Ccfe);
        got = await ItemOf("out-range");
        Ensure(
            Py.Equal(got, outRange),
            $"PutItem refused under 'v BETWEEN 1 AND 10' (v = 11) must leave the item unchanged, got {Py.Repr(got)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }
}
