// Translated from behavioral/dynamodb/check_concurrent_create_backup_duplicate_name_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-create-backup-duplicate-name-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentCreateBackupDuplicateNameAccepted
{
    private const string Check = "concurrent-create-backup-duplicate-name-accepted";

    /// <summary>
    /// Eight concurrent CreateBackup calls on one table with the same BackupName all succeed:
    /// DynamoDB does not enforce BackupName uniqueness, and each racer gets a distinct BackupArn
    /// whose DescribeBackup reports the shared name.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-create-backup-duplicate-name-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-cbc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);
        // A freshly-created table's continuous backups (PITR plumbing) take a short while to finish
        // enabling; CreateBackup fails with ContinuousBackupsUnavailableException until that settles,
        // which would otherwise be mistaken for the BackupInUseException race this check targets.
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription.ContinuousBackupsStatus.Value,
            status => status == "ENABLED",
            timeoutS: 120,
            intervalS: 2);

        const int concurrency = 8;
        var sharedBackupName = FidelityContext.UniqueName("sim2real-behav-cbc-bk-");
        var barrier = new AsyncBarrier(concurrency);
        var results = new (bool Ok, string? Arn, string? Code)?[concurrency];

        async Task Create(int i)
        {
            await barrier.SignalAndWait(cancellationToken);
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = sharedBackupName }));
            results[i] = error is null ? (true, resp!.BackupDetails.BackupArn, null) : (false, null, error.Code);
        }

        using (ctx.Concurrently())
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(i => Task.Run(() => Create(i), cancellationToken)));
        }

        foreach (var r in results)
        {
            if (r is { Ok: true } ok)
            {
                var arn = ok.Arn!;
                ctx.OnCleanup(() => SafeDeleteBackup(ddb, arn));
            }
        }

        var failures = results.Where(r => r is { Ok: false }).Select(r => r!.Value).ToList();
        var successes = results.Where(r => r is { Ok: true }).Select(r => r!.Value).ToList();

        Ensure(
            failures.Count == 0,
            $"expected all {concurrency} concurrent same-named CreateBackup calls on the same table " +
            "to succeed independently (real DynamoDB does not enforce BackupName uniqueness or " +
            $"serialize concurrent backup creates at this concurrency), but {failures.Count} were " +
            $"rejected: {Py.Repr(failures.Select(f => f.Code))}");
        Ensure(successes.Count == concurrency, $"expected all {concurrency} racers to succeed independently, got {successes.Count}");

        foreach (var s in successes)
        {
            var described = await PollUntil(
                async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = s.Arn })).BackupDescription,
                desc => desc.BackupDetails.BackupStatus.Value is "AVAILABLE" or "CREATING",
                timeoutS: 60,
                intervalS: 3);
            Ensure(
                described.BackupDetails.BackupName == sharedBackupName,
                $"expected the independently-read backup name to be {Py.Repr(sharedBackupName)}, " +
                $"got {Py.Repr(described.BackupDetails.BackupName)}");
        }

        var distinctArns = successes.Select(s => s.Arn).Distinct(StringComparer.Ordinal).Count();
        Ensure(
            distinctArns == successes.Count,
            $"expected {successes.Count} distinct backup ARNs, got {distinctArns} -- a " +
            "duplicate ARN would mean two racers were silently collapsed into one backup");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task SafeDeleteBackup(IAmazonDynamoDB ddb, string backupArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        if (error is not null && error.Code is not ("BackupNotFoundException" or "BackupInUseException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
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

        throw Fail($"condition not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s, last value: {Py.Repr(last)}");
    }

    /// <summary><c>threading.Barrier</c>: every participant waits until all have arrived.</summary>
    private sealed class AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participants;

        public Task SignalAndWait(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _all.TrySetResult();
            }

            return _all.Task.WaitAsync(cancellationToken);
        }
    }
}
