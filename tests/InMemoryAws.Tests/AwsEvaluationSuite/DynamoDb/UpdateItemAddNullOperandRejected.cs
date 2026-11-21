// Translated from behavioral/dynamodb/check_update_item_add_null_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-null-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddNullOperandRejected
{
    private const string Check = "update-item-add-null-operand-rejected";

    private static readonly (string Code, string Message) AddNullOperand = (
        "ValidationException",
        "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for operator " +
        "or function; operator: ADD, operand type: NULL, typeSet: ALLOWED_FOR_ADD_OPERAND");

    /// <summary>Call fn() and assert the error it raises carries exactly code and message.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected {Py.Repr(expected.Code)}, but the call succeeded: {result}");
        }

        Ensure(err.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>
    /// UpdateExpression ADD refuses a NULL-typed operand, leaves the attribute untouched, and a
    /// same-typed ADD still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-null-operand-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("x")));
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("tally", N("10"))));
        var names = Names(("#t", "tally"));

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD #t :v",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = Item((":v", Null())),
            }),
            AddNullOperand);

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged["tally"], N("10")),
            $"the rejected ADD must leave the attribute untouched, got {Py.Repr(unchanged["tally"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #t :v",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = Item((":v", N("5"))),
        });
        var bumped = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(bumped["tally"], N("15")),
            $"the positive-control same-typed ADD must apply, got {Py.Repr(bumped["tally"])}");
    }
}
