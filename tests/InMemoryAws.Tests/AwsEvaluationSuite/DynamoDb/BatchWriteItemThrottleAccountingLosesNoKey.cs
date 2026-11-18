// Translated from behavioral/dynamodb/check_batch_write_item_throttle_accounting_loses_no_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-throttle-accounting-loses-no-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemThrottleAccountingLosesNoKey
{
    private const string Check = "batch-write-item-throttle-accounting-loses-no-key";
    private const int PerBatch = 25;                 // a fixed API constant
    // Burst/adaptive capacity absorbs a few hundred writes at 1 WCU; 8,000 throttles reliably.
    private const int Batches = 320;
    private const int Total = PerBatch * Batches;
    private const string ThrottleMsg = "The level of configured provisioned throughput for the table was exceeded. " +
                                       "Consider increasing your provisioning level with the UpdateTable API.";
    private const int DrainBudgetS = 600;
    private const int TableActiveBudgetS = 300;
    private static readonly string Payload = new('x', 900); // ≈1 KB item -> 1 write unit

    /// <summary>
    /// At 1 WCU, BatchWriteItem returns partial 200s whose UnprocessedItems plus the table contents
    /// partition the request exactly, and raising the write budget lands precisely the withheld keys.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbThroughput, "provisioned throughput is never enforced, so all 8,000 writes land against a 1-WCU table and nothing is reported unprocessed")]
    [DisplayName("dynamodb.batch-write-item-throttle-accounting-loses-no-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // --- positive control FIRST: the batch shape itself is well formed and, given capacity, is
        // --- accepted whole in one call. Without this, a residue below proves nothing about capacity.
        var control = await CreateTable(ctx, "control");
        var controlPks = Enumerable.Range(0, PerBatch).Select(i => $"CTRL#{i.ToString("0000", CultureInfo.InvariantCulture)}").ToList();
        var controlResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [control] = Requests(controlPks) } });
        var controlResidue = controlResp.UnprocessedItems ?? [];
        Ensure(
            controlResidue.Count == 0,
            $"positive control: a {Py.Repr(PerBatch)}-item BatchWriteItem against a PAY_PER_REQUEST table " +
            $"must be accepted whole in one call, got residue {Py.Repr(controlResidue.Keys)}");
        var controlScanned = (await ScanPks(ddb, control)).Order(StringComparer.Ordinal).ToList();
        Ensure(
            controlScanned.SequenceEqual(controlPks),
            $"positive control: all {Py.Repr(PerBatch)} control items must be readable, got " +
            $"{Py.Repr(controlScanned)}");

        // --- the subject: the same shape, one thing broken -- a 1-unit write budget.
        // ReadCapacityUnits is generous so the strongly-consistent verification Scan over _TOTAL items
        // is not itself throttled.
        var subject = await CreateTable(ctx, "subject", new ProvisionedThroughput { ReadCapacityUnits = 2000, WriteCapacityUnits = 1 });

        var requested = Enumerable.Range(0, Total).Select(i => $"ITEM#{i.ToString("0000", CultureInfo.InvariantCulture)}").ToList();
        var reportedBack = new HashSet<string>(StringComparer.Ordinal);
        var partial200s = 0;
        var allRefusedExceptions = 0;
        for (var batchIndex = 0; batchIndex < Batches; batchIndex++)
        {
            var chunk = requested.Skip(batchIndex * PerBatch).Take(PerBatch).ToList();
            var (resp, error) = await FidelityContext.Capture(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [subject] = Requests(chunk) } }));
            if (error is not null)
            {
                Ensure(
                    error.Code == "ProvisionedThroughputExceededException",
                    $"batch {Py.Repr(batchIndex)}: when NO item in a BatchWriteItem can be processed for want " +
                    "of provisioned throughput, the documented failure is " +
                    $"ProvisionedThroughputExceededException, got {Py.Repr(error.Code)} / " +
                    $"{Py.Repr(error.Message)}");
                Ensure(
                    error.Message == ThrottleMsg,
                    $"batch {Py.Repr(batchIndex)}: ProvisionedThroughputExceededException carried the message " +
                    $"{Py.Repr(error.Message)}, expected {Py.Repr(ThrottleMsg)}");
                allRefusedExceptions++;
                reportedBack.UnionWith(chunk);
                continue;
            }

            var residue = (resp!.UnprocessedItems?.GetValueOrDefault(subject) ?? []).Select(r => r.PutRequest.Item["pk"].S).ToList();
            Ensure(
                residue.Count < PerBatch,
                $"batch {Py.Repr(batchIndex)}: a 200 response must be a PARTIAL success -- the all-refused " +
                "case is documented to raise ProvisionedThroughputExceededException instead -- so " +
                $"UnprocessedItems must be a strict subset of the {Py.Repr(PerBatch)} requests, got " +
                $"{Py.Repr(residue.Count)}");
            if (residue.Count > 0)
            {
                partial200s++;
            }

            reportedBack.UnionWith(residue);
        }

        // --- the accounting must close, and it must close on a strongly consistent read of the table
        // --- rather than on anything the mutating calls said about themselves.
        var landed = await ScanPks(ddb, subject);
        var landedSet = landed.ToHashSet(StringComparer.Ordinal);
        Ensure(
            landed.Count == landedSet.Count,
            $"the table reported {Py.Repr(landed.Count)} rows with only {Py.Repr(landedSet.Count)} distinct keys, so " +
            "the Scan itself is duplicating and nothing below can be trusted");
        Ensure(
            landedSet.Count < Total,
            $"a 1-unit provisioned write budget must not absorb {Py.Repr(Total)} ~1 KB writes issued back to " +
            $"back: {Py.Repr(landedSet.Count)} of {Py.Repr(Total)} landed, with {Py.Repr(reportedBack.Count)} keys reported " +
            $"as unprocessed across {Py.Repr(partial200s)} partial 200s and {Py.Repr(allRefusedExceptions)} " +
            "all-refused exceptions -- i.e. this target enforced no write capacity at all");
        var silentlyLost = requested.Where(k => !landedSet.Contains(k) && !reportedBack.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Ensure(
            silentlyLost.Count == 0,
            $"{Py.Repr(silentlyLost.Count)} keys were neither written nor reported back, so a caller draining " +
            $"UnprocessedItems would lose them for good; e.g. {Py.Repr(silentlyLost.Take(10).ToList())}");
        var writtenButReported = landedSet.Where(reportedBack.Contains).Order(StringComparer.Ordinal).ToList();
        Ensure(
            writtenButReported.Count == 0,
            $"{Py.Repr(writtenButReported.Count)} keys were reported as UNPROCESSED and are nevertheless in " +
            "the table, so a caller retrying the residue would double-apply them; e.g. " +
            $"{Py.Repr(writtenButReported.Take(10).ToList())}");
        var union = landedSet.Union(reportedBack, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Ensure(
            union.SetEquals(requested),
            $"the two sets must partition the request exactly: landed {Py.Repr(landedSet.Count)} + reported " +
            $"{Py.Repr(reportedBack.Count)} != requested {Py.Repr(Total)}; extras " +
            $"{Py.Repr(union.Except(requested, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(10).ToList())}");

        // --- repair the ONE broken thing and demand the withheld writes ---
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = subject,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 2000, WriteCapacityUnits = 500 },
        });
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = subject })).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.ProvisionedThroughput.WriteCapacityUnits == 500,
            timeoutS: TableActiveBudgetS,
            intervalS: 5.0,
            description: "the raised write budget to be ACTIVE, so the withheld writes can be demanded");
        var outstanding = reportedBack.Order(StringComparer.Ordinal).ToList();
        var clock = FidelityTime.StartNew();
        var backoff = 1.0;
        while (outstanding.Count > 0)
        {
            if (clock.Elapsed.TotalSeconds >= DrainBudgetS)
            {
                throw Fail(
                    $"after raising the write budget to 500 units, {Py.Repr(outstanding.Count)} of the " +
                    $"{Py.Repr(reportedBack.Count)} withheld keys still would not write within " +
                    $"{Py.Repr(DrainBudgetS)}s; e.g. {Py.Repr(outstanding.Take(10).ToList())}");
            }

            var chunk = outstanding.Take(PerBatch).ToList();
            outstanding = outstanding.Skip(PerBatch).ToList();
            var residue = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [subject] = Requests(chunk) } }))
                .UnprocessedItems?.GetValueOrDefault(subject) ?? [];
            if (residue.Count > 0)
            {
                outstanding = [.. residue.Select(r => r.PutRequest.Item["pk"].S), .. outstanding];
                await FidelityTime.Delay(TimeSpan.FromSeconds(backoff));
                backoff = Math.Min(backoff * 2, 8.0);
            }
        }

        var final = await ScanPks(ddb, subject);
        var sortedFinal = final.Order(StringComparer.Ordinal).ToList();
        Ensure(
            sortedFinal.SequenceEqual(requested),
            "once the write budget is raised, the identical BatchWriteItem calls must land exactly the " +
            $"{Py.Repr(Total)} requested keys and nothing else: the table holds {Py.Repr(final.Count)}, symmetric " +
            $"difference {Py.Repr(SymmetricDifference(final, requested).Take(10).ToList())}");
    }

    /// <summary>Poll probe() until predicate holds. RAISES on timeout -- never returns the last value.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not observed within {timeoutS.ToString(CultureInfo.InvariantCulture)}s; last saw {Py.Repr(last)}");
    }

    private static Dictionary<string, AttributeValue> ItemFor(string pk) => Item(("pk", S(pk)), ("payload", S(Payload)));

    private static List<WriteRequest> Requests(IEnumerable<string> pks) =>
        [.. pks.Select(pk => new WriteRequest { PutRequest = new PutRequest { Item = ItemFor(pk) } })];

    /// <summary>Every pk currently in the table, via a paginated strongly-consistent Scan.</summary>
    private static async Task<List<string>> ScanPks(IAmazonDynamoDB ddb, string table)
    {
        var got = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ProjectionExpression = "pk",
                ExclusiveStartKey = start,
            });
            got.AddRange(page.Items.Select(i => i["pk"].S));
            if (page.LastEvaluatedKey is null)
            {
                return got;
            }

            start = page.LastEvaluatedKey;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, string purpose, ProvisionedThroughput? throughput = null)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName($"sim2real-behav-bwthrottle-{purpose}-");
        var request = new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:check", Value = Check },
            ],
        };
        if (throughput is null)
        {
            request.BillingMode = BillingMode.PAY_PER_REQUEST;
        }
        else
        {
            request.BillingMode = BillingMode.PROVISIONED;
            request.ProvisionedThroughput = throughput;
        }

        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static List<string> SymmetricDifference(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = a.ToHashSet(StringComparer.Ordinal);
        set.SymmetricExceptWith(b);
        return [.. set.Order(StringComparer.Ordinal)];
    }
}
