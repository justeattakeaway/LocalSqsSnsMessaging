// Translated from behavioral/dynamodb/check_delete_item_condition_attribute_type_with_an_unknown_type_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-attribute-type-with-an-unknown-type-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionAttributeTypeWithAnUnknownTypeNameRejected
{
    private const string Check = "delete-item-condition-attribute-type-with-an-unknown-type-name-rejected";

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("item-1")));

    private static Dictionary<string, AttributeValue> TheItem() => Item(("pk", S("item-1")), ("status", S("active")));

    /// <summary>
    /// DeleteItem's ConditionExpression attribute_type(#a, :t) with :t naming no DynamoDB type
    /// is refused ValidationException, not ConditionalCheckFailedException, and the item stays.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-attribute-type-with-an-unknown-type-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-delunk-");
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
        await ddb.PutItemAsync(table, TheItem());

        // Break exactly one thing: the type operand names no DynamoDB type.
        var (code, message) = await Error(() => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "attribute_type(#a, :t)",
            ExpressionAttributeNames = Names(("#a", "status")),
            ExpressionAttributeValues = Item((":t", S("STRING"))),
        }));
        Ensure(
            code == "ValidationException",
            $"the malformed condition was refused with {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException, not ConditionalCheckFailedException");
        const string expected =
            "1 validation error detected: Invalid ConditionExpression: Invalid attribute type name " +
            "found; type: STRING, valid types: {S,SS,N,NS,B,BS,BOOL,NULL,L,M}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, TheItem()), $"after the rejection the item must be unchanged, got {Py.Repr(got)}");

        // Positive control: the correctly typed condition lets the delete through.
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "attribute_type(#a, :t)",
            ExpressionAttributeNames = Names(("#a", "status")),
            ExpressionAttributeValues = Item((":t", S("S"))),
        });
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(got is null, $"the correctly typed condition must let the delete apply, got {Py.Repr(got)}");
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

    /// <summary>The (code, message) of the ClientError <c>fn()</c> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
