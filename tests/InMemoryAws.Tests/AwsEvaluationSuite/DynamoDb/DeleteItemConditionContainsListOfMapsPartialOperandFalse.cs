// Translated from behavioral/dynamodb/check_delete_item_condition_contains_list_of_maps_partial_operand_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-contains-list-of-maps-partial-operand-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionContainsListOfMapsPartialOperandFalse
{
    private const string Check = "delete-item-condition-contains-list-of-maps-partial-operand-false";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// DeleteItem's ConditionExpression contains() over a list of maps needs full element
    /// equality: a map operand that is a strict subset of one list element's keys does not satisfy
    /// contains, so the delete is refused.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-contains-list-of-maps-partial-operand-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        AttributeValue ListAttr() => L(M(("a", S("1")), ("b", S("2"))), M(("a", S("9")), ("b", S("9"))));
        var partialMap = M(("a", S("1")));
        var fullMatchMap = M(("b", S("2")), ("a", S("1")));

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("di-contains-partial")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-contains-partial")), ("entries", ListAttr())));

        // Break: the operand carries only a subset of element_0's keys, so no list element equals
        // it exactly -- contains() must be false, not "true because it matches a prefix of keys".
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Key(),
                ConditionExpression = "contains(entries, :partial)",
                ExpressionAttributeValues = Item((":partial", partialMap)),
            }),
            Ccfe);

        // Effect absent: the rejected delete must not have removed the item.
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            stillThere is not null && Py.Equal(stillThere.GetValueOrDefault("entries"), ListAttr()),
            $"rejected delete must not remove the item, got {Py.Repr(stillThere)}");

        // Positive control: the identical shape, but the operand is key-for-key and value-for-value
        // equal to element_0 (key order in the request differs) -- contains() is true and the delete
        // applies, proving the refusal above was the partial match and not the function itself.
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "contains(entries, :match)",
            ExpressionAttributeValues = Item((":match", fullMatchMap)),
        });
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(gone is null, $"expected item absent after the satisfied contains() delete, got {Py.Repr(gone)}");
    }

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
