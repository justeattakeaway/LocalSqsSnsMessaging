// Translated from behavioral/dynamodb/check_delete_item_condition_in_operand_compares_maps_structurally.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-in-operand-compares-maps-structurally</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionInOperandComparesMapsStructurally
{
    private const string Check = "delete-item-condition-in-operand-compares-maps-structurally";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// DeleteItem's ConditionExpression IN compares a map operand structurally: the delete applies
    /// only when the stored map equals one of the IN list's maps member-for-member, not by key subset
    /// or superset.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-in-operand-compares-maps-structurally")]
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

        AttributeValue StoredMap() => M(("a", S("1")), ("b", S("2")));
        var supersetMap = M(("a", S("1")), ("b", S("2")), ("c", S("3")));
        var subsetMap = M(("a", S("1")));
        var otherMap = M(("a", S("9")), ("b", S("9")));
        var matchingMap = M(("b", S("2")), ("a", S("1")));

        Dictionary<string, AttributeValue> NearMissKey() => Item(("pk", S("di-in-near-miss")));
        await ddb.PutItemAsync(table, Item(("pk", S("di-in-near-miss")), ("m", StoredMap())));

        // Break: the IN list carries a superset and a subset of the stored map's keys, and an
        // unrelated map, but never the stored map itself -- none compares equal structurally.
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = NearMissKey(),
                ConditionExpression = "m IN (:sup, :sub, :other)",
                ExpressionAttributeValues = Item((":sup", supersetMap), (":sub", subsetMap), (":other", otherMap)),
            }),
            Ccfe);

        // Effect absent: the rejected delete must not have removed the item.
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = NearMissKey(), ConsistentRead = true })).Item;
        Ensure(
            stillThere is not null && Py.Equal(stillThere.GetValueOrDefault("m"), StoredMap()),
            $"rejected delete must not remove the item, got {Py.Repr(stillThere)}");

        // Positive control: the identical shape, but the IN list's last entry is key-for-key and
        // value-for-value equal to the stored map (key order in the request differs) -- it matches
        // structurally and the delete applies.
        await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = NearMissKey(),
            ConditionExpression = "m IN (:sup, :sub, :match)",
            ExpressionAttributeValues = Item((":sup", supersetMap), (":sub", subsetMap), (":match", matchingMap)),
        });
        var gone = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = NearMissKey(), ConsistentRead = true })).Item;
        Ensure(gone is null, $"expected item absent after the satisfied IN condition delete, got {Py.Repr(gone)}");
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
