// Translated from behavioral/dynamodb/check_update_item_subtraction_literal_operand_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-subtraction-literal-operand-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSubtractionLiteralOperandTypeMismatchRejected
{
    private const string Check = "update-item-subtraction-literal-operand-type-mismatch-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgOperandType =
        "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for operator " +
        "or function; operator or function: -, operand type: S";

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found code means it is already gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>(code, message) of the error fn raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be refused, but it succeeded: {result}") : (err.Code, err.Message);
    }

    /// <summary>
    /// A subtraction (x - y) whose literal operand is a String, not a Number, is rejected
    /// with ValidationException naming the - operator and the operand's own type, regardless of
    /// which side of the subtraction carries the mismatched value, and leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-subtraction-literal-operand-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-subtype-");
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

        var key = Item(("pk", S("p")));
        var original = Item(("pk", S("p")), ("num", N("5")));
        await ddb.PutItemAsync(table, original);

        UpdateItemRequest Subtract(AttributeValue a, AttributeValue b) => new()
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET num = :a - :b",
            ExpressionAttributeValues = Item((":a", a), (":b", b)),
        };

        // The String operand on the left of "-".
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(Subtract(S("x"), N("1"))));
        Ensure(
            code == ErrValidation,
            $"SET num = :a - :b with :a a String answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            $"{Py.Repr(ErrValidation)}");
        Ensure(
            message == MsgOperandType,
            $"SET num = :a - :b with :a a String message is {Py.Repr(message)}, expected {Py.Repr(MsgOperandType)}");

        // The String operand on the right of "-".
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(Subtract(N("1"), S("x"))));
        Ensure(
            code == ErrValidation,
            $"SET num = :a - :b with :b a String answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            $"{Py.Repr(ErrValidation)}");
        Ensure(
            message == MsgOperandType,
            $"SET num = :a - :b with :b a String message is {Py.Repr(message)}, expected {Py.Repr(MsgOperandType)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after two refused subtractions the item is {Py.Repr(unchanged)}, expected it unchanged from " +
            $"{Py.Repr(original)}");

        // Positive control: subtraction with both operands numeric applies normally.
        var request = Subtract(N("10"), N("3"));
        request.ReturnValues = ReturnValue.UPDATED_NEW;
        var resp = await ddb.UpdateItemAsync(request);
        Ensure(
            Py.Equal(resp.Attributes, Item(("num", N("7")))),
            $"SET num = :a - :b with two numeric operands returned {Py.Repr(resp.Attributes)}, expected num=7");
    }
}
