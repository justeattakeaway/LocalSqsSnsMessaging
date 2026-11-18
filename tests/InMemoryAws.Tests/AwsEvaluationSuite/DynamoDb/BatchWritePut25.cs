// Translated from behavioral/dynamodb/check_batch_write_put_25.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-put-25</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWritePut25
{
    private const string Check = "batch-write-put-25";

    /// <summary>BatchWriteItem accepts up to 25 put requests in one call and persists all of them.</summary>
    [Test]
    [DisplayName("dynamodb.batch-write-put-25")]
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
        var requests = Enumerable.Range(0, 25)
            .Select(i => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S($"bw-{i.ToString("000", CultureInfo.InvariantCulture)}")), ("data", S("x"))) } })
            .ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = requests } });
        var unprocessed = resp.UnprocessedItems?.GetValueOrDefault(table) ?? [];
        Ensure(unprocessed.Count == 0, $"expected all 25 puts accepted, got {unprocessed.Count} unprocessed: {Py.Repr(unprocessed.Select(w => w.PutRequest?.Item))}");
        var scan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, Select = Select.COUNT });
        Ensure(scan.Count == 25, $"expected 25 items persisted, found {scan.Count}");
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bw-000"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "expected item bw-000 to be retrievable after batch write");
        Ensure(got["data"].S == "x", $"expected data attribute 'x', got {Py.Repr(got.GetValueOrDefault("data"))}");
    }
}
