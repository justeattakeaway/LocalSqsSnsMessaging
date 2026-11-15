// Translated from behavioral/dynamodb/check_update_item_remove_missing_map_key_noop_and_set_empty_list_index.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-remove-missing-map-key-noop-and-set-empty-list-index</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemRemoveMissingMapKeyNoopAndSetEmptyListIndex
{
    private const string CheckId = "update-item-remove-missing-map-key-noop-and-set-empty-list-index";

    /// <summary>
    /// UpdateItem REMOVE of a map key that does not exist is a true no-op, and SET l[0] on a
    /// stored empty list stores a single-element list.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-remove-missing-map-key-noop-and-set-empty-list-index")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);

        var key = Item(("pk", S("has-map")));
        var seeded = Item(("pk", S("has-map")), ("m", M(("present", S("x")))));
        await ddb.PutItemAsync(t, seeded);

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE m.#missing",
            ExpressionAttributeNames = Names(("#missing", "missing")),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, seeded),
            $"REMOVE m.missing where 'missing' was never a key of m returned ALL_NEW " +
            $"{Py.Repr(resp.Attributes)}, expected the item unchanged {Py.Repr(seeded)}");
        var after = await Stored(ddb, t, "has-map");
        Ensure(Py.Equal(after, seeded), $"GetItem after the no-op REMOVE returned {Py.Repr(after)}, expected it unchanged {Py.Repr(seeded)}");

        // Positive control: REMOVE of a key that IS present actually removes it, so the no-op above
        // is about the missing key, not a broken REMOVE.
        var resp2 = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "REMOVE m.#present",
            ExpressionAttributeNames = Names(("#present", "present")),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(resp2.Attributes, Item(("pk", S("has-map")), ("m", M()))),
            $"REMOVE m.present returned ALL_NEW {Py.Repr(resp2.Attributes)}, expected an emptied map");

        var key2 = Item(("pk", S("empty-list")));
        var seeded2 = Item(("pk", S("empty-list")), ("l", L()));
        await ddb.PutItemAsync(t, seeded2);

        var resp3 = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key2,
            UpdateExpression = "SET l[0] = :v",
            ExpressionAttributeValues = Item((":v", S("only"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(resp3.Attributes, Item(("pk", S("empty-list")), ("l", L(S("only"))))),
            $"SET l[0] on a stored empty list returned ALL_NEW {Py.Repr(resp3.Attributes)}, expected a " +
            "single-element list [only]");
        var after2 = await Stored(ddb, t, "empty-list");
        Ensure(
            Py.Equal(after2, Item(("pk", S("empty-list")), ("l", L(S("only"))))),
            $"GetItem after SET l[0] on an empty list returned {Py.Repr(after2)}, expected [only]");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddbnp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
