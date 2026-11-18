// Translated from behavioral/dynamodb/check_item_size_limit_enforced_at_400kb.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-size-limit-enforced-at-400kb</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemSizeLimitEnforcedAt400kb
{
    private const string CheckId = "item-size-limit-enforced-at-400kb";
    private const string ErrValidation = "ValidationException";
    private const int KB = 1024;

    /// <summary>
    /// The botocore error code and message <paramref name="fn"/> raises.
    ///
    /// An unexpected SUCCESS raises rather than returning None: a responder that ACCEPTS a request real
    /// AWS refuses is the strongest signal this check can produce.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// An item of about 380 KB reads back byte-identical; an item over 400 KB in one attribute or across many, and an
    /// UpdateItem growing an item past it, raise ValidationException with the exact messages and change nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-size-limit-enforced-at-400kb")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        const int under = 380 * KB;
        await ddb.PutItemAsync(t, Item(("pk", S("under")), ("blob", S(new string('u', under)))));
        var got = await Get("under") ?? [];
        var stored = got.GetValueOrDefault("blob")?.S ?? "";
        Ensure(stored.Length == under, $"a {under}-byte attribute must round-trip whole, got {stored.Length} bytes");
        Ensure(stored == new string('u', under), "the stored value must be byte-identical, not truncated or padded");

        const int over = 410 * KB;
        var (code, message) = await Code(() => ddb.PutItemAsync(t, Item(("pk", S("over")), ("blob", S(new string('o', over))))));
        Ensure(code == ErrValidation, $"a {over}-byte item exceeds the 400KB limit and must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "Item size has exceeded the maximum allowed size", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(await Get("over") is null, "an item rejected for size must not be stored");

        // The limit counts the whole item, not one attribute: many mid-sized
        // attributes that each pass individually still exceed the aggregate.
        var spread = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        for (var i = 0; i < 11; i++)
        {
            spread[$"a{i}"] = S(new string('x', 40 * KB));
        }

        spread["pk"] = S("spread");
        (code, message) = await Code(() => ddb.PutItemAsync(t, spread));
        Ensure(code == ErrValidation, $"11 x 40KB attributes exceed 400KB in aggregate and must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == "Item size has exceeded the maximum allowed size", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(await Get("spread") is null, "an item rejected for aggregate size must not be stored");

        // The limit is enforced on the update path too: growing a stored item past
        // the aggregate bound is refused, and the item already stored is unchanged.
        const int grown = 390 * KB;
        await ddb.PutItemAsync(t, Item(("pk", S("grow")), ("blob", S(new string('g', grown)))));
        const int addition = 30 * KB;
        (code, message) = await Code(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("grow"))),
            UpdateExpression = "SET extra = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', addition)))),
        }));
        Ensure(
            code == ErrValidation,
            $"growing a {grown}-byte item by {addition} bytes exceeds the 400KB limit and must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(
            message == "Item size to update has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message for the update path, got {Py.Repr(message)}");
        var untouched = await Get("grow") ?? [];
        Ensure(
            !untouched.ContainsKey("extra"),
            $"the refused UpdateItem SET must leave the existing item unchanged, got attribute 'extra' in {Py.Repr(untouched.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(untouched.GetValueOrDefault("blob")?.S == new string('g', grown), "the existing item's stored attribute must be unaffected by the refused growth");
    }
}
