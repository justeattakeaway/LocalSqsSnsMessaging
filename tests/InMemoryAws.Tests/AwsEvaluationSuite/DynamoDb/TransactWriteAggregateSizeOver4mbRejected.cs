// Translated from behavioral/dynamodb/check_transact_write_aggregate_size_over_4mb_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-aggregate-size-over-4mb-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteAggregateSizeOver4mbRejected
{
    private const int FillerBytes = 390 * 1024;
    private const int OverLimitItems = 11;
    private const string TooLargeTemplate = "Transaction payload size cannot exceed 4MB. Payload Size: {0}";

    /// <summary>
    /// TransactWriteItems whose items are each under 400 KB but exceed 4 MB in aggregate is a
    /// ValidationException that writes none of them; two of the same items then commit in one transaction.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-aggregate-size-over-4mb-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-tx4mb-");

        var control = Enumerable.Range(0, 2).Select(i => MakeItem($"ok-{i:00}")).ToList();
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. control.Select(it => new TransactWriteItem { Put = new Put { TableName = table, Item = it } })],
        });
        foreach (var it in control)
        {
            var got = await Get(ddb, table, Item(("pk", it["pk"])));
            Ensure(
                Py.Equal(got, it),
                $"control item {Py.Repr(it["pk"])} read back with data length {DataLength(got)}, expected {FillerBytes}");
        }

        var big = Enumerable.Range(0, OverLimitItems).Select(i => MakeItem($"big-{i:00}")).ToList();
        var aggregate = big.Sum(ItemSize);
        Ensure(aggregate > 4 * 1024 * 1024, $"the over-limit items total only {aggregate} bytes");
        var expected = string.Format(CultureInfo.InvariantCulture, TooLargeTemplate, aggregate);
        var (code, msg, reasons) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. big.Select(it => new TransactWriteItem { Put = new Put { TableName = table, Item = it } })],
        }));
        Ensure(
            (code, msg) == ("ValidationException", expected),
            $"a {aggregate}-byte transaction gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException carried CancellationReasons {Py.Repr(reasons)}");
        var landed = new List<string>();
        foreach (var it in big)
        {
            if (await Get(ddb, table, Item(("pk", it["pk"]))) is not null)
            {
                landed.Add(it["pk"].S);
            }
        }

        Ensure(landed.Count == 0, $"the refused over-4 MB transaction wrote {Py.Repr(landed)}");

        var subset = big.Take(2).ToList();
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. subset.Select(it => new TransactWriteItem { Put = new Put { TableName = table, Item = it } })],
        });
        foreach (var it in subset)
        {
            var got = await Get(ddb, table, Item(("pk", it["pk"])));
            Ensure(
                Py.Equal(got, it),
                $"refused item {Py.Repr(it["pk"])} committed in a two-item transaction read back with data "
                + $"length {DataLength(got)}, expected {FillerBytes}");
        }
    }

    private static int DataLength(Dictionary<string, AttributeValue>? got) =>
        got is not null && got.TryGetValue("data", out var data) && data.S is not null ? data.S.Length : 0;

    /// <summary>An item of one short key and one ~390 KB string attribute.</summary>
    private static Dictionary<string, AttributeValue> MakeItem(string pk) => Item(("pk", S(pk)), ("data", S(new string('x', FillerBytes))));

    /// <summary>The DynamoDB size of an all-string item: attribute-name bytes plus value bytes.</summary>
    private static int ItemSize(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>Create an on-demand table keyed pk (S); wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the error <paramref name="fn"/> raises.</summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message, error.CancellationReasons());
    }

    /// <summary>The item at key read with ConsistentRead, or null.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
