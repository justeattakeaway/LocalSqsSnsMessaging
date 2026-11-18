// Translated from behavioral/dynamodb/check_ddb_eventual_and_strong_read_are_two_different_reads.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-eventual-and-strong-read-are-two-different-reads</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbEventualAndStrongReadAreTwoDifferentReads
{
    private const string Check = "ddb-eventual-and-strong-read-are-two-different-reads";

    // A DynamoDB item's billable size is the sum, over every attribute, of the UTF-8 length of the
    // name plus the length of the value. WCU = ceil(size / 1024) and a strong RCU = ceil(size / 4096);
    // an eventually consistent read of the same item is exactly half that. So the 2049..3072 band bills
    // 3 WCU / 1.0 strong / 0.5 eventual RCU, and the 4097..5120 band bills 5 WCU / 2.0 / 1.0.
    private const int Band3Kb = 3 * 1024;
    private const int Band5Kb = 5 * 1024;

    private const int Pairs = 200;
    private const string Gsi = "gk-index";

    /// <summary>
    /// Padding that lands the whole item just inside <paramref name="band"/> bytes.
    /// Overhead accounted for: the "pk" name (2) + its value, the "pad" name (3), the "v" name (1)
    /// and the numeric value of v (&lt;= 8 bytes of slack covers every value this check writes).
    /// </summary>
    private static string PadFor(int band, string pk) => new('x', band - pk.Length - 2 - 3 - 1 - 8);

    private sealed class Spec
    {
        public int Band { get; init; }
        public double Wcu { get; init; }
        public double Strong { get; init; }
        public double Eventual { get; init; }
        public int V { get; set; }
    }

    /// <summary>
    /// Strong and eventual reads of one item bill 1.0/0.5 RCU up to 4 KB and 2.0/1.0 above; strong
    /// reads are never stale, eventual reads return some written value (canary 0 included) and never a
    /// newer one, and a GSI refuses ConsistentRead=True with an exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-eventual-and-strong-read-are-two-different-reads")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbcr-");

        // On-demand reports read units unrounded, so the 0.5 RCU of an eventual read is observable.
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTable(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var items = new Dictionary<string, Spec>(StringComparer.Ordinal)
        {
            ["small"] = new() { Band = Band3Kb, Wcu = 3.0, Strong = 1.0, Eventual = 0.5, V = 0 },
            ["large"] = new() { Band = Band5Kb, Wcu = 5.0, Strong = 2.0, Eventual = 1.0, V = 0 },
        };
        foreach (var (pk, spec) in items)
        {
            var put = await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(pk)), ("pad", S(PadFor(spec.Band, pk))), ("v", N("0"))),
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            var got = put.ConsumedCapacity.CapacityUnits;
            Ensure(
                got == spec.Wcu,
                $"canary write of the {spec.Band}-byte {Py.Repr(pk)} item billed {Py.Repr(got)} WCU, expected " +
                $"{Py.Repr(spec.Wcu)}. The RCU assertions below rest on this arithmetic, so a mismatch " +
                "here means the item is not the size this check believes it is -- fix the padding, do " +
                "not relax the read assertions.");
        }

        var staleStrong = new List<(int, string, object?)>();
        var phantomEventual = new List<(int, string, object?)>();
        var futureEventual = new List<(int, string, object?)>();
        var badCapacity = new List<(int, string, string, double?, double)>();
        var disagreements = 0;

        for (var i = 0; i < Pairs; i++)
        {
            var pk = i % 2 == 0 ? "small" : "large";
            var spec = items[pk];
            spec.V += 1;
            var want = spec.V;
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(pk)), ("pad", S(PadFor(spec.Band, pk))), ("v", N(want.ToString(CultureInfo.InvariantCulture)))),
            });

            async Task<(string? Value, double? Units)> Read(bool consistent)
            {
                var resp = await ddb.GetItemAsync(new GetItemRequest
                {
                    TableName = table,
                    Key = Item(("pk", S(pk))),
                    ConsistentRead = consistent,
                    ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
                });
                var value = resp.Item is not null ? resp.Item["v"].N : null;
                return (value, resp.ConsumedCapacity.CapacityUnits);
            }

            // Alternate which read goes first so neither mode is systematically advantaged.
            string? strongV, eventualV;
            double? strongCu, eventualCu;
            if (i % 4 < 2)
            {
                (strongV, strongCu) = await Read(true);
                (eventualV, eventualCu) = await Read(false);
            }
            else
            {
                (eventualV, eventualCu) = await Read(false);
                (strongV, strongCu) = await Read(true);
            }

            if (strongV != want.ToString(CultureInfo.InvariantCulture))
            {
                staleStrong.Add((i, pk, strongV));
            }

            // Every value 0..want was written to this key (0 is the canary), so any of them is lag.
            if (eventualV is null || eventualV.Length == 0 || !eventualV.All(char.IsAsciiDigit))
            {
                phantomEventual.Add((i, pk, eventualV));
            }
            else if (int.Parse(eventualV, CultureInfo.InvariantCulture) > want)
            {
                futureEventual.Add((i, pk, eventualV));
            }

            if (strongCu != spec.Strong)
            {
                badCapacity.Add((i, pk, "strong", strongCu, spec.Strong));
            }

            if (eventualCu != spec.Eventual)
            {
                badCapacity.Add((i, pk, "eventual", eventualCu, spec.Eventual));
            }

            if (strongV != eventualV)
            {
                disagreements += 1;
            }
        }

        Ensure(
            staleStrong.Count == 0,
            $"{staleStrong.Count} of {Pairs} ConsistentRead=True reads were STALE -- a strong read " +
            "issued after a completed PutItem must never miss it. First offenders " +
            $"(iteration, item, value returned): {Py.Repr(staleStrong.Take(5).ToList())}");
        Ensure(
            phantomEventual.Count == 0,
            $"{phantomEventual.Count} of {Pairs} ConsistentRead=False reads returned a value that was " +
            "never written (or no item at all). Lag is permitted; invention is not. First offenders: " +
            $"{Py.Repr(phantomEventual.Take(5).ToList())}");
        Ensure(
            futureEventual.Count == 0,
            $"{futureEventual.Count} of {Pairs} ConsistentRead=False reads returned a value NEWER than " +
            "the write that preceded them -- an eventual read may lag the leader, never lead it. " +
            $"First offenders: {Py.Repr(futureEventual.Take(5).ToList())}");

        Ensure(
            badCapacity.Count == 0,
            $"{badCapacity.Count} of {2 * Pairs} reads billed the wrong capacity. A strong read of a " +
            "<=4 KB item costs 1.0 RCU and the eventual read of the same item costs exactly half, " +
            "0.5; across the 4 KB boundary both double to 2.0 and 1.0. An emulator that keeps one " +
            "copy of the data has no second read path to bill differently. First offenders " +
            $"(iteration, item, mode, got, expected): {Py.Repr(badCapacity.Take(6).ToList())}");
        Ensure(
            disagreements < Pairs,
            $"every one of the {Pairs} eventual reads disagreed with the strong read taken in the " +
            "same iteration. Lag is expected; being wrong 200/200 means the eventual read is not " +
            "tracking the table at all.");

        // A GetItem miss still consumes read capacity at the same 2:1 ratio.
        foreach (var (consistent, wantCu) in new[] { (true, 1.0), (false, 0.5) })
        {
            var miss = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("never-written-key"))),
                ConsistentRead = consistent,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            Ensure(
                miss.Item is null,
                $"GetItem(ConsistentRead={Py.Repr(consistent)}) on a key that was never written returned an " +
                $"item: {Py.Repr(miss.Item)}");
            var gotCu = miss.ConsumedCapacity.CapacityUnits;
            Ensure(
                gotCu == wantCu,
                $"GetItem(ConsistentRead={Py.Repr(consistent)}) on an ABSENT key billed {Py.Repr(gotCu)}, expected " +
                $"{Py.Repr(wantCu)}. A read that finds nothing is still a read, and the eventual/strong " +
                "price ratio is the same 1:2 as for a hit -- an emulator that bills 0 for a miss has " +
                "no capacity model.");
        }

        QueryRequest BaseQ(bool consistent) => new()
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("small"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = consistent,
        };
        var strongQ = await ddb.QueryAsync(BaseQ(true));
        var eventualQ = await ddb.QueryAsync(BaseQ(false));
        Ensure(
            strongQ.Count == 1 && eventualQ.Count == 1,
            $"the base-table Query returned Count {Py.Repr(strongQ.Count)} strong / " +
            $"{Py.Repr(eventualQ.Count)} eventual, expected 1 and 1");
        var sq = strongQ.ConsumedCapacity.CapacityUnits;
        var eq = eventualQ.ConsumedCapacity.CapacityUnits;
        Ensure(
            sq == 1.0 && eq == 0.5,
            $"a base-table Query billed {Py.Repr(sq)} strong / {Py.Repr(eq)} eventual, expected 1.0 / 0.5. Query on " +
            "the table itself DOES support a strong read -- the 2:1 ratio is a property of the read " +
            "mode, not of GetItem.");

        // A GSI is projected asynchronously, so poll until the item is in it before asserting.
        await ddb.PutItemAsync(table, Item(("pk", S("indexed")), ("gk", S("g1"))));

        Task<QueryResponse> GsiQuery(bool? consistentRead = null) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gk = :g",
            ExpressionAttributeValues = Item((":g", S("g1"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = consistentRead,
        });

        var before = await PollUntil(
            () => GsiQuery(),
            r => r.Count == 1,
            timeoutSeconds: 30.0,
            description:
                "the GSI projecting the item written to the base table (AWS documents GSI updates as " +
                "'usually propagated within a fraction of a second'; 30s is ~100x that, so a timeout " +
                "here means broken rather than slow)");
        Ensure(
            before.ScannedCount >= 1,
            $"the GSI query scanned {Py.Repr(before.ScannedCount)} entries -- the index-empty assertions " +
            "below would be vacuous if the index were never actually read");

        var (code, message) = await Error(() => GsiQuery(consistentRead: true));
        Ensure(
            code == "ValidationException",
            $"Query(IndexName={Py.Repr(Gsi)}, ConsistentRead=True) was rejected with {Py.Repr(code)}, expected " +
            "'ValidationException'. There is no strongly-consistent read of a global secondary index, " +
            "so there is no capacity price for one either.");
        const string expectedMessage = "Consistent reads are not supported on global secondary indexes";
        Ensure(
            message == expectedMessage,
            $"the GSI refusal message was {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}: a caller has to be " +
            "able to tell 'this index cannot be read strongly' from 'this index does not exist'");

        var after = await GsiQuery();
        Ensure(
            after.Count == before.Count && before.Count == 1 && Py.Equal(after.Items, before.Items),
            "the REFUSED ConsistentRead query changed what the GSI returns: before " +
            $"{Py.Repr(before.Items)}, after {Py.Repr(after.Items)}. A rejected read must have no effect.");

        var controlCu = after.ConsumedCapacity.CapacityUnits;
        Ensure(
            controlCu == 0.5,
            $"the same GSI Query with ConsistentRead omitted billed {Py.Repr(controlCu)}, expected 0.5. A GSI " +
            "read is ALWAYS billed at the eventually-consistent rate; an emulator reporting 1.0 is " +
            "charging for a read path that does not exist.");

        // ConsistentRead is valid on the base table; the refusal above is about the index.
        var onTable = await ddb.QueryAsync(BaseQ(true));
        Ensure(
            onTable.Count == 1,
            "ConsistentRead=True on the base table of the same table returned Count " +
            $"{Py.Repr(onTable.Count)}, expected 1. Without this control the ValidationException above " +
            "could mean 'this target does not support ConsistentRead at all'.");

        // The 'small'/'large' items carry no gk attribute, so the sparse index must never surface them.
        var pks = after.Items.Select(it => it["pk"].S).ToList();
        Ensure(
            pks.SequenceEqual(["indexed"]),
            $"the sparse GSI returned {Py.Repr(pks)}; only the item " +
            "carrying a 'gk' attribute belongs in it. An emulator that answers index queries out of " +
            "the base table returns the 400 items written above too.");
    }

    /// <summary>The AWS (code, message) raised by fn(); raises if fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail(
                "the call was expected to be rejected by the service but it SUCCEEDED -- a target that " +
                "accepts this input is more broken than one that returns the wrong error code");
        }

        return (error.Code, error.Message);
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, string description, double intervalSeconds = 0.5)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Delete the table and confirm it is gone rather than merely requested.</summary>
    private static async Task DeleteTable(IAmazonDynamoDB ddb, string table)
    {
        await ddb.DeleteTableAsync(table);
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            try
            {
                await ddb.DescribeTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
