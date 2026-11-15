// Translated from behavioral/dynamodb/check_50_table_fanout_item_isolation_and_scan_exactness.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.50-table-fanout-item-isolation-and-scan-exactness</summary>
[Category("AwsEvaluationSuite")]
public sealed class Check50TableFanoutItemIsolationAndScanExactness
{
    private const int N = 50;
    private const int Workers = 25;
    private const int Dupes = 10;
    private const int Page = 10;
    private const double ResidualDeadlineS = 300.0;
    private const double ActiveWaitS = 300.0;

    // deliberately IDENTICAL in all 51 tables
    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("ITEM")));

    /// <summary>
    /// Fifty concurrently created tables each hold one item under the SAME key, read back only from
    /// its own table with Scan Count == ScannedCount == 1; duplicate creates and post-delete calls fail
    /// Item storage must be keyed by (TableName, key), not by key alone.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.50-table-fanout-item-isolation-and-scan-exactness")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // ItemCount is refreshed only about every six hours, so it is never asserted.
        var ddb = ctx.DynamoClient(maxAttempts: 5);
        var stem = FidelityContext.UniqueName("sim2real-behav-d1-tbl-");

        async Task Make(string name)
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = name,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = "d1-table-fanout" }],
            });
            ctx.OnCleanup(() => DropTable(ddb, name));
        }

        // A table that exists before the fan-out and must be untouched by it.
        var sentinel = $"{stem}-sentinel";
        await Make(sentinel);
        await AwaitActive(ctx, ddb, sentinel);
        await ddb.PutItemAsync(sentinel, With(Key(), ("blob", B("SENTINEL"))));

        var tables = Enumerable.Range(0, N).Select(i => $"{stem}-{i.ToString("00", CultureInfo.InvariantCulture)}").ToList();
        var blobs = tables.ToDictionary(t => t, _ => RandomNumberGenerator.GetBytes(256), StringComparer.Ordinal);

        async Task<(string Name, string? Arn, string? Error)> Create(string name)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = name,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = "d1-table-fanout" }],
            }));
            if (error is not null)
            {
                return (name, null, $"{name}: {error.Code}");
            }

            ctx.OnCleanup(() => DropTable(ddb, name));
            return (name, resp!.TableDescription.TableArn, null);
        }

        (string Name, string? Arn, string? Error)[] creates;
        using (ctx.Concurrently())
        {
            creates = await PoolMap(Workers, tables, Create);
        }

        var failed = creates.Where(c => c.Error is not null).Select(c => c.Error).ToList();
        Ensure(failed.Count == 0, $"CreateTable failed for {failed.Count} of {N}: {Py.Repr(failed)}");
        var arns = creates.Select(c => c.Arn!).ToList();
        var distinct = arns.Distinct(StringComparer.Ordinal).ToList();
        Ensure(
            distinct.Count == N,
            $"{Py.Repr(N)} CreateTable calls returned only {Py.Repr(distinct.Count)} distinct TableArns: " +
            $"{Py.Repr(distinct.Order(StringComparer.Ordinal).Take(3).ToList())}");
        foreach (var (name, arn, _) in creates)
        {
            Ensure(arn!.EndsWith('/' + name, StringComparison.Ordinal), $"{Py.Repr(name)} was given the arn {Py.Repr(arn)}");
        }

        using (ctx.Concurrently())
        {
            await PoolMap(Workers, tables, async t =>
            {
                await AwaitActive(ctx, ddb, t);
                return 0;
            });
        }

        string[] statusList;
        using (ctx.Concurrently())
        {
            statusList = await PoolMap(Workers, tables, async t =>
                (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table.TableStatus.Value);
        }

        var states = tables.Zip(statusList).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
        Ensure(
            states.All(s => s.Value == "ACTIVE"),
            "a table did not reach ACTIVE: " + Py.Repr(states.Where(s => s.Value != "ACTIVE").ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal)));

        async Task<int> Put(string name)
        {
            await ddb.PutItemAsync(name, With(
                Key(),
                ("blob", B(blobs[name])),
                ("idx", N(tables.IndexOf(name).ToString(CultureInfo.InvariantCulture)))));
            return 0;
        }

        using (ctx.Concurrently())
        {
            await PoolMap(Workers, tables, Put);
        }

        async Task<(string Name, byte[] Blob, string Idx, int? Count, int? Scanned)> Read(string name)
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Key(), ConsistentRead = true })).Item;
            var scan = await ddb.ScanAsync(new ScanRequest { TableName = name, ConsistentRead = true });
            return (name, Bytes(item["blob"].B), item["idx"].N, scan.Count, scan.ScannedCount);
        }

        (string Name, byte[] Blob, string Idx, int? Count, int? Scanned)[] reads;
        using (ctx.Concurrently())
        {
            reads = await PoolMap(Workers, tables, Read);
        }

        var readMap = reads.ToDictionary(r => r.Name, r => r.Blob, StringComparer.Ordinal);
        Ensure(
            tables.All(t => readMap[t].AsSpan().SequenceEqual(blobs[t])),
            "the item read back from a table is not the item written to it: " +
            Py.Repr(tables.Where(t => !readMap[t].AsSpan().SequenceEqual(blobs[t]))
                .ToDictionary(t => t, t => (blobs[t][..8], readMap[t][..Math.Min(8, readMap[t].Length)]), StringComparer.Ordinal)));
        foreach (var (name, blob, idx, count, scanned) in reads)
        {
            Ensure(
                blob.Length == 256,
                $"{name}: the Binary attribute round-tripped as bytes of {Py.Repr(blob.Length)} bytes, expected 256 bytes");
            var expectedIdx = tables.IndexOf(name).ToString(CultureInfo.InvariantCulture);
            Ensure(idx == expectedIdx, $"{name}: idx reads {Py.Repr(idx)}, expected {Py.Repr(expectedIdx)}");
            Ensure(
                count == 1,
                $"{name}: Scan reports Count {Py.Repr(count)}, expected 1 -- {Py.Repr(N)} tables were each written " +
                "exactly one item, so any other number means the item store is shared");
            Ensure(scanned == 1, $"{name}: Scan reports ScannedCount {Py.Repr(scanned)} against Count {Py.Repr(count)}, expected 1");
        }

        var sent = await ddb.ScanAsync(new ScanRequest { TableName = sentinel, ConsistentRead = true });
        Ensure(
            sent.Count == 1 && sent.ScannedCount == 1,
            $"the sentinel table reports Count {Py.Repr(sent.Count)} / ScannedCount " +
            $"{Py.Repr(sent.ScannedCount)} after the fan-out, expected 1 / 1");
        var sentBlob = Bytes(sent.Items[0]["blob"].B);
        Ensure(
            sentBlob.AsSpan().SequenceEqual("SENTINEL"u8),
            $"the sentinel item now holds {Py.Repr(sentBlob[..Math.Min(16, sentBlob.Length)])}");

        async Task<(List<string> Names, int Pages)> Walk()
        {
            var names = new List<string>();
            string? start = null;
            var pages = 0;
            while (true)
            {
                var request = new ListTablesRequest { Limit = Page };
                if (!string.IsNullOrEmpty(start))
                {
                    request.ExclusiveStartTableName = start;
                }

                var resp = await ddb.ListTablesAsync(request);
                var page = resp.TableNames ?? [];
                Ensure(page.Count <= Page, $"ListTables returned {Py.Repr(page.Count)} names for Limit={Py.Repr(Page)}");
                names.AddRange(page);
                pages++;
                start = resp.LastEvaluatedTableName;
                if (string.IsNullOrEmpty(start))
                {
                    return (names, pages);
                }
            }
        }

        var (walk, pageCount) = await Walk();
        Ensure(
            walk.Count == walk.Distinct(StringComparer.Ordinal).Count(),
            "the paginated ListTables walk repeated a name -- ExclusiveStartTableName is not a cursor: " +
            Py.Repr(walk.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList()));
        var missing = tables.Append(sentinel).Except(walk, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"the ListTables walk of {Py.Repr(walk.Count)} names over {Py.Repr(pageCount)} pages omits {Py.Repr(missing)}");
        Ensure(
            pageCount >= 5,
            $"Limit={Py.Repr(Page)} over {Py.Repr(walk.Count)} tables produced only {Py.Repr(pageCount)} page(s); Limit was " +
            "ignored, so the walk was never a paginated walk");

        var dupes = tables.Take(Dupes).ToList();
        (string? Code, string? Message)[] codes;
        using (ctx.Concurrently())
        {
            codes = await PoolMap(Dupes, dupes, t => Error(
                () => ddb.CreateTableAsync(new CreateTableRequest
                {
                    TableName = t,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                    AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }),
                $"CreateTable on the existing name {Py.Repr(t)}"));
        }

        var expectedDupes = dupes.Select(t => ((string?)"ResourceInUseException", (string?)$"Table already exists: {t}")).ToList();
        Ensure(
            codes.SequenceEqual(expectedDupes),
            $"concurrent CreateTable on names that already exist returned {Py.Repr(codes)}, expected " +
            $"{Py.Repr(expectedDupes)}");
        ScanResponse[] afterScans;
        using (ctx.Concurrently())
        {
            afterScans = await PoolMap(Dupes, dupes, t => ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true }));
        }

        foreach (var (name, scan) in dupes.Zip(afterScans))
        {
            Ensure(
                scan.Count == 1,
                $"{name}: the rejected CreateTable emptied or duplicated the table -- Count is now " +
                $"{Py.Repr(scan.Count)}");
            Ensure(
                Bytes(scan.Items[0]["blob"].B).AsSpan().SequenceEqual(blobs[name]),
                $"{name}: the rejected CreateTable replaced the item blob");
        }

        async Task<string?> Rm(string name)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            return error is null ? null : $"{name}: {error.Code}";
        }

        List<string?> undeleted;
        using (ctx.Concurrently())
        {
            undeleted = (await PoolMap(Workers, tables, Rm)).Where(e => e is not null).ToList();
        }

        Ensure(undeleted.Count == 0, $"DeleteTable failed for: {Py.Repr(undeleted)}");

        async Task<bool> Still(string name)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name }));
            if (error is null)
            {
                return true;
            }

            Ensure(
                error.Code == "ResourceNotFoundException",
                $"{name}: DescribeTable after DeleteTable returned {Py.Repr(error.Code)}, expected " +
                "ResourceNotFoundException");
            var expectedMsg = $"Requested resource not found: Table: {name} not found";
            Ensure(
                error.Message == expectedMsg,
                $"{name}: DescribeTable after DeleteTable said {Py.Repr(error.Message)}, expected {Py.Repr(expectedMsg)}");
            return false;
        }

        // DeleteTable is asynchronous; poll DescribeTable to a hard deadline.
        var bound = BudgetS(ctx, ResidualDeadlineS);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            bool[] ups;
            using (ctx.Concurrently())
            {
                ups = await PoolMap(Workers, tables, Still);
            }

            var alive = tables.Zip(ups).Where(p => p.Second).Select(p => p.First).ToList();
            if (alive.Count == 0)
            {
                break;
            }

            if (clock.Elapsed.TotalSeconds >= bound && BudgetSpent(ctx))
            {
                throw new TimeoutException("the deleted tables leaving DescribeTable: the run budget ended first; not graded");
            }

            Ensure(
                clock.Elapsed.TotalSeconds < bound,
                $"{Py.Repr(alive.Count)} of {Py.Repr(N)} tables still answer DescribeTable " +
                $"{bound.ToString("F0", CultureInfo.InvariantCulture)}s after DeleteTable: {Py.Repr(alive.Order(StringComparer.Ordinal).ToList())}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        var (final, _) = await Walk();
        var still = tables.Intersect(final, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(still.Count == 0, $"ListTables still lists deleted tables: {Py.Repr(still)}");
        Ensure(
            final.Contains(sentinel),
            $"the sentinel table vanished from ListTables while the fan-out was deleted -- the delete " +
            $"reached beyond the {Py.Repr(N)} named tables");

        var (deadCode, deadMsg) = await Error(
            () => ddb.PutItemAsync(tables[0], With(Key(), ("blob", B("DEAD")))),
            $"PutItem against deleted table {Py.Repr(tables[0])}");
        Ensure(
            deadCode == "ResourceNotFoundException",
            $"PutItem against deleted table {Py.Repr(tables[0])} returned {Py.Repr(deadCode)}, expected " +
            "ResourceNotFoundException");
        Ensure(
            deadMsg == "Requested resource not found",
            $"PutItem against deleted table {Py.Repr(tables[0])} said {Py.Repr(deadMsg)}, expected " +
            "'Requested resource not found'");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>Poll DescribeTable every second until the table is ACTIVE, bounded by the budget.</summary>
    private static async Task AwaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        var bound = BudgetS(ctx, ActiveWaitS);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            string? status;
            try
            {
                status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = null;
            }

            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= bound && BudgetSpent(ctx))
            {
                throw new TimeoutException($"{name} ACTIVE: the run budget ended first; not graded");
            }

            Ensure(
                clock.Elapsed.TotalSeconds < bound,
                $"{name} did not reach ACTIVE within {bound.ToString("F0", CultureInfo.InvariantCulture)}s (last status {Py.Repr(status)})");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>Return the (Code, Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail($"{what} succeeded; real AWS refuses it");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Delete a table, tolerating only an already-absent one.</summary>
    private static async Task DropTable(IAmazonDynamoDB ddb, string name)
    {
        try
        {
            await ddb.DeleteTableAsync(name);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    /// <summary><c>{**base, ...}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> baseItem, params (string Name, AttributeValue Value)[] entries)
    {
        var item = new Dictionary<string, AttributeValue>(baseItem, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            item[name] = value;
        }

        return item;
    }

    /// <summary><c>ThreadPoolExecutor(max_workers=workers).map(fn, items)</c>: results in order.</summary>
    private static async Task<TResult[]> PoolMap<T, TResult>(int workers, IReadOnlyList<T> items, Func<T, Task<TResult>> fn)
    {
        using var gate = new SemaphoreSlim(workers);
        return await Task.WhenAll(items.Select(item => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                return await fn(item);
            }
            finally
            {
                gate.Release();
            }
        })));
    }
}
