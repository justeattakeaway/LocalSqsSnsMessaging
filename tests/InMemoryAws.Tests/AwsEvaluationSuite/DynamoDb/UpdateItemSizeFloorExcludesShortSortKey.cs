// Translated from behavioral/dynamodb/check_update_item_size_floor_excludes_short_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-size-floor-excludes-short-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSizeFloorExcludesShortSortKey
{
    private const string Check = "update-item-size-floor-excludes-short-sort-key";
    private static readonly (string Code, string Message) ItemTooLarge = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    // Below FlatFloorBytes of pre-existing attribute bytes, a standalone UpdateItem's size
    // ceiling for a new attribute "v" is flat at 409600 - len("v") - FlatFloorBytes,
    // regardless of how those pre-existing bytes are distributed among the item's attributes.
    private const int Cap = 409600;
    private const int FlatFloorBytes = 22;
    private const int VNameLen = 1; // len("v")
    private const int FlatCeiling = Cap - VNameLen - FlatFloorBytes;

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected UpdateItem to be rejected, but it succeeded: {result}") : (err.Code, err.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// Seed key, confirm FlatCeiling is the exact accept/refuse boundary for a bare
    /// <c>SET v = :v</c> write, and confirm the refused write left no trace.
    /// </summary>
    private static async Task<int> Probe(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key)
    {
        await ddb.PutItemAsync(table, new Dictionary<string, AttributeValue>(key, StringComparer.Ordinal));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', FlatCeiling)))),
        });
        var accepted = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            (accepted.GetValueOrDefault("v")?.S ?? "").Length == FlatCeiling,
            $"a SET of {FlatCeiling} bytes did not read back at that length: {Py.Repr(accepted.GetValueOrDefault("v"))}");

        await ddb.PutItemAsync(table, new Dictionary<string, AttributeValue>(key, StringComparer.Ordinal)); // reset: no "v" attribute on the stored item
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", S(new string('x', FlatCeiling + 1)))),
            }),
            ItemTooLarge);
        var refused = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(!refused.ContainsKey("v"), $"a refused SET of {FlatCeiling + 1} bytes still created attribute v: {Py.Repr(refused)}");
        return FlatCeiling;
    }

    /// <summary>
    /// A composite-key table's UpdateItem size ceiling equals a hash-only table's ceiling
    /// when the sort key's bytes keep the item's pre-existing bytes under the 22-byte floor.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-size-floor-excludes-short-sort-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var hashTable = FidelityContext.UniqueName("sim2real-behav-ddbfloorhash-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = hashTable,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(hashTable));
        await ddb.WaitForTableExists(hashTable, delaySeconds: 1, maxAttempts: 90);

        var sortTable = FidelityContext.UniqueName("sim2real-behav-ddbfloorsort-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sortTable,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH), new KeySchemaElement("s", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S), new AttributeDefinition("s", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(sortTable));
        await ddb.WaitForTableExists(sortTable, delaySeconds: 1, maxAttempts: 90);

        // Hash-only, 1-byte key: 2 pre-existing bytes, well under the floor.
        var hashCeiling = await Probe(ddb, hashTable, Item(("k", S("0"))));
        Ensure(hashCeiling == FlatCeiling, $"hash-only ceiling was {Py.Repr(hashCeiling)}, expected the flat ceiling {Py.Repr(FlatCeiling)}");

        // Hash + a 1-byte sort key: 4 pre-existing bytes (still well under the floor). The
        // composite ceiling must equal the hash-only one -- the sort key's bytes do not yet
        // cost anything, unlike the 100-byte sort key regime where the per-byte charge binds.
        var compositeCeiling = await Probe(ddb, sortTable, Item(("k", S("0")), ("s", S("r"))));
        Ensure(
            compositeCeiling == hashCeiling,
            $"a 1-byte sort key gave ceiling {Py.Repr(compositeCeiling)}; a hash-only table gave " +
            $"{Py.Repr(hashCeiling)} -- both should be the flat floor ceiling");
    }
}
