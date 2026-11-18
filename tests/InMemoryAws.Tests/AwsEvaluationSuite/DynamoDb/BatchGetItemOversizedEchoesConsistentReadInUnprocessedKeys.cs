// Translated from behavioral/dynamodb/check_batch_get_item_oversized_echoes_consistent_read_in_unprocessed_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-oversized-echoes-consistent-read-in-unprocessed-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemOversizedEchoesConsistentReadInUnprocessedKeys
{
    private const string Check = "batch-get-item-oversized-echoes-consistent-read-in-unprocessed-keys";

    // 44 items of 380 KiB total about 17.1 MB, just over the 16 MB BatchGetItem response limit.
    private const int NItems = 44;
    private const int PayloadBytes = 380 * 1024;

    /// <summary>
    /// A BatchGetItem too large to answer in one response defers keys into UnprocessedKeys that
    /// carries the same ConsistentRead value the request asked for, true or false.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-oversized-echoes-consistent-read-in-unprocessed-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-bgicr-");
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

        var payload = new string('z', PayloadBytes);
        var allKeys = Enumerable.Range(0, NItems).Select(i => $"k{i.ToString("00", CultureInfo.InvariantCulture)}").ToList();
        foreach (var key in allKeys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key)), ("pad", S(payload))));
        }

        foreach (var consistentRead in new[] { true, false })
        {
            var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new()
                {
                    [table] = new KeysAndAttributes { Keys = [.. allKeys.Select(k => Item(("pk", S(k))))], ConsistentRead = consistentRead },
                },
            });
            var spec = resp.UnprocessedKeys?.GetValueOrDefault(table);
            Ensure(
                spec is not null,
                $"{Py.Repr(NItems)} items of {Py.Repr(PayloadBytes)} bytes must not fit one response, so " +
                $"ConsistentRead={Py.Repr(consistentRead)} should leave {table} in UnprocessedKeys, got " +
                $"{Py.Repr(resp.UnprocessedKeys?.Keys)}");
            Ensure(
                spec.ConsistentRead == consistentRead,
                $"UnprocessedKeys for ConsistentRead={Py.Repr(consistentRead)} echoed " +
                $"{Py.Repr(spec.ConsistentRead)}");
        }
    }
}
