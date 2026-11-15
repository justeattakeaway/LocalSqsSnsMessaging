// Translated from behavioral/dynamodb/check_hot_partition_key_throttled_while_distributed_keys_at_same_rate_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.hot-partition-key-throttled-while-distributed-keys-at-same-rate-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class HotPartitionKeyThrottledWhileDistributedKeysAtSameRateRejected
{
    private const string CheckId = "hot-partition-key-throttled-while-distributed-keys-at-same-rate-rejected";

    // 4000 WCU forces DynamoDB to spread the table over >= ceil(4000/1000) == 4 partitions, each
    // carrying ~1000 WCU/s of the table's budget. 3000 RCU exists only so the server-side verification
    // reads are not themselves the subject; ceil(3000/3000) == 1, so it does not change the partition
    // count and therefore does not change what the write arms measure.
    private const int Wcu = 4000;
    private const int Rcu = 3000;

    // Offered write rate, identical for every arm. 2500/s is comfortably under the table's 4000 WCU/s
    // and comfortably over one partition's ~1000 WCU/s. That gap is the experiment: at this rate the
    // table ceiling can never be the binding constraint, so a throttle can only come from a
    // partition-level limiter.
    private const int Target = 2500;
    private const int ArmSeconds = 10;
    private const int DistKeys = 50;
    private const int QuiesceSeconds = 20;

    // On AWS a single hot key lands at the ~1000 WCU/s per-partition-key ceiling (adaptive capacity
    // does not absorb it) with reason 'TableWriteKeyRangeThroughputExceeded'. Writes are paced because
    // an unpaced driver exceeds the table ceiling and throttles both arms.
    private static readonly string Blob = new('x', 900); // -> 1 KB items, so 1 item == 1 WCU exactly and the arithmetic is checkable

    private sealed class ArmStats
    {
        public int Accepted;
        public int Unprocessed;
        public int Calls;
        public Dictionary<string, int> Codes { get; } = new(StringComparer.Ordinal);
        public HashSet<string?> Reasons { get; } = [];
        public HashSet<string?> Resources { get; } = [];
        public List<string> UnprocessedSks { get; } = [];
        public int ProbeAccepted;
        public double ElapsedSeconds;
        public int Offered;
        public int Landed;
        public double Rate;

        public string Repr() => Py.Repr(new Dictionary<string, object?>
        {
            ["accepted"] = Accepted,
            ["unprocessed"] = Unprocessed,
            ["calls"] = Calls,
            ["codes"] = Codes,
            ["reasons"] = Reasons.ToList(),
            ["resources"] = Resources.ToList(),
            ["unprocessed_sks"] = UnprocessedSks.Take(5).ToList(),
            ["probe_accepted"] = ProbeAccepted,
            ["elapsed_s"] = ElapsedSeconds,
            ["offered"] = Offered,
            ["landed"] = Landed,
            ["rate"] = Rate,
        });
    }

    private static string F0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Drive <paramref name="target"/> writes/s of 1 KB items for <paramref name="durationSeconds"/> and report what the service did.
    ///
    /// BatchWriteItem carries the load (throttles surface as UnprocessedItems); single PutItem probes
    /// run alongside because only the exception carries ThrottlingReasons.
    /// </summary>
    private static async Task<ArmStats> PacedArm(
        FidelityContext ctx, IAmazonDynamoDB ddb, string table, string label, Func<int, string> keyfn, int target, int durationSeconds, int nProbe = 6)
    {
        var nBatches = durationSeconds * target / 25;
        var gate = new object();
        var st = new ArmStats();
        var nxt = 0;
        var t0 = FidelityTime.UtcNow;

        void RecordError(AwsError error)
        {
            lock (gate)
            {
                var code = error.Code ?? "";
                st.Codes[code] = st.Codes.GetValueOrDefault(code) + 1;
                foreach (var reason in (error.Exception as ProvisionedThroughputExceededException)?.ThrottlingReasons ?? [])
                {
                    st.Reasons.Add(reason.Reason);
                    st.Resources.Add(reason.Resource);
                }
            }
        }

        async Task BatchWriter()
        {
            while (true)
            {
                int b;
                lock (gate)
                {
                    b = nxt;
                    if (b >= nBatches)
                    {
                        return;
                    }

                    nxt++;
                }

                var slack = t0 + TimeSpan.FromSeconds(b * 25.0 / target) - FidelityTime.UtcNow;
                if (slack > TimeSpan.Zero)
                {
                    await FidelityTime.Delay(slack);
                }

                var batchBase = b * 25;
                var items = Enumerable.Range(0, 25).Select(j => new WriteRequest
                {
                    PutRequest = new PutRequest
                    {
                        Item = Item(
                            ("pk", S(keyfn(batchBase + j))),
                            ("sk", S($"{label}-{(batchBase + j).ToString("0000000", CultureInfo.InvariantCulture)}")),
                            ("b", S(Blob))),
                    },
                }).ToList();
                var (resp, error) = await FidelityContext.Capture(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = items },
                }));
                if (error is not null)
                {
                    RecordError(error);
                    continue;
                }

                var left = resp!.UnprocessedItems?.GetValueOrDefault(table) ?? [];
                lock (gate)
                {
                    st.Calls++;
                    st.Unprocessed += left.Count;
                    st.Accepted += 25 - left.Count;
                    st.UnprocessedSks.AddRange(left.Select(w => w.PutRequest.Item["sk"].S));
                }
            }
        }

        async Task Probe(int wid)
        {
            var n = 0;
            var deadline = t0 + TimeSpan.FromSeconds(durationSeconds);
            while (FidelityTime.UtcNow < deadline)
            {
                n++;
                var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, Item(
                    ("pk", S(keyfn((n * 7919) + (wid * 13)))),
                    ("sk", S($"{label}-p{wid}-{n.ToString("000000", CultureInfo.InvariantCulture)}")),
                    ("b", S(Blob)))));
                if (error is null)
                {
                    lock (gate)
                    {
                        st.ProbeAccepted++;
                    }
                }
                else
                {
                    RecordError(error);
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(0.15));
            }
        }

        using (ctx.Concurrently())
        {
            var futures = Enumerable.Range(0, 100).Select(_ => Task.Run(BatchWriter))
                .Concat(Enumerable.Range(0, nProbe).Select(w => Task.Run(() => Probe(w))))
                .ToList();
            await Task.WhenAll(futures);
        }

        st.ElapsedSeconds = (FidelityTime.UtcNow - t0).TotalSeconds;
        st.Offered = nBatches * 25;
        st.Landed = st.Accepted + st.ProbeAccepted;
        st.Rate = st.Landed / st.ElapsedSeconds;
        return st;
    }

    /// <summary>Strongly-consistent server-side count of the items under one partition key, paginated.</summary>
    private static async Task<int> CountPk(IAmazonDynamoDB ddb, string table, string pk)
    {
        var total = 0;
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S(pk))),
                Select = Select.COUNT,
                ConsistentRead = true,
                ExclusiveStartKey = start is { Count: > 0 } ? start : null,
            });
            total += page.Count ?? 0;
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// At one offered write rate, writes to a single partition key are throttled with the
    /// TableWriteKeyRangeThroughputExceeded reason, while the same rate over 50 keys is not throttled;
    /// 4 keys land in between, and throttled writes never persist.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbThroughput, "Provisioned throughput is never enforced, so the single hot key is never throttled (the Python run also ran out of the 60 s budget in its 20 s quiesce waits)")]
    [DisplayName("dynamodb.hot-partition-key-throttled-while-distributed-keys-at-same-rate-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // The load client has retries off so a retry cannot absorb a throttle; the read-back client
        // retries because a throttled verification read is not a finding.
        var ddb = ctx.DynamoClient(maxAttempts: 1);
        // botocore's retries={'max_attempts': 12} is 12 retries: total_max_attempts == 13.
        var rd = ctx.DynamoClient(maxAttempts: 13);
        // botocore spells "N retries" as total_max_attempts == N+1 and applies it independently of the
        // retry mode, so assert the NORMALIZED value rather than trusting the Config that was passed.
        Ensure(
            ddb.Config.MaxErrorRetry + 1 == 1,
            $"load client resolved retries to {{'total_max_attempts': {ddb.Config.MaxErrorRetry + 1}}}, expected total_max_attempts == 1 (no retries at all)");
        Ensure(
            rd.Config.MaxErrorRetry + 1 == 13,
            $"verification client resolved retries to {{'total_max_attempts': {rd.Config.MaxErrorRetry + 1}}}, expected total_max_attempts == 13");

        var table = FidelityContext.UniqueName("sim2real-behav-hotpart-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = Rcu, WriteCapacityUnits = Wcu },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });

        ctx.OnCleanup(async () =>
        {
            try
            {
                await rd.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await rd.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var arn = (await rd.DescribeTableAsync(table)).Table.TableArn;

        // Warm the table across the whole distributed key space at a rate no partition can throttle,
        // so both measured arms meet an already-split, already-baselined table.
        var warm = await PacedArm(ctx, ddb, table, "warm", i => $"d{i % DistKeys}", target: 500, durationSeconds: 4, nProbe: 0);
        Ensure(
            warm.Unprocessed == 0 && warm.Codes.Count == 0,
            $"the 500/s warm-up was itself throttled: {warm.Unprocessed} unprocessed, codes {Py.Repr(warm.Codes)} -- the table cannot carry even 1/8 of its provisioned write capacity");

        // Arm B (distributed) runs FIRST, so a failure here can never be explained as damage left
        // behind by the hot arm.
        await FidelityTime.Delay(TimeSpan.FromSeconds(QuiesceSeconds));
        var dist = await PacedArm(ctx, ddb, table, "DIST", i => $"d{i % DistKeys}", target: Target, durationSeconds: ArmSeconds);
        await FidelityTime.Delay(TimeSpan.FromSeconds(QuiesceSeconds));
        var hot = await PacedArm(ctx, ddb, table, "HOT", _ => "hot", target: Target, durationSeconds: ArmSeconds);
        await FidelityTime.Delay(TimeSpan.FromSeconds(QuiesceSeconds));
        // Continuity arm: four partition keys. If the limiter tracks key cardinality continuously
        // rather than special-casing a literal single-key writer, throttling must land strictly
        // between the two measured arms.
        var four = await PacedArm(ctx, ddb, table, "FOUR", i => $"h{i % 4}", target: Target, durationSeconds: ArmSeconds);

        // The comparison is only valid if the arms offered the same load and the driver was not
        // itself the bottleneck.
        Ensure(
            dist.Offered == hot.Offered && hot.Offered == four.Offered,
            $"arms offered different loads: dist={dist.Offered} hot={hot.Offered} four={four.Offered} -- the comparison would be void");
        // 0.6 x 2500/s is still 1.5x one partition key's ~1000 WCU/s, so the hot arm is over its ceiling.
        Ensure(
            dist.Rate >= 0.6 * Target,
            $"the distributed arm achieved only {F0(dist.Rate)} writes/s against an offered {Target}/s while spending no throttling budget -- the driver, not the service, was the bottleneck, so the two arms are not at the same rate ({dist.Repr()})");

        Ensure(
            dist.Unprocessed == 0,
            $"{dist.Unprocessed} of {dist.Offered} distributed writes came back unprocessed at {F0(dist.Rate)}/s against {Wcu} provisioned WCU -- a well-spread writer inside the table budget must not be throttled");
        Ensure(dist.Codes.Count == 0, $"the distributed arm raised {Py.Repr(dist.Codes)}; expected no errors at all");
        Ensure(
            dist.Reasons.Count == 0,
            $"the distributed arm reported throttling reasons {Py.Repr(dist.Reasons.Order(StringComparer.Ordinal).ToList())}; a writer spread over 50 keys inside the table budget has no reason to be throttled");

        // The hot arm is throttled, and named as a key-range throttle.
        Ensure(
            hot.Unprocessed > 0,
            $"all {hot.Offered} writes to the single partition key \"hot\" were accepted at {F0(hot.Rate)}/s -- there is no partition-level limiter ({hot.Repr()})");
        Ensure(
            hot.Codes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["ProvisionedThroughputExceededException"]),
            $"the hot arm's single-item probes raised {Py.Repr(hot.Codes)}; expected exactly ProvisionedThroughputExceededException and nothing else");
        Ensure(
            hot.Reasons.SetEquals(["TableWriteKeyRangeThroughputExceeded"]),
            $"the hot arm reported throttling reasons {Py.Repr(hot.Reasons.Order(StringComparer.Ordinal).ToList())}, expected exactly {{'TableWriteKeyRangeThroughputExceeded'}} -- a table-level reason here means the limiter is not partition-aware");
        Ensure(
            hot.Resources.SetEquals([arn]),
            $"ThrottlingReasons named resources {Py.Repr(hot.Resources.Order(StringComparer.Ordinal).ToList())}, expected exactly the table ARN {Py.Repr(arn)}");
        Ensure(
            hot.Landed <= 0.70 * dist.Landed,
            $"the hot arm landed {hot.Landed} writes against the distributed arm's {dist.Landed} of the same offered {Target}/s -- a single partition key must be capped far below the table budget");

        // Negative space: exactly the accepted writes landed, and nothing reported unprocessed did.
        var hotCount = await CountPk(rd, table, "hot");
        Ensure(
            hotCount == hot.Landed,
            $"a consistent COUNT under pk=\"hot\" found {hotCount} items but the service accepted {hot.Landed} ({hot.Accepted} batched + {hot.ProbeAccepted} probed) -- a throttled write must leave nothing behind, and an accepted one must not be lost");
        var absentProbe = hot.UnprocessedSks.Order(StringComparer.Ordinal).Take(500).ToList();
        Ensure(
            absentProbe.Count == 500,
            $"only {absentProbe.Count} unprocessed sort keys were captured, needed 500 to probe for absence ({hot.Unprocessed} unprocessed reported)");
        var foundRejected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < absentProbe.Count; i += 100)
        {
            Dictionary<string, KeysAndAttributes>? request = new(StringComparer.Ordinal)
            {
                [table] = new KeysAndAttributes
                {
                    Keys = [.. absentProbe.Skip(i).Take(100).Select(s => Item(("pk", S("hot")), ("sk", S(s))))],
                    ConsistentRead = true,
                    ProjectionExpression = "sk",
                },
            };
            while (request is { Count: > 0 })
            {
                var page = await rd.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = request });
                foundRejected.UnionWith(page.Responses[table].Select(it => it["sk"].S));
                request = page.UnprocessedKeys;
            }
        }

        Ensure(
            foundRejected.Count == 0,
            $"{foundRejected.Count} items reported as UnprocessedItems are present on a consistent read, e.g. {Py.Repr(foundRejected.Order(StringComparer.Ordinal).Take(3).ToList())} -- a throttled write partially landed");

        var keys = Enumerable.Range(0, DistKeys).Select(i => $"d{i}").ToList();
        int[] counts;
        using (ctx.Concurrently())
        {
            counts = await Task.WhenAll(keys.Select(k => Task.Run(() => CountPk(rd, table, k), cancellationToken)));
        }

        var perKey = keys.Zip(counts).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
        var batchShare = dist.Offered / DistKeys;
        var thin = perKey.Where(p => p.Value < batchShare).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Ensure(
            thin.Count == 0,
            $"{thin.Count} of {DistKeys} distributed keys hold fewer than the {batchShare} items the arm wrote to each, e.g. {Py.Repr(thin.OrderBy(p => p.Key, StringComparer.Ordinal).Take(3).Select(p => (p.Key, p.Value)).ToList())} -- the load was not actually distributed the way the arm intended");
        Ensure(
            perKey.Values.Sum() == warm.Landed + dist.Landed,
            $"the {DistKeys} distributed keys hold {perKey.Values.Sum()} items but the warm-up and the distributed arm together accepted {warm.Landed + dist.Landed} -- the table lost or duplicated writes");

        // Continuity arm: four keys throttle less than one key, at the same offered rate.
        Ensure(
            four.Unprocessed < hot.Unprocessed,
            $"spreading the same {four.Offered} writes over 4 partition keys left {four.Unprocessed} unprocessed against {hot.Unprocessed} for a single key -- the limiter is not tracking key cardinality continuously");
        Ensure(
            four.Landed > hot.Landed,
            $"4 keys landed {four.Landed} writes and 1 key landed {hot.Landed} of the same offered load -- more distinct partition keys must buy more throughput");
    }
}
