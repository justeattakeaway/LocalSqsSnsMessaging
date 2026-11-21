// Translated from behavioral/dynamodb/check_batch_write_item_two_number_attributes_byte_cost_sum_pinned_at_400kb_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-two-number-attributes-byte-cost-sum-pinned-at-400kb-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemTwoNumberAttributesByteCostSumPinnedAt400kbBoundary
{
    private const string Check = "batch-write-item-two-number-attributes-byte-cost-sum-pinned-at-400kb-boundary";
    private const int LimitBytes = 409600;
    private static readonly (string Code, string Message) SizeErr = ("ValidationException", "Item size has exceeded the maximum allowed size");

    // Two large-exponent Numbers at the documented magnitude ceiling, one of each sign; each cost is
    // the exact byte count a binary search over the item size limit measures for that literal alone.
    private const string N1 = "9.9999999999999999999999999999999999999E+125";
    private const string N2 = "-9.9999999999999999999999999999999999999E+125";
    private const int N1Cost = 20;
    private const int N2Cost = 21;

    // format(Decimal(N), "f"): the 38 significant digits followed by 88 zeros.
    private static readonly string N1Stored = new string('9', 38) + new string('0', 88);
    private static readonly string N2Stored = "-" + N1Stored;

    /// <summary>
    /// BatchWriteItem sums the byte cost of two Number attributes in one item: an item built
    /// from two large-exponent Numbers at exactly 400 KB passes, and one byte over is refused with
    /// the item size error, writing nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-two-number-attributes-byte-cost-sum-pinned-at-400kb-boundary")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        async Task<Dictionary<string, AttributeValue>?> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        // --- at exactly 400 KB: accepted, and both Numbers round-trip in their canonical stored form ---
        var atLimit = ItemOfTotalSize("at-limit", LimitBytes);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = atLimit } }] },
        });
        var unprocessed = resp.UnprocessedItems ?? [];
        Ensure(
            unprocessed.Count == 0,
            $"a {LimitBytes}-byte item split across two Number attributes must fully process, "
            + $"got UnprocessedItems={Py.Repr(unprocessed.Keys)}");
        var got = await Get("at-limit");
        Ensure(Py.Equal(got!["n1"], N(N1Stored)), $"n1 must round-trip as {Py.Repr(N1Stored)}, got {Py.Repr(got["n1"])}");
        Ensure(Py.Equal(got["n2"], N(N2Stored)), $"n2 must round-trip as {Py.Repr(N2Stored)}, got {Py.Repr(got["n2"])}");
        Ensure(got["f"].S == atLimit["f"].S, "the filler must read back whole at exactly the limit");

        // --- one byte over, split the same way across the same two Numbers: refused, nothing written ---
        var over = ItemOfTotalSize("over-limit", LimitBytes + 1);
        await ExpectError(
            () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new() { [table] = [new WriteRequest { PutRequest = new PutRequest { Item = over } }] },
            }),
            SizeErr.Code,
            SizeErr.Message);
        Ensure(await Get("over-limit") is null, "a BatchWriteItem PutRequest rejected for size must not persist the item");

        // Positive control: the table still accepts an ordinary write after the rejection.
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("ctrl")), ("n1", N("1"))) } }],
            },
        });
        Ensure(Py.Equal((await Get("ctrl"))!["n1"], N("1")), "control write after the rejection must still succeed");
    }

    /// <summary>A pk + two N attributes (byte cost N1Cost, N2Cost) + filler item sized to totalBytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfTotalSize(string pkValue, int totalBytes)
    {
        var overhead = "pk".Length + pkValue.Length + "n1".Length + N1Cost + "n2".Length + N2Cost + "f".Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item's fixed attributes");
        return Item(("pk", S(pkValue)), ("n1", N(N1)), ("n2", N(N2)), ("f", S(new string('x', fillerLen))));
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises instead.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        Ensure(error.Code == code, $"expected {code}, got {error.Code}: {Py.Repr(error.Message)}");
        Ensure(error.Message == message, $"expected AWS's exact message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
    }
}
