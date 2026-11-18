// Translated from behavioral/dynamodb/check_batch_write_item_rounds_each_item_to_1kb_then_sums.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-rounds-each-item-to-1kb-then-sums</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemRoundsEachItemTo1kbThenSums
{
    private const string Check = "batch-write-item-rounds-each-item-to-1kb-then-sums";

    /// <summary>
    /// BatchWriteItem rounds each PutRequest item up to its own 1 KB boundary before summing:
    /// a 500-byte put plus a 3584-byte (3.5 KB) put in one call cost 5 write units together, not
    /// the 4 units that rounding their combined 4084 bytes once would give.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-rounds-each-item-to-1kb-then-sums")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-bwcu-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive controls: each size alone, rounded up on its own, costs its own documented unit count.
        var small = ItemOfSize("ctrl-small", "f", 500);
        var smallResp = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = small, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        var smallCu = smallResp.ConsumedCapacity.CapacityUnits;
        Ensure(smallCu == 1.0, $"a lone 500-byte PutItem consumed {Py.Repr(smallCu)} CapacityUnits, expected 1.0");

        var big = ItemOfSize("ctrl-big", "f", 3584);
        var bigResp = await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = big, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        var bigCu = bigResp.ConsumedCapacity.CapacityUnits;
        Ensure(bigCu == 4.0, $"a lone 3584-byte PutItem consumed {Py.Repr(bigCu)} CapacityUnits, expected 4.0");

        // The subject: both sizes in ONE BatchWriteItem call against the same table.
        var batchSmall = ItemOfSize("batch-small", "f", 500);
        var batchBig = ItemOfSize("batch-big", "f", 3584);
        var batchResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [table] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = batchSmall } },
                    new WriteRequest { PutRequest = new PutRequest { Item = batchBig } },
                ],
            },
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(
            (batchResp.UnprocessedItems ?? []).Count == 0,
            "the two-item batch must be accepted whole, got UnprocessedItems=" +
            $"{Py.Repr(batchResp.UnprocessedItems?.Keys)}");
        var cc = batchResp.ConsumedCapacity;
        Ensure(cc.Count == 1, $"expected one per-table capacity entry, got {Py.Repr(cc.Select(c => (c.TableName, c.CapacityUnits)))}");
        Ensure(cc[0].TableName == table, $"the capacity entry named table {Py.Repr(cc[0].TableName)}, expected {Py.Repr(table)}");
        var batchCu = cc[0].CapacityUnits;
        Ensure(
            batchCu == 5.0,
            $"a batch of a 500-byte put and a 3584-byte put consumed {Py.Repr(batchCu)} CapacityUnits, " +
            "expected 5.0: each item rounds up to its own 1 KB boundary (1.0 + 4.0) before the " +
            "table total sums them, rather than rounding their combined 4084 bytes once");

        var gotSmall = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-small"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            gotSmall.GetValueOrDefault("f")?.S == batchSmall["f"].S,
            "the 500-byte batched item must round-trip whole, got " +
            $"{(gotSmall.GetValueOrDefault("f")?.S ?? "").Length} bytes back");
        var gotBig = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("batch-big"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            gotBig.GetValueOrDefault("f")?.S == batchBig["f"].S,
            "the 3584-byte batched item must round-trip whole, got " +
            $"{(gotBig.GetValueOrDefault("f")?.S ?? "").Length} bytes back");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>A one-pk, one-filler-attribute item whose aggregate encoded size is exactly total_bytes.</summary>
    private static Dictionary<string, AttributeValue> ItemOfSize(string pkValue, string fillerAttr, int totalBytes)
    {
        var overhead = "pk".Length + pkValue.Length + fillerAttr.Length;
        var fillerLen = totalBytes - overhead;
        Ensure(fillerLen > 0, $"{totalBytes} bytes is too small for this item shape");
        return Item(("pk", S(pkValue)), (fillerAttr, S(new string('x', fillerLen))));
    }
}
