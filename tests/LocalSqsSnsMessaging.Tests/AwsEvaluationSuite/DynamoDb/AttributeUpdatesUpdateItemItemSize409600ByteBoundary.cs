// Translated from behavioral/dynamodb/check_attribute_updates_update_item_item_size_409600_byte_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-updates-update-item-item-size-409600-byte-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeUpdatesUpdateItemItemSize409600ByteBoundary
{
    private const string Check = "attribute-updates-update-item-item-size-409600-byte-boundary";
    private const int LimitBytes = 409600;
    private const string Pk = "k";
    private const string Attr = "data";
    private static readonly (string Code, string Message) OverLimit = ("ValidationException", "Item size to update has exceeded the maximum allowed size");

    /// <summary>
    /// UpdateItem's legacy AttributeUpdates grows an item to exactly the 409,600-byte limit
    /// and reads back whole; one byte more is refused and the stored item is unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-updates-update-item-item-size-409600-byte-boundary")]
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

        var atLimit = Filler(LimitBytes);
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(Pk))),
            AttributeUpdates = new() { [Attr] = new AttributeValueUpdate { Value = S(atLimit), Action = AttributeAction.PUT } },
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(Pk))), ConsistentRead = true })).Item ?? [];
        var stored = got.GetValueOrDefault(Attr)?.S ?? "";
        Ensure(
            stored == atLimit,
            $"a legacy AttributeUpdates PUT growing the item to exactly {LimitBytes} bytes must round-trip " +
            $"whole, got {stored.Length} bytes back");

        var over = Filler(LimitBytes + 1);
        await ExpectError(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(Pk))),
                AttributeUpdates = new() { [Attr] = new AttributeValueUpdate { Value = S(over), Action = AttributeAction.PUT } },
            }),
            OverLimit.Code,
            OverLimit.Message);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(Pk))), ConsistentRead = true })).Item ?? [];
        Ensure(
            (unchanged.GetValueOrDefault(Attr)?.S ?? "") == atLimit,
            $"a legacy AttributeUpdates PUT refused for growing the item to {LimitBytes + 1} bytes " +
            $"must leave the stored {LimitBytes}-byte value unchanged");
    }

    /// <summary>An ASCII filler for <c>Attr</c> making the whole item exactly <paramref name="totalBytes"/>.</summary>
    private static string Filler(int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(Pk) + Encoding.UTF8.GetByteCount(Attr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return new string('x', fillerLen);
    }

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; an unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected {code} but the call succeeded: {Py.Repr(result)}");
    }
}
