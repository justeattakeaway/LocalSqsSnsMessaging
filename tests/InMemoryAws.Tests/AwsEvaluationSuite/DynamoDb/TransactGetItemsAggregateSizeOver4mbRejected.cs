// Translated from behavioral/dynamodb/check_transact_get_items_aggregate_size_over_4mb_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-aggregate-size-over-4mb-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsAggregateSizeOver4mbRejected
{
    private const int FillerBytes = 390 * 1024;
    private const int OverLimitItems = 11;
    private const int UnderLimitItems = 10;
    private static readonly (string Code, string Message) TooLarge = ("ValidationException", "Payload in response is too large.");

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
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

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>Create an on-demand table keyed pk (S) and wait until ACTIVE.</summary>
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
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>
    /// (Error.Code, Error.Message, CancellationReasons) of the ClientError fn() raises.
    /// An unexpected success fails, quoting the result.
    /// </summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message, error.CancellationReasons());
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>An item of one short key and one ~390 KB string attribute.</summary>
    private static Dictionary<string, AttributeValue> MakeItem(string pk) => Item(("pk", S(pk)), ("data", S(new string('x', FillerBytes))));

    /// <summary>The DynamoDB size of an all-string item: attribute-name bytes plus value bytes.</summary>
    private static int ItemSize(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    /// <summary>
    /// TransactGetItems whose addressed items are each under 400 KB but exceed 4 MB in aggregate is
    /// a ValidationException that returns none of them; the same items minus one stay under 4 MB and
    /// every one of them comes back whole.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-aggregate-size-over-4mb-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txget4mb-");

        var big = Enumerable.Range(0, OverLimitItems).Select(i => MakeItem($"big-{i.ToString("00", CultureInfo.InvariantCulture)}")).ToList();
        foreach (var it in big)
        {
            await ddb.PutItemAsync(table, it);
        }

        var under = big.Take(UnderLimitItems).ToList();
        var underAggregate = under.Sum(ItemSize);
        Ensure(underAggregate < 4 * 1024 * 1024, $"the under-limit items total {underAggregate} bytes");
        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [.. under.Select(it => new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", it["pk"])) } })],
        });
        var responses = resp.Responses.Select(r => r.Item).ToList();
        Ensure(
            Py.Equal(responses, under),
            $"a {underAggregate}-byte TransactGetItems returned {responses.Count} item(s), expected {under.Count}");

        var aggregate = big.Sum(ItemSize);
        Ensure(aggregate > 4 * 1024 * 1024, $"the over-limit items total only {aggregate} bytes");
        var (code, msg, reasons) = await Error(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [.. big.Select(it => new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", it["pk"])) } })],
        }));
        Ensure((code, msg) == TooLarge, $"a {aggregate}-byte TransactGetItems gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException carried CancellationReasons {Py.Repr(reasons?.Select(r => r.Code).ToList())}");

        var survivor = big[^1];
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", survivor["pk"])), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got, survivor),
            $"after the refused over-4 MB read, GetItem on {Py.Repr(survivor["pk"])} returned {(got is null ? "None" : "a different item")}");
    }
}
