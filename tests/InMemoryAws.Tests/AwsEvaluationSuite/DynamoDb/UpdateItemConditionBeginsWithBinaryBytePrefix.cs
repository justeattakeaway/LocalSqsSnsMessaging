// Translated from behavioral/dynamodb/check_update_item_condition_begins_with_binary_byte_prefix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-begins-with-binary-byte-prefix</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionBeginsWithBinaryBytePrefix
{
    private const string Check = "update-item-condition-begins-with-binary-byte-prefix";
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be rejected, but it succeeded: {result}") : (err.Code, err.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// UpdateItem's ConditionExpression begins_with on a Binary attribute evaluates by raw byte
    /// prefix: a non-prefix byte string fails the write with ConditionalCheckFailedException and
    /// leaves the item unchanged, while a true byte prefix — shorter than or equal in length — applies it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-begins-with-binary-byte-prefix")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbwb-");
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

        const string pk = "bin-item";
        byte[] payload = [0x01, 0x02, 0xff, 0x00, 0x80];
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("bindata", B(payload)), ("rev", N("0"))));

        async Task<Dictionary<string, AttributeValue>?> ReadItem() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        UpdateItemRequest SetRev(string rev, byte[] prefix) => new()
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "SET rev = :r",
            ConditionExpression = "begins_with(bindata, :p)",
            ExpressionAttributeValues = Item((":r", N(rev)), (":p", B(prefix))),
        };

        // A byte string that is not a prefix of the stored value: the condition evaluates false.
        await ExpectError(async () => await ddb.UpdateItemAsync(SetRev("1", [0x01, 0x03])), Ccfe);
        var item = await ReadItem();
        Ensure(
            Py.Equal(item, Item(("pk", S(pk)), ("bindata", B(payload)), ("rev", N("0")))),
            $"a rejected UpdateItem must leave the item unchanged, got {Py.Repr(item)}");

        // A byte string longer than the stored value can never be a prefix of it either.
        await ExpectError(async () => await ddb.UpdateItemAsync(SetRev("1", [.. payload, 0x00])), Ccfe);
        item = await ReadItem();
        Ensure(
            Py.Equal(item, Item(("pk", S(pk)), ("bindata", B(payload)), ("rev", N("0")))),
            $"a rejected UpdateItem on an over-length operand must leave the item unchanged, got {Py.Repr(item)}");

        // A true byte prefix, shorter than the stored value: the condition holds and the write lands.
        await ddb.UpdateItemAsync(SetRev("1", [0x01, 0x02]));
        item = await ReadItem();
        Ensure(
            Py.Equal(item, Item(("pk", S(pk)), ("bindata", B(payload)), ("rev", N("1")))),
            $"UpdateItem under a holding begins_with prefix condition left {Py.Repr(item)}");

        // A byte string equal to the whole stored value is itself a true prefix.
        await ddb.UpdateItemAsync(SetRev("2", payload));
        item = await ReadItem();
        Ensure(
            Py.Equal(item, Item(("pk", S(pk)), ("bindata", B(payload)), ("rev", N("2")))),
            $"UpdateItem under an exact-length begins_with prefix condition left {Py.Repr(item)}");
    }
}
