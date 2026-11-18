// Translated from behavioral/dynamodb/check_item_size_limit_counts_list_element_overhead.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-size-limit-counts-list-element-overhead</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemSizeLimitCountsListElementOverhead
{
    private const string TooBigMessage = "Item size has exceeded the maximum allowed size";

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static AttributeValue RepeatedList(int count)
    {
        var element = S("aa");
        return new AttributeValue { L = [.. Enumerable.Repeat(element, count)] };
    }

    /// <summary>
    /// A 150,000-element list of 2-byte strings (300 KB of values, over 400 KB with element overhead) is
    /// refused and not stored; a 130,000-element one is stored and bills exactly 381 CapacityUnits.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-size-limit-counts-list-element-overhead")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-lsize-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        var resp = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("under")), ("data", RepeatedList(130_000))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cu = resp.ConsumedCapacity?.CapacityUnits;
        Ensure(cu == 381.0, $"the 130,000-element list item consumed {Py.Repr(cu)} CapacityUnits, expected 381.0");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("under"))), ConsistentRead = true })).Item ?? [];
        var count = stored.GetValueOrDefault("data")?.L?.Count ?? 0;
        Ensure(count == 130_000, $"the stored list reads back with {Py.Repr(count)} elements, expected 130000");

        var (code, msg) = await Error(() => ddb.PutItemAsync(table, Item(("pk", S("over")), ("data", RepeatedList(150_000)))));
        Ensure((code, msg) == ("ValidationException", TooBigMessage), $"the 150,000-element list item gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over"))), ConsistentRead = true });
        Ensure(gone.Item is null, $"the refused item was stored: {Py.Repr(gone.Item?.Keys.Order(StringComparer.Ordinal).ToList())}");
    }
}
