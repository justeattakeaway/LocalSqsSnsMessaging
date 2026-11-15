// Translated from behavioral/dynamodb/check_restore_to_earliest_restorable_time_excludes_later_writes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-to-earliest-restorable-time-excludes-later-writes</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreToEarliestRestorableTimeExcludesLaterWrites
{
    private const int SkewS = 120;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s, last value: {Py.Repr(last)}");
    }

    /// <summary>
    /// A just-created table has continuous backups enabling in the background; retry the
    /// enable request until that settles.
    /// </summary>
    private static async Task<UpdateContinuousBackupsResponse> EnablePitrWithRetry(IAmazonDynamoDB ddb, string tableName, double timeoutS = 60, double intervalS = 2)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = tableName,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            }));
            if (error is null)
            {
                return resp!;
            }

            if (error.Code == "ContinuousBackupsUnavailableException" && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            throw error.Exception;
        }
    }

    /// <summary>
    /// The server's clock at a response, from its HTTP Date header (one-second resolution). The
    /// in-memory handler sends no Date header, so this reads the bus's clock (the server's) as the
    /// response arrives, truncated to the second as the header is.
    /// </summary>
    private static DateTime ServerTime()
    {
        var now = FidelityTime.UtcNow.UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>A table still CREATING from a restore refuses DeleteTable; retry until it can go.</summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutS = 900.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static double Remaining(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// Enabling PITR reports EarliestRestorableDateTime at the moment it was enabled and a
    /// LatestRestorableDateTime no earlier; a restore to that earliest time carries over the item
    /// written before it and excludes the item written after it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_PointInTimeRecoveryDescription.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-to-earliest-restorable-time-excludes-later-writes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var source = FidelityContext.UniqueName("sim2real-behav-rpitr-early-src-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, source));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = source,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(source)).Table.TableStatus.Value,
            s => s == "ACTIVE",
            timeoutS: 60,
            intervalS: 2,
            description: $"{source} ACTIVE");
        await ddb.PutItemAsync(source, Item(("pk", S("item-a")), ("val", S("before-restore-time"))));
        var wroteAAt = ServerTime();

        // PITR is enabled a few seconds after the first write on the server's clock, so the earliest
        // restorable time lies clearly after it and a restore to that time must carry it over.
        await PollUntil(
            async () =>
            {
                await ddb.DescribeTableAsync(source);
                return ServerTime();
            },
            now => now >= wroteAAt.AddSeconds(5),
            timeoutS: SkewS + 30,
            intervalS: 1,
            description: "the server clock passing the first write by five seconds");
        await EnablePitrWithRetry(ddb, source);
        var enabledAt = ServerTime();
        var pitr = await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = source }))
                .ContinuousBackupsDescription.PointInTimeRecoveryDescription,
            p => p.PointInTimeRecoveryStatus == PointInTimeRecoveryStatus.ENABLED && p.EarliestRestorableDateTime is not null && p.LatestRestorableDateTime is not null,
            timeoutS: 90,
            intervalS: 2,
            description: "PITR ENABLED with both restorable date-times");
        var earliest = pitr.EarliestRestorableDateTime!.Value;
        var latest = pitr.LatestRestorableDateTime!.Value;
        var (low, high) = (enabledAt.AddSeconds(-SkewS), enabledAt.AddSeconds(SkewS));
        Ensure(
            low <= earliest.ToUniversalTime() && earliest.ToUniversalTime() <= high,
            $"EarliestRestorableDateTime {Iso(earliest)} is not within {SkewS} s of the UpdateContinuousBackups response time {Iso(enabledAt)}");
        Ensure(
            earliest <= latest,
            $"LatestRestorableDateTime {Iso(latest)} precedes EarliestRestorableDateTime {Iso(earliest)}");

        // The second write lands a few seconds after the earliest restorable time on the server's
        // clock, so a restore to that time must exclude it.
        var floor = (earliest.ToUniversalTime() > wroteAAt ? earliest.ToUniversalTime() : wroteAAt).AddSeconds(5);
        await PollUntil(
            async () =>
            {
                await ddb.DescribeTableAsync(source);
                return ServerTime();
            },
            now => now >= floor,
            timeoutS: SkewS + 30,
            intervalS: 1,
            description: "the server clock passing the earliest restorable time by five seconds");
        await ddb.PutItemAsync(source, Item(("pk", S("item-b")), ("val", S("after-restore-time"))));
        var target = FidelityContext.UniqueName("sim2real-behav-rpitr-early-dst-");

        async Task<RestoreTableToPointInTimeResponse?> AttemptRestore()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = source,
                TargetTableName = target,
                RestoreDateTime = earliest,
            }));
            if (error is not null)
            {
                if (error.Code is "InvalidRestoreTimeException" or "PointInTimeRecoveryUnavailableException")
                {
                    return null;
                }

                throw error.Exception;
            }

            ctx.OnCleanup(() => SafeDeleteTable(ddb, target));
            return resp;
        }

        var restoreResp = (await PollUntil(
            AttemptRestore,
            r => r is not null,
            timeoutS: 90,
            intervalS: 3,
            description: "RestoreTableToPointInTime to EarliestRestorableDateTime accepted"))!;
        var described = restoreResp.TableDescription;
        Ensure(described.TableName == target, $"RestoreTableToPointInTime described table {Py.Repr(described.TableName)}, expected {Py.Repr(target)}");
        var summary = described.RestoreSummary;
        Ensure(
            summary.RestoreDateTime?.ToUniversalTime() == earliest.ToUniversalTime(),
            $"RestoreSummary.RestoreDateTime {(summary.RestoreDateTime is { } r ? Iso(r) : "None")} is not the requested EarliestRestorableDateTime {Iso(earliest)}");
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(target)).Table,
            t => t.TableStatus == TableStatus.ACTIVE,
            timeoutS: Remaining(ctx, 1500.0),
            intervalS: 5,
            description: $"{target} ACTIVE");
        var itemA = (await ddb.GetItemAsync(new GetItemRequest { TableName = target, Key = Item(("pk", S("item-a"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(itemA, Item(("pk", S("item-a")), ("val", S("before-restore-time")))),
            $"item written before the restore time restored as {Py.Repr(itemA)}");
        var itemB = (await ddb.GetItemAsync(new GetItemRequest { TableName = target, Key = Item(("pk", S("item-b"))), ConsistentRead = true })).Item;
        Ensure(itemB is null, $"item written after the restore time must be excluded from the restored table, got {Py.Repr(itemB)}");
    }
}
