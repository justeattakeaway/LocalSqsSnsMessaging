// Translated from behavioral/dynamodb/check_validation_before_table_lookup_deleteitem_and_getitem_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.validation-before-table-lookup-deleteitem-and-getitem-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ValidationBeforeTableLookupDeleteitemAndGetitemRejected
{
    private const string Check = "validation-before-table-lookup-deleteitem-and-getitem-rejected";

    private static readonly (string Code, string Message) ReturnValuesInvalid = (
        "ValidationException",
        "1 validation error detected: ReturnValues can only be ALL_OLD or NONE");

    private static readonly (string Code, string Message) ConsumedCapacityInvalid = (
        "ValidationException",
        "1 validation error detected: Value 'INVALID_VALUE' at 'returnConsumedCapacity' failed to " +
        "satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    private static readonly (string Code, string Message) TableNotFound = ("ResourceNotFoundException", "Requested resource not found");

    /// <summary>
    /// DeleteItem and GetItem validate their request before the table lookup: a malformed
    /// ReturnValues/ReturnConsumedCapacity fails ValidationException against a missing table, a
    /// well-formed one fails ResourceNotFoundException there, and both succeed on a table that exists.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.validation-before-table-lookup-deleteitem-and-getitem-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var ghost = FidelityContext.UniqueName("sim2real-behav-ghost-");
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("item")));

        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = ghost, Key = Key(), ReturnValues = ReturnValue.UPDATED_NEW }),
            ReturnValuesInvalid);
        await ExpectError(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = ghost, Key = Key(), ReturnConsumedCapacity = new ReturnConsumedCapacity("INVALID_VALUE") }),
            ConsumedCapacityInvalid);

        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = ghost, Key = Key(), ReturnValues = ReturnValue.ALL_OLD }),
            TableNotFound);
        await ExpectError(
            () => ddb.GetItemAsync(new GetItemRequest { TableName = ghost, Key = Key(), ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL }),
            TableNotFound);

        var table = FidelityContext.UniqueName("sim2real-behav-table-");
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

        var item = Item(("pk", S("item")), ("body", S("present")));
        await ddb.PutItemAsync(table, item);

        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL, ConsistentRead = true });
        Ensure(Py.Equal(got.Item, item), $"expected the stored item {Py.Repr(item)}, got {Py.Repr(got.Item)}");
        var capacity = Boto(got.ConsumedCapacity);
        var expectedCapacity = new Dictionary<string, object?> { ["TableName"] = table, ["CapacityUnits"] = 1.0 };
        Ensure(
            Py.Equal(capacity, expectedCapacity),
            $"expected ConsumedCapacity for a 1-unit strongly consistent read of the table, got {Py.Repr(capacity)}");

        var deleted = await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), ReturnValues = ReturnValue.ALL_OLD });
        Ensure(
            Py.Equal(deleted.Attributes, item),
            $"expected the accepted DeleteItem to return the removed item {Py.Repr(item)}, got " +
            $"{Py.Repr(deleted.Attributes)}");
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(after is null, $"the item must be gone after the accepted delete, found {Py.Repr(after)}");
    }

    /// <summary>ConsumedCapacity as boto3 hands it to the check: only the members present.</summary>
    private static Dictionary<string, object?>? Boto(ConsumedCapacity? cc)
    {
        if (cc is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            result["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is not null)
        {
            result["CapacityUnits"] = cc.CapacityUnits;
        }

        if (cc.ReadCapacityUnits is not null)
        {
            result["ReadCapacityUnits"] = cc.ReadCapacityUnits;
        }

        if (cc.WriteCapacityUnits is not null)
        {
            result["WriteCapacityUnits"] = cc.WriteCapacityUnits;
        }

        if (cc.Table is not null)
        {
            result["Table"] = cc.Table.CapacityUnits;
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            result["GlobalSecondaryIndexes"] = cc.GlobalSecondaryIndexes.Keys.ToList();
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            result["LocalSecondaryIndexes"] = cc.LocalSecondaryIndexes.Keys.ToList();
        }

        return result;
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly code/message; raise if it unexpectedly succeeds.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {expected.Code}, got {error.Code} (message {Py.Repr(error.Message)})");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected {expected.Code} to be refused, but the call SUCCEEDED and returned {Py.Repr(result)}");
    }
}
