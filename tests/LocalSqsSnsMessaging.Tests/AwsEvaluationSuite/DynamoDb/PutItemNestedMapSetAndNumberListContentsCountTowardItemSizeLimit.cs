// Translated from behavioral/dynamodb/check_put_item_nested_map_set_and_number_list_contents_count_toward_item_size_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-nested-map-set-and-number-list-contents-count-toward-item-size-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemNestedMapSetAndNumberListContentsCountTowardItemSizeLimit
{
    private const string ErrValidation = "ValidationException";
    private const string TooBigMessage = "Item size has exceeded the maximum allowed size";
    private static readonly string Digits38 = new('9', 38);

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null)
            {
                return;
            }

            if (notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadlineS = Math.Max(0.0, (timeLeft == 0 ? 300.0 : timeLeft) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadlineS)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<PutItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static AttributeValue NestedMap(int n, int fillerLen)
    {
        var entries = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            entries[$"k{i}"] = M(("v", S(new string('x', fillerLen))));
        }

        return new AttributeValue { M = entries };
    }

    private static List<string> StringSet(int n, int memberLen) =>
        Enumerable.Range(0, n).Select(i => i.ToString("D4", CultureInfo.InvariantCulture) + new string('s', memberLen - 4)).ToList();

    private static AttributeValue NumberList(int count) =>
        new() { L = Enumerable.Range(0, count).Select(_ => N(Digits38)).ToList() };

    private static List<string> SortedKeys(Dictionary<string, AttributeValue> item) => item.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// PutItem counts a nested map's values, a string set's members and a Number list's
    /// elements toward the 400 KB item size limit, not only a top-level scalar attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-nested-map-set-and-number-list-contents-count-toward-item-size-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-contsize-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);

        // Nested map: 250 sub-maps of a 2000-byte string (500,000 bytes of nested content) is
        // refused; 10 of the same shape (20,000 bytes) is accepted and reads back whole.
        await ddb.PutItemAsync(table, Item(("pk", S("nested-ok")), ("data", NestedMap(10, 2000))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nested-ok"))), ConsistentRead = true })).Item ?? [];
        var entries = got.GetValueOrDefault("data")?.M ?? [];
        Ensure(entries.Count == 10, $"the 10-entry nested map read back with {entries.Count} entries");
        Ensure(entries["k3"].M["v"].S == new string('x', 2000), "a nested map value did not round-trip whole");

        var (code, msg) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("nested-over")), ("data", NestedMap(250, 2000)))));
        Ensure((code, msg) == (ErrValidation, TooBigMessage), $"a 500,000-byte nested map item gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nested-over"))), ConsistentRead = true });
        Ensure(gone.Item is null, $"the refused nested-map item was stored: {Py.Repr(gone.Item is null ? null : SortedKeys(gone.Item))}");

        // String set: 10 members of 4200 bytes each (42,000 bytes) is accepted and reads back
        // as the same set; 100 members of the same size (420,000 bytes) is refused.
        var smallSet = StringSet(10, 4200);
        await ddb.PutItemAsync(table, Item(("pk", S("ss-ok")), ("data", new AttributeValue { SS = smallSet })));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ss-ok"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            (got.GetValueOrDefault("data")?.SS ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(smallSet),
            "the 10-member string set did not round-trip whole");

        var bigSet = StringSet(100, 4200);
        (code, msg) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("ss-over")), ("data", new AttributeValue { SS = bigSet }))));
        Ensure((code, msg) == (ErrValidation, TooBigMessage), $"a 100-member, 420,000-byte string set item gave {Py.Repr(code)}: {Py.Repr(msg)}");
        gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ss-over"))), ConsistentRead = true });
        Ensure(gone.Item is null, $"the refused string-set item was stored: {Py.Repr(gone.Item is null ? null : SortedKeys(gone.Item))}");

        // Number list: each 38-digit element costs 20 bytes; 15,000 of them (315,000 bytes) is
        // accepted and reads back intact; 21,000 of them (441,000 bytes) is refused.
        await ddb.PutItemAsync(table, Item(("pk", S("nlist-ok")), ("data", NumberList(15_000))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nlist-ok"))), ConsistentRead = true })).Item ?? [];
        var numbers = got.GetValueOrDefault("data")?.L ?? [];
        Ensure(numbers.Count == 15_000, $"the stored number list reads back with {numbers.Count} elements");
        Ensure(numbers.All(n => Py.Equal(n, N(Digits38))), "a number list element did not round-trip as the exact N string");

        (code, msg) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("nlist-over")), ("data", NumberList(21_000)))));
        Ensure((code, msg) == (ErrValidation, TooBigMessage), $"a 21,000-element 38-digit number list item gave {Py.Repr(code)}: {Py.Repr(msg)}");
        gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("nlist-over"))), ConsistentRead = true });
        Ensure(gone.Item is null, $"the refused number-list item was stored: {Py.Repr(gone.Item is null ? null : SortedKeys(gone.Item))}");
    }
}
