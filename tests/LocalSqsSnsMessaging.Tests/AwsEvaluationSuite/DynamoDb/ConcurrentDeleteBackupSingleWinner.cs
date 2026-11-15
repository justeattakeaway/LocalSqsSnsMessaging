// Translated from behavioral/dynamodb/check_concurrent_delete_backup_single_winner.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-delete-backup-single-winner</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentDeleteBackupSingleWinner
{
    private const string Check = "concurrent-delete-backup-single-winner";
    private const string ErrBackupNotFound = "BackupNotFoundException";

    /// <summary>
    /// N concurrent DeleteBackup calls on the SAME AVAILABLE backup yield exactly one success;
    /// every other racer gets BackupNotFoundException, and a final DescribeBackup confirms it is gone.
    /// Tagged axis:rejection: the graded half is the losers' error, not the one success.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-delete-backup-single-winner")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        // table_exists waiter bound: 2 s x 90 attempts.
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 90);

        // Immediately after CreateTable, the continuous-backups infra may still be provisioning
        // (CreateBackup then raises ContinuousBackupsUnavailableException) -- wait it out.
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = t })).ContinuousBackupsDescription,
            d => d.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED,
            timeoutS: Math.Max(1.0, Math.Min(90.0, TimeLeftOr(ctx, 90.0) - 5.0)),
            intervalS: 2.0,
            description: "continuous backups ready");

        var backupArn = (await ddb.CreateBackupAsync(new CreateBackupRequest { TableName = t, BackupName = FidelityContext.UniqueName("bkp-") })).BackupDetails.BackupArn;
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: Math.Max(1.0, Math.Min(120.0, TimeLeftOr(ctx, 120.0) - 5.0)),
            intervalS: 2.0,
            description: "backup AVAILABLE");

        const int n = 6;
        object[] results;
        using (ctx.Concurrently())
        {
            results = await RunConcurrent([.. Enumerable.Range(0, n).Select(_ => (Func<Task<DeleteBackupResponse>>)(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn })))]);
        }

        var successes = ConcurrentSuccesses(results);
        Ensure(successes == 1, $"expected exactly one winning DeleteBackup, got {successes}: {Py.Repr(results.Select(r => r is Exception ex ? ex.Message : "ok"))}");

        var codes = ConcurrentErrorCodes(results);
        Ensure(codes.Count == n - 1, $"expected {n - 1} losing racers, got {codes.Count}: {Py.Repr(codes)}");
        Ensure(codes.All(c => c == ErrBackupNotFound), $"expected every losing racer to get {ErrBackupNotFound}, got {Py.Repr(codes)}");

        var final = await FidelityContext.CaptureError(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }))
            ?? throw Fail("expected this call to be rejected, but it SUCCEEDED");
        Ensure(final.Code == ErrBackupNotFound, $"expected {ErrBackupNotFound}, got {final.Code}");
        Ensure(
            final.Message == $"Backup not found: {backupArn}",
            $"expected AWS's exact BackupNotFoundException message, got {Py.Repr(final.Message)}");
    }

    /// <summary><c>ctx.time_left() or default</c>.</summary>
    private static double TimeLeftOr(FidelityContext ctx, double fallback) => ctx.TimeLeftSeconds is var left && left != 0 ? left : fallback;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 90, double intervalS = 2.0, string description = "condition")
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

        throw Fail($"{description} not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Runs every fn concurrently; each result is its response or the exception it raised.</summary>
    private static async Task<object[]> RunConcurrent<T>(IReadOnlyList<Func<Task<T>>> fns)
        where T : class =>
        await Task.WhenAll(fns.Select(fn => Task.Run<object>(async () =>
        {
            try
            {
                return await fn();
            }
#pragma warning disable CA1031 // as the Python check, every failure is recorded as a result
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        })));

    private static int ConcurrentSuccesses(object[] results) => results.Count(r => r is not Exception);

    private static List<string?> ConcurrentErrorCodes(object[] results) =>
    [
        .. results.OfType<Exception>().Select(r => r is Amazon.Runtime.AmazonServiceException ex ? AwsError.From(ex).Code : r.GetType().Name),
    ];
}
