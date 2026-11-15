// Translated from behavioral/dynamodb/check_list_backups_reflects_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-backups-reflects-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListBackupsReflectsLifecycle
{
    /// <summary>
    /// ListBackups includes a backup only between its CreateBackup and DeleteBackup calls;
    /// after DeleteBackup, an independent DescribeBackup and a second DeleteBackup both 404
    /// (BackupNotFoundException).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-backups-reflects-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-lbk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "list-backups-reflects-lifecycle" }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 120,
            intervalSeconds: 3);

        var backupName = FidelityContext.UniqueName("sim2real-behav-lbkname-");
        var namesBefore = await ListBackupNames(ddb, tableName);
        Ensure(
            !namesBefore.Contains(backupName),
            $"unique backup name {Py.Repr(backupName)} already present before creation: {Py.Repr(namesBefore)}");

        var backupArn = (await CreateBackupWithRetry(ddb, tableName, backupName)).BackupDetails.BackupArn;
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            status => status == "AVAILABLE",
            timeoutSeconds: 180,
            intervalSeconds: 3);

        var namesPresent = await PollUntil(
            () => ListBackupNames(ddb, tableName),
            names => names.Contains(backupName),
            timeoutSeconds: 60,
            intervalSeconds: 3);
        Ensure(
            namesPresent.Contains(backupName),
            $"expected {Py.Repr(backupName)} in ListBackups while the backup is AVAILABLE, got {Py.Repr(namesPresent)}");

        await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn });

        var namesAbsent = await PollUntil(
            () => ListBackupNames(ddb, tableName),
            names => !names.Contains(backupName),
            timeoutSeconds: 120,
            intervalSeconds: 3);
        Ensure(
            !namesAbsent.Contains(backupName),
            $"expected {Py.Repr(backupName)} absent from ListBackups after DeleteBackup, got {Py.Repr(namesAbsent)}");

        var expectedMessage = $"Backup not found: {backupArn}";
        var clock = FidelityTime.StartNew();
        AwsError finalErr;
        while (true)
        {
            var (described, error) = await FidelityContext.Capture(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }));
            if (error is not null)
            {
                finalErr = error;
                break;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(60))
            {
                throw Fail($"DescribeBackup still answers for the deleted backup after 60s: {Py.Repr(described)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(
            finalErr.Code == "BackupNotFoundException",
            $"expected DescribeBackup on the deleted backup to 404 with BackupNotFoundException, got {Py.Repr(finalErr.Code)}");
        Ensure(finalErr.Message == expectedMessage, $"expected DescribeBackup message {Py.Repr(expectedMessage)}, got {Py.Repr(finalErr.Message)}");

        var redeleteErr = await Error(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        Ensure(
            redeleteErr.Code == "BackupNotFoundException",
            $"expected a second DeleteBackup on the already-deleted backup to 404 with BackupNotFoundException, got {Py.Repr(redeleteErr.Code)}");
        Ensure(redeleteErr.Message == expectedMessage, $"expected DeleteBackup message {Py.Repr(expectedMessage)}, got {Py.Repr(redeleteErr.Message)}");
    }

    private static async Task<List<string>> ListBackupNames(IAmazonDynamoDB ddb, string tableName)
    {
        var resp = await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = tableName });
        return [.. (resp.BackupSummaries ?? []).Select(s => s.BackupName)];
    }

    private static async Task<CreateBackupResponse> CreateBackupWithRetry(IAmazonDynamoDB ddb, string tableName, string backupName, double timeoutSeconds = 180, double intervalSeconds = 5)
    {
        // Immediately after a table becomes ACTIVE, its continuous-backups infrastructure can
        // still be settling, which makes CreateBackup transiently reject with
        // ContinuousBackupsUnavailableException. Retry within a bound instead of treating this
        // as a hard failure.
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                return await ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = backupName });
            }
            catch (ContinuousBackupsUnavailableException ex)
            {
                lastErr = AwsError.From(ex);
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            }
        }

        throw Fail($"CreateBackup still unavailable after {timeoutSeconds}s: {lastErr}");
    }

    /// <summary>The Error dict of a call that MUST be refused; RAISES if the call succeeds.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    private static async Task SafeDeleteBackup(IAmazonDynamoDB ddb, string backupArn)
    {
        try
        {
            await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn });
        }
        catch (BackupNotFoundException)
        {
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds)
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

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }
}
