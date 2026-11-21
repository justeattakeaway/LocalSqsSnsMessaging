// Translated from behavioral/dynamodb/check_update_item_size_floor_treats_key_name_and_value_bytes_equally.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-size-floor-treats-key-name-and-value-bytes-equally</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSizeFloorTreatsKeyNameAndValueBytesEqually
{
    private const string Check = "update-item-size-floor-treats-key-name-and-value-bytes-equally";
    private static readonly (string Code, string Message) ItemTooLarge = ("ValidationException", "Item size to update has exceeded the maximum allowed size");
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
    /// Seed key, assert a bare <c>SET v = :v</c> of exactly <paramref name="ceiling"/> bytes is accepted and one
    /// byte more is refused, and that the refused write left no trace.
    /// </summary>
    private static async Task Probe(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key, int ceiling, int preexistingBytes)
    {
        await ddb.PutItemAsync(table, new Dictionary<string, AttributeValue>(key, StringComparer.Ordinal));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', ceiling)))),
        });
        var accepted = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            (accepted.GetValueOrDefault("v")?.S ?? "").Length == ceiling,
            $"a SET of {ceiling} bytes (predicted for {preexistingBytes} pre-existing bytes) " +
            $"did not read back at that length: {Py.Repr(accepted.GetValueOrDefault("v"))}");

        await ddb.PutItemAsync(table, new Dictionary<string, AttributeValue>(key, StringComparer.Ordinal)); // reset: no "v" attribute on the stored item
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", S(new string('x', ceiling + 1)))),
            }),
            ItemTooLarge);
        var refused = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(!refused.ContainsKey("v"), $"a refused SET of {ceiling + 1} bytes still created attribute v: {Py.Repr(refused)}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix, string keyName)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement(keyName, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(keyName, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 90);
        return table;
    }

    /// <summary>
    /// UpdateItem's size ceiling crosses from the flat 22-byte floor to the per-byte charge
    /// at the same total key-attribute byte count whether those bytes come from the key's
    /// name or from the key's value -- not only once both are well past the floor.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-size-floor-treats-key-name-and-value-bytes-equally")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Crossing the floor by KEY VALUE length, key name fixed at 1 byte ("k").
        var valueTable = await CreateTable(ctx, ddb, "sim2real-behav-ddbfloorval-", "k");

        // 1-byte name + 21-byte value = 22 pre-existing bytes: at the floor, still flat.
        await Probe(ddb, valueTable, Item(("k", S(new string('v', 21)))), FlatCeiling, 22);
        // 1-byte name + 22-byte value = 23 pre-existing bytes: one past the floor.
        await Probe(ddb, valueTable, Item(("k", S(new string('v', 22)))), FlatCeiling - 1, 23);

        // Crossing the floor by KEY NAME length, key value fixed at 1 byte ("0").
        var name21 = new string('n', 21);
        var name21Table = await CreateTable(ctx, ddb, "sim2real-behav-ddbfloornm21-", name21);

        // 21-byte name + 1-byte value = 22 pre-existing bytes: at the floor, still flat.
        await Probe(ddb, name21Table, Item((name21, S("0"))), FlatCeiling, 22);

        var name22 = new string('n', 22);
        var name22Table = await CreateTable(ctx, ddb, "sim2real-behav-ddbfloornm22-", name22);

        // 22-byte name + 1-byte value = 23 pre-existing bytes: one past the floor.
        await Probe(ddb, name22Table, Item((name22, S("0"))), FlatCeiling - 1, 23);
    }
}
