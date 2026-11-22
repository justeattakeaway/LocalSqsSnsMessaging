// Translated from behavioral/dynamodb/check_delete_item_three_term_condition_expression_and_gates_the_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-three-term-condition-expression-and-gates-the-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemThreeTermConditionExpressionAndGatesTheDelete
{
    private const string Check = "delete-item-three-term-condition-expression-and-gates-the-delete";

    private const string Cond = "attribute_exists(pk) AND ver = :v AND #st = :s";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A three-term ConditionExpression AND on DeleteItem needs every term true: one false
    /// term raises ConditionalCheckFailedException and leaves the item untouched; all three true
    /// deletes it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-three-term-condition-expression-and-gates-the-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-del3and-");
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

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        Task<DeleteItemResponse> Delete(Dictionary<string, AttributeValue> key) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = key,
                ConditionExpression = Cond,
                ExpressionAttributeNames = Names(("#st", "status")),
                ExpressionAttributeValues = Item((":v", N("1")), (":s", S("A"))),
            });

        // --- one term false (status mismatch): attribute_exists(pk) true, ver = :v true, #st = :s false ---
        var keyFail = Item(("pk", S("fail")));
        var baseFail = Item(("pk", S("fail")), ("ver", N("1")), ("status", S("B")));
        await ddb.PutItemAsync(table, baseFail);

        await ExpectError(() => Delete(keyFail), Ccfe);
        var stillThere = await Get("fail");
        Ensure(
            Py.Equal(stillThere, baseFail),
            $"DeleteItem under a three-term AND with one false term changed the item to {Py.Repr(stillThere)}");

        // --- positive control: all three terms true ---
        var keyPass = Item(("pk", S("pass")));
        var basePass = Item(("pk", S("pass")), ("ver", N("1")), ("status", S("A")));
        await ddb.PutItemAsync(table, basePass);

        await Delete(keyPass);
        var gone = await Get("pass");
        Ensure(gone is null, $"DeleteItem under a three-term AND with all terms true left {Py.Repr(gone)}");
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
