// Translated from behavioral/dynamodb/check_provisioned_throughput_exceeded_enforced_across_reads.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.provisioned-throughput-exceeded-enforced-across-reads</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProvisionedThroughputExceededEnforcedAcrossReads
{
    private const string CheckId = "provisioned-throughput-exceeded-enforced-across-reads";

    /// <summary>Runs every function concurrently, returning each one's result or the exception it raised.</summary>
    private static async Task<object?[]> RunConcurrent(IReadOnlyList<Func<Task<object>>> fns, FidelityContext ctx, double timeoutS = 60)
    {
        var results = new object?[fns.Count];
        using (ctx.Concurrently())
        {
            var tasks = fns.Select((fn, idx) => Task.Run(
                async () =>
                {
                    try
                    {
                        results[idx] = await fn();
                    }
#pragma warning disable CA1031 // The check records whatever each call raised, as the Python does.
                    catch (Exception exc)
#pragma warning restore CA1031
                    {
                        results[idx] = exc;
                    }
                },
                ctx.CancellationToken)).ToArray();
            var all = Task.WhenAll(tasks);
            var timeout = FidelityTime.Delay(TimeSpan.FromSeconds(timeoutS), ctx.CancellationToken);
            if (await Task.WhenAny(all, timeout) != all)
            {
                for (var i = 0; i < tasks.Length; i++)
                {
                    if (!tasks[i].IsCompleted)
                    {
                        results[i] = new TimeoutException();
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Concurrent GetItem, Query, Scan, BatchGetItem and TransactGetItems against a 1-RCU table
    /// raise ProvisionedThroughputExceededException on at least one read op; which ops are throttled
    /// depends on undocumented burst credit and arrival order, so no single op is required.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbThroughput, "Provisioned read capacity is not enforced, so no read is ever throttled with ProvisionedThroughputExceededException")]
    [DisplayName("dynamodb.provisioned-throughput-exceeded-enforced-across-reads")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // Retries off: the SDK would otherwise retry away the throttling errors this check counts.
        var ddb = ctx.DynamoClient(maxAttempts: 1);
        var retries = ((AmazonDynamoDBConfig)ddb.Config).MaxErrorRetry;
        Ensure(
            retries == 0,
            $"load client resolved retries to {retries}, expected total_max_attempts == 1 (no retries at all)");
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 400 },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Large items make each consistent read cost many RCU, exhausting burst credit quickly.
        var big = new string('x', 200_000);
        const string pk = "throughput-pk";
        static string Sk(int i) => i.ToString("000", CultureInfo.InvariantCulture);
        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S(Sk(i))), ("blob", S(big))));
        }

        // Positive control: an unloaded read succeeds, so rejecting every read cannot pass.
        const string ctlPk = "throughput-control", ctlSk = "ctl";
        await ddb.PutItemAsync(t, Item(("pk", S(ctlPk)), ("sk", S(ctlSk)), ("v", S("ok"))));
        var ctlResp = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", S(ctlPk)), ("sk", S(ctlSk))),
            ConsistentRead = true,
        });
        Ensure(
            ctlResp.Item?.GetValueOrDefault("v")?.S == "ok",
            $"a single unloaded consistent GetItem before any load returned {Py.Repr(ctlResp.Item)}, expected the control item -- the table must serve ordinary reads correctly before throughput enforcement is exercised");

        var ops = new List<(string Label, Func<Task<object>> Fn)>
        {
            ("GetItem", async () => await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = t, Key = Item(("pk", S(pk)), ("sk", S("000"))), ConsistentRead = true,
            })),
            ("Query", async () => await ddb.QueryAsync(new QueryRequest
            {
                TableName = t,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S(pk))),
                ConsistentRead = true,
            })),
            ("Scan", async () => await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true })),
            ("BatchGetItem", async () => await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
                {
                    [t] = new KeysAndAttributes
                    {
                        Keys = [.. Enumerable.Range(0, 5).Select(i => Item(("pk", S(pk)), ("sk", S(Sk(i)))))],
                        ConsistentRead = true,
                    },
                },
            })),
            ("TransactGetItems", async () => await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems = [new TransactGetItem { Get = new Get { TableName = t, Key = Item(("pk", S(pk)), ("sk", S("000"))) } }],
            })),
        };

        async Task<Dictionary<string, HashSet<string?>>> OneWave(int perOpN)
        {
            var fns = new List<Func<Task<object>>>();
            var labels = new List<string>();
            foreach (var (label, fn) in ops)
            {
                for (var n = 0; n < perOpN; n++)
                {
                    fns.Add(fn);
                    labels.Add(label);
                }
            }

            var results = await RunConcurrent(fns, ctx, timeoutS: 60);
            var codesByOp = ops.ToDictionary(o => o.Label, _ => new HashSet<string?>(), StringComparer.Ordinal);
            foreach (var (label, r) in labels.Zip(results))
            {
                if (r is AmazonServiceException exc)
                {
                    codesByOp[label].Add(AwsError.From(exc).Code);
                }
            }

            return codesByOp;
        }

        // A brand-new table may still carry a little burst credit; spend it in a first wave, then
        // assert enforcement on an immediately-following second wave.
        await OneWave(perOpN: 6);
        var codesByOp = await OneWave(perOpN: 6);

        var triggered = codesByOp.Where(e => e.Value.Contains("ProvisionedThroughputExceededException")).ToList();
        Ensure(
            triggered.Count > 0,
            $"expected ProvisionedThroughputExceededException on at least one read op under load against a 1-RCU table, observed: {Py.Repr(codesByOp.ToDictionary(e => e.Key, e => e.Value.ToList(), StringComparer.Ordinal))}");
    }
}
