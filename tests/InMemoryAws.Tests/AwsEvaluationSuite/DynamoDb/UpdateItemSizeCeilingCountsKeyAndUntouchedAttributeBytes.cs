// Translated from behavioral/dynamodb/check_update_item_size_ceiling_counts_key_and_untouched_attribute_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-size-ceiling-counts-key-and-untouched-attribute-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSizeCeilingCountsKeyAndUntouchedAttributeBytes
{
    private const string Check = "update-item-size-ceiling-counts-key-and-untouched-attribute-bytes";
    private static readonly (string Code, string Message) ItemTooLarge = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    // DynamoDB takes the LOOSER of two caps on a standalone UpdateItem that writes one new string
    // attribute "v": the finished item must stay <= 409600 bytes, and "v"'s own name+value must stay
    // <= 409600 - FlatFloorBytes bytes on their own. Below FlatFloorBytes bytes of pre-existing
    // attributes (the key's, a sort key's, an untouched attribute's -- none of them excluded) the
    // second cap binds and the ceiling is flat; past it the first cap binds and costs byte for byte.
    private const int Cap = 409600;
    private const int FlatFloorBytes = 22;
    private const int VNameLen = 1; // len("v")

    /// <summary>
    /// A standalone UpdateItem's size ceiling for the attribute it writes is 409600 minus
    /// whichever is larger: 22, or the bytes every other attribute in the item occupies -- the
    /// key's, a sort key's and an untouched attribute's bytes all count, identically in us-east-1.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-size-ceiling-counts-key-and-untouched-attribute-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var use1 = ctx.DynamoClient(region: "us-east-1"); // region-pinned: the same ceiling is asserted in a second region

        var table = FidelityContext.UniqueName("sim2real-behav-ddbupdceil-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 90);

        var sortTable = FidelityContext.UniqueName("sim2real-behav-ddbupdceilsk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sortTable,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH), new KeySchemaElement("s", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("k", ScalarAttributeType.S),
                new AttributeDefinition("s", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(sortTable));
        await ddb.WaitForTableExists(sortTable, delaySeconds: 1, maxAttempts: 90);

        var longName = new string('a', 100);
        var nameTable = FidelityContext.UniqueName("sim2real-behav-ddbupdceilnm-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = nameTable,
            KeySchema = [new KeySchemaElement(longName, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(longName, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(nameTable));
        await ddb.WaitForTableExists(nameTable, delaySeconds: 1, maxAttempts: 90);

        // A 1-byte key leaves 2 pre-existing bytes, well under the 22-byte floor: the flat ceiling
        // binds, and it sits below 409600 -- the written attribute's own cap, not the finished item's.
        var key1 = Item(("k", S("0")));
        var flat = await Probe(ddb, table, key1);
        Ensure(
            flat == Cap - VNameLen - FlatFloorBytes,
            $"a 1-byte key's ceiling was {flat}, expected the flat floor " +
            $"{Cap - VNameLen - FlatFloorBytes}");

        // Exactly at the 22-byte floor the ceiling is still flat; one byte past it, the ceiling
        // drops by exactly that one byte -- the transition from the flat cap to the per-byte one.
        var atFloor = await Probe(ddb, table, key1, extraItem: Item(("u", S(new string('z', 19)))));
        Ensure(atFloor == flat, $"22 pre-existing bytes gave ceiling {atFloor}, expected the flat {flat}");
        var pastFloor = await Probe(ddb, table, key1, extraItem: Item(("u", S(new string('z', 20)))));
        Ensure(
            pastFloor == flat - 1,
            $"23 pre-existing bytes gave ceiling {pastFloor}, expected exactly one less than the " +
            $"flat ceiling {flat}");

        // A longer key VALUE is not excluded: its bytes cost the ceiling byte for byte past the floor.
        var keyV100 = Item(("k", S(new string('y', 100))));
        var byValue = await Probe(ddb, table, keyV100);
        Ensure(
            byValue == Cap - VNameLen - 101,
            $"a 100-byte key value gave ceiling {byValue}, expected {Cap - VNameLen - 101}");

        var keyV1024 = Item(("k", S(new string('y', 1024))));
        var byValue1024 = await Probe(ddb, table, keyV1024);
        Ensure(
            byValue1024 == Cap - VNameLen - 1025,
            $"a 1024-byte key value gave ceiling {byValue1024}, expected {Cap - VNameLen - 1025}");

        // A longer key NAME costs exactly the same as the same number of bytes in the key value.
        var keyName100 = Item((longName, S("0")));
        var byName = await Probe(ddb, nameTable, keyName100);
        Ensure(
            byName == byValue,
            $"a 100-byte key name gave ceiling {byName}; a 100-byte key value gave {byValue} -- " +
            "key-name and key-value bytes must cost the ceiling the same");

        // A sort key's bytes are not excluded either: they cost exactly what any other attribute's do.
        var keySorted = Item(("k", S("0")), ("s", S(new string('r', 100))));
        var bySort = await Probe(ddb, sortTable, keySorted);
        Ensure(
            bySort == Cap - VNameLen - 103,
            $"a 100-byte sort key value gave ceiling {bySort}, expected {Cap - VNameLen - 103}");

        // Identical in us-east-1: the rule is not regional.
        var use1Table = FidelityContext.UniqueName("sim2real-behav-ddbupdceilue1-");
        await use1.CreateTableAsync(new CreateTableRequest
        {
            TableName = use1Table,
            KeySchema = [new KeySchemaElement("k", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("k", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => use1.DeleteTableAsync(use1Table));
        await use1.WaitForTableExists(use1Table, delaySeconds: 1, maxAttempts: 90);

        var use1Flat = await Probe(use1, use1Table, key1);
        Ensure(use1Flat == flat, $"us-east-1's flat ceiling was {use1Flat}, expected {flat} as in us-west-2");
        var use1ByValue = await Probe(use1, use1Table, keyV100);
        Ensure(
            use1ByValue == byValue,
            $"us-east-1's ceiling for a 100-byte key value was {use1ByValue}, expected {byValue} " +
            "as in us-west-2");
    }

    /// <summary>Max accepted length of the string value a bare <c>SET v = :v</c> writes.</summary>
    private static int Ceiling(int preexistingBytes) => Cap - VNameLen - Math.Max(preexistingBytes, FlatFloorBytes);

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<UpdateItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected UpdateItem to be rejected, but it succeeded: {Py.Repr(result?.Attributes)}");
    }

    private static async Task ExpectError(Func<Task<UpdateItemResponse>> fn, string code, string message)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static int AttrBytes(string name, AttributeValue value) => name.Length + value.S.Length;

    /// <summary>
    /// Seed key (+ extraItem), confirm the predicted ceiling is the exact accept/refuse boundary
    /// for a <c>SET v = :v</c> write, confirm the refused write left no trace, and return the ceiling.
    /// </summary>
    private static async Task<int> Probe(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key, Dictionary<string, AttributeValue>? extraItem = null)
    {
        var item = new Dictionary<string, AttributeValue>(key, StringComparer.Ordinal);
        if (extraItem is { Count: > 0 })
        {
            foreach (var (n, v) in extraItem)
            {
                item[n] = v;
            }
        }

        var preexisting = item.Sum(e => AttrBytes(e.Key, e.Value));
        var ceiling = Ceiling(preexisting);

        await ddb.PutItemAsync(table, item);
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', ceiling)))),
        });
        var accepted = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        accepted.TryGetValue("v", out var v1);
        Ensure(
            (v1?.S ?? "") != "" && v1!.S.Length == ceiling,
            $"a SET of {ceiling} bytes (the predicted ceiling for {preexisting} pre-existing bytes) " +
            $"did not read back at that length: {Py.Repr(v1)}");

        await ddb.PutItemAsync(table, item); // reset: no "v" attribute on the stored item
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", S(new string('x', ceiling + 1)))),
            }),
            ItemTooLarge.Code,
            ItemTooLarge.Message);
        var refused = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(!refused.ContainsKey("v"), $"a refused SET of {ceiling + 1} bytes still created attribute v: {Py.Repr(refused)}");
        return ceiling;
    }
}
