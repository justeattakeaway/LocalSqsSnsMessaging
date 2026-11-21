// Translated from behavioral/dynamodb/check_delete_item_condition_type_mismatch_and_absent_size_evaluate_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-type-mismatch-and-absent-size-evaluate-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionTypeMismatchAndAbsentSizeEvaluateFalse
{
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// DeleteItem with a ConditionExpression comparing an N attribute to an S value, or taking
    /// size() of an attribute absent from the item, evaluates false: ConditionalCheckFailedException,
    /// never a type coercion or an error about the missing attribute.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-type-mismatch-and-absent-size-evaluate-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-dic-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        Task<DeleteItemResponse> Delete(Dictionary<string, AttributeValue> key, string condition, Dictionary<string, AttributeValue> values) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = key,
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
            });

        // --- Bullet 1: an N attribute compared to an S value is false, not coerced, on DeleteItem. ---
        var mismatchKey = Item(("pk", S("di-type-mismatch")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-type-mismatch")), ("n", N("5"))));

        await ExpectError(() => Delete(mismatchKey, "n = :sval", Item((":sval", S("5")))), Ccfe);
        var stillThere = await Get(mismatchKey);
        Ensure(
            Py.Equal(stillThere, Item(("pk", S("di-type-mismatch")), ("n", N("5")))),
            $"a rejected delete must not remove the item, got {Py.Repr(stillThere)}");

        // Positive control: the same attribute compared to a same-typed N value satisfies the delete.
        await Delete(mismatchKey, "n = :nval", Item((":nval", N("5"))));
        var gone = await Get(mismatchKey);
        Ensure(gone is null, $"expected item absent after the type-matched delete, got {Py.Repr(gone)}");

        // --- Bullet 2: size() of an attribute absent from the item is false on DeleteItem. ---
        var absentKey = Item(("pk", S("di-size-absent")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-size-absent")), ("other", S("x"))));

        await ExpectError(() => Delete(absentKey, "size(missing_attr) > :zero", Item((":zero", N("0")))), Ccfe);
        var stillThere2 = await Get(absentKey);
        Ensure(
            Py.Equal(stillThere2, Item(("pk", S("di-size-absent")), ("other", S("x")))),
            $"a rejected delete must not remove the item, got {Py.Repr(stillThere2)}");

        // Positive control: a sibling item that actually carries the attribute satisfies size() > 0.
        var presentKey = Item(("pk", S("di-size-present")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-size-present")), ("missing_attr", S("xyz"))));
        await Delete(presentKey, "size(missing_attr) > :zero", Item((":zero", N("0"))));
        var gone2 = await Get(presentKey);
        Ensure(gone2 is null, $"expected item absent after the satisfied-condition delete, got {Py.Repr(gone2)}");
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
