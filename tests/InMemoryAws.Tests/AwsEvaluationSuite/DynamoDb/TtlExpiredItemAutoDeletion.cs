// Translated from behavioral/dynamodb/check_ttl_expired_item_auto_deletion.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ttl-expired-item-auto-deletion</summary>
[Category("AwsEvaluationSuite")]
public sealed class TtlExpiredItemAutoDeletion
{
    /// <summary>The fixed best-effort window in which the TTL background sweep is watched.</summary>
    private const double SweepWindowS = 20.0;

    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>
    /// A poll's timeout: capS, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    private static async Task<T> PollUntil<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        Func<T?, string> repr,
        double timeoutS = 60,
        double intervalS = 1.0,
        string description = "condition",
        FidelityContext? ctx = null)
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

        if (ctx is not null && BudgetSpent(ctx))
        {
            throw new TimeoutException($"{description}: the run budget ended first; not graded");
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {repr(last)}");
    }

    /// <summary>Call fn() until it succeeds, retrying ClientErrors whose code is in retryCodes.</summary>
    private static async Task<T> RetryOnCode<T>(Func<Task<T>> fn, string[] retryCodes, double timeoutS, double intervalS, string description)
        where T : class
    {
        async Task<(string Status, T? Value)> Attempt()
        {
            var (value, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                return ("ok", value);
            }

            if (!retryCodes.Contains(error.Code))
            {
                throw error.Exception;
            }

            return ("retry", null);
        }

        var outcome = await PollUntil(
            Attempt,
            r => r.Status == "ok",
            r => $"('{r.Status}', None)",
            timeoutS: timeoutS,
            intervalS: intervalS,
            description: description);
        return outcome.Value!;
    }

    private static readonly string[] ThrottlingCodes = ["ThrottlingException"];

    /// <summary>DescribeTimeToLive, retrying ThrottlingException (the API has a low account-level rate).</summary>
    private static Task<TimeToLiveDescription> DescribeTtl(IAmazonDynamoDB ddb, string tableName) =>
        RetryOnCode(
            async () => (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = tableName })).TimeToLiveDescription,
            retryCodes: ThrottlingCodes,
            timeoutS: 30,
            intervalS: 3.0,
            description: $"describe_time_to_live({tableName}) past transient throttling");

    private static string TtlRepr(TimeToLiveDescription? d)
    {
        if (d is null)
        {
            return "None";
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (d.TimeToLiveStatus is not null)
        {
            dict["TimeToLiveStatus"] = d.TimeToLiveStatus;
        }

        if (d.AttributeName is not null)
        {
            dict["AttributeName"] = d.AttributeName;
        }

        return Py.Repr(dict);
    }

    /// <summary>
    /// With TTL enabled, an already-expired item stays readable via GetItem and Scan until the
    /// background sweep runs; a time FilterExpression hides it, and a sweep never deletes a live item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/howitworks-ttl.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ttl-expired-item-auto-deletion")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);

        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = name,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiresAt" },
        });
        await PollUntil(
            () => DescribeTtl(ddb, name),
            d => d.TimeToLiveStatus == TimeToLiveStatus.ENABLED || d.TimeToLiveStatus == TimeToLiveStatus.ENABLING,
            TtlRepr,
            timeoutS: BudgetS(ctx, 90.0),
            intervalS: 2.0,
            description: "TTL enabled on table",
            ctx: ctx);

        var now = FidelityTime.UtcNow.ToUnixTimeSeconds();
        var expiredKey = Item(("pk", S("ttl-expired-item")));
        var liveKey = Item(("pk", S("ttl-live-item")));
        await ddb.PutItemAsync(name, Item(
            ("pk", S("ttl-expired-item")),
            ("expiresAt", N((now - 3600).ToString(CultureInfo.InvariantCulture))),
            ("payload", S("x"))));
        await ddb.PutItemAsync(name, Item(
            ("pk", S("ttl-live-item")),
            ("expiresAt", N((now + (3600 * 24 * 30)).ToString(CultureInfo.InvariantCulture))),
            ("payload", S("x"))));

        // TTL deletion is an asynchronous sweep, not a read-time filter.
        var stillThere = await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = expiredKey, ConsistentRead = true });
        Ensure(stillThere.Item is not null, "an expired item must remain readable until the TTL sweep runs");
        Ensure(Py.Equal(stillThere.Item["payload"], S("x")), Py.Repr(stillThere.Item));

        var plainScan = await ddb.ScanAsync(new ScanRequest { TableName = name, ConsistentRead = true });
        var scannedPks = plainScan.Items.Select(it => it["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            scannedPks.Contains("ttl-expired-item") && scannedPks.Contains("ttl-live-item"),
            "a plain Scan must still surface an expired-but-undeleted item");

        // AWS's own guidance for hiding not-yet-deleted expired items from reads is
        // a caller-side FilterExpression against the current time.
        var filteredScan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = "expiresAt > :now",
            ExpressionAttributeValues = Item((":now", N(now.ToString(CultureInfo.InvariantCulture)))),
            ConsistentRead = true,
        });
        var filteredPks = filteredScan.Items.Select(it => it["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(!filteredPks.Contains("ttl-expired-item"), Py.Repr(filteredPks));
        Ensure(filteredPks.Contains("ttl-live-item"), Py.Repr(filteredPks));

        // Best-effort: AWS deletes expired items "within a few days", so the sweep is not asserted.
        // The predicate folds in the deadline so PollUntil returns instead of raising.
        var window = FidelityTime.StartNew();

        async Task<(bool Swept, bool Expired)> SweepProbe()
        {
            var expiredNow = await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = expiredKey, ConsistentRead = true });
            return (expiredNow.Item is null, window.Elapsed.TotalSeconds >= SweepWindowS);
        }

        var (swept, _) = await PollUntil(
            SweepProbe,
            r => r.Swept || r.Expired,
            r => Py.Repr(r),
            timeoutS: 150,
            intervalS: 2.0,
            description: "best-effort observation window for the TTL background sweep");
        if (swept)
        {
            var liveNow = await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = liveKey, ConsistentRead = true });
            Ensure(liveNow.Item is not null, "TTL sweep must not delete an item whose TTL has not yet passed");
        }
    }
}
