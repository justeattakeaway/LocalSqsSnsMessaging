// Translated from behavioral/dynamodb/check_batch_write_item_oversized_item_among_valid_batch_rejected_whole.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-oversized-item-among-valid-batch-rejected-whole</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemOversizedItemAmongValidBatchRejectedWhole
{
    private const string Check = "batch-write-item-oversized-item-among-valid-batch-rejected-whole";
    private const string ErrValidation = "ValidationException";
    private const int LimitBytes = 409600;

    /// <summary>
    /// BatchWriteItem refuses the whole call with ValidationException when one of 25
    /// PutRequests exceeds the item size limit, writing none of the 24 valid siblings
    /// either, while the same 24 valid items alone are accepted in full.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-oversized-item-among-valid-batch-rejected-whole")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, table));
        await WaitTableActive(ddb, table);

        var validPks = Enumerable.Range(0, 24).Select(i => $"bw-mix-ok-{i.ToString("000", CultureInfo.InvariantCulture)}").ToList();
        List<WriteRequest> ValidRequests() =>
            [.. validPks.Select(pk => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(pk)), ("data", S("fine"))) } })];
        const string oversizedPk = "bw-mix-oversized";
        var oversizedItem = ItemOfSize(oversizedPk, "data", LimitBytes + 1);
        var mixedRequests = ValidRequests();
        mixedRequests.Add(new WriteRequest { PutRequest = new PutRequest { Item = oversizedItem } });

        var (code, message) = await Error(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = mixedRequests } }));
        Ensure(
            code == ErrValidation,
            $"a batch with one over-{LimitBytes}-byte PutRequest among 24 valid ones must be " +
            $"{ErrValidation} for the whole call, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Item size has exceeded the maximum allowed size",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        foreach (var pk in validPks.Take(3).Append(oversizedPk))
        {
            var after = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
            Ensure(
                after.Item is null,
                $"the request is rejected as a whole, so {Py.Repr(pk)} must not be written, " +
                $"got {Py.Repr(after.Item)}");
        }

        var controlResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = ValidRequests() } });
        var unprocessed = controlResp.UnprocessedItems?.GetValueOrDefault(table) ?? [];
        Ensure(
            unprocessed.Count == 0,
            "expected the same 24 valid puts alone to be fully accepted, " +
            $"got {unprocessed.Count} unprocessed: {Py.Repr(unprocessed.Select(w => w.PutRequest?.Item))}");
        var controlGot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(validPks[0]))), ConsistentRead = true })).Item;
        Ensure(
            controlGot is not null,
            $"expected control item {Py.Repr(validPks[0])} to be written once the oversized sibling is removed");
    }

    /// <summary>A one-pk, one-filler-attribute item whose attribute-value length totals total_bytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = Encoding.UTF8.GetByteCount("pk") + Encoding.UTF8.GetByteCount(pkValue) + Encoding.UTF8.GetByteCount(fillerAttr);
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(new string('x', fillerLen))));
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

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
