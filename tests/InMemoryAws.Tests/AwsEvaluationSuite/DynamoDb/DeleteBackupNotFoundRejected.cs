// Translated from behavioral/dynamodb/check_delete_backup_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-backup-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteBackupNotFoundRejected
{
    /// <summary>
    /// DeleteBackup on an existing backup succeeds with status DELETED; the same call on that deleted backup,
    /// repeated, and on a backup id that never existed raises BackupNotFoundException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-backup-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;

        var tableName = FidelityContext.UniqueName("sim2real-behav-notfound-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);
        await WaitContinuousBackupsEnabled(ddb, tableName);

        var backupName = FidelityContext.UniqueName("sim2real-behav-notfound-bk-");
        var backupArn = await CreateBackupWithRetry(ddb, tableName, backupName);
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await WaitBackupAvailable(ddb, backupArn);

        // Positive control: DeleteBackup on a real, existing backup succeeds. Without
        // this, a target that returns BackupNotFoundException for every DeleteBackup
        // call — never actually deleting anything — would pass the negative below too.
        var deleteResp = await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn });
        var deletedStatus = deleteResp.BackupDescription.BackupDetails.BackupStatus;
        Ensure(deletedStatus == BackupStatus.DELETED, $"expected DELETED, got {deletedStatus}");

        // Independent read confirms the backup is actually gone.
        var (goneCode, goneMsg) = await Error(async () => await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }));
        Ensure(goneCode == "BackupNotFoundException", $"expected BackupNotFoundException, got {goneCode ?? "None"}");
        var expectedGone = $"Backup not found: {backupArn}";
        Ensure(goneMsg == expectedGone, $"expected message {Py.Repr(expectedGone)}, got {Py.Repr(goneMsg)}");

        // A second DeleteBackup against that now-dead id is rejected too — the delete
        // did not silently no-op or succeed twice.
        var (redeleteCode, redeleteMsg) = await Error(async () => await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        Ensure(redeleteCode == "BackupNotFoundException", $"expected BackupNotFoundException, got {redeleteCode ?? "None"}");
        Ensure(redeleteMsg == expectedGone, $"expected message {Py.Repr(expectedGone)}, got {Py.Repr(redeleteMsg)}");

        // A backup id that never existed at all is rejected the same way. Real
        // DynamoDB backup ids are a 14-digit (zero-padded) millisecond epoch timestamp,
        // a dash, and an 8-character hex suffix (e.g. "01639426382000-1509b1b3"). A
        // backup id of the wrong shape (e.g. only 12 digits) is rejected by ARN-shape
        // validation with a generic ValidationException *before* DynamoDB ever looks
        // up whether a backup with that id exists — so it never reaches
        // BackupNotFoundException. Use a well-formed but nonexistent id so the
        // request actually exercises the not-found lookup.
        var fakeBackupId = "0" + FidelityTime.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-abcdef02";
        var fakeArn = $"arn:aws:dynamodb:{ctx.Region}:{accountId}:table/{tableName}/backup/{fakeBackupId}";
        var (neverExistedCode, neverExistedMsg) = await Error(async () => await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = fakeArn }));
        Ensure(neverExistedCode == "BackupNotFoundException", $"expected BackupNotFoundException, got {neverExistedCode ?? "None"}");
        var expectedFake = $"Backup not found: {fakeArn}";
        Ensure(neverExistedMsg == expectedFake, $"expected message {Py.Repr(expectedFake)}, got {Py.Repr(neverExistedMsg)}");
    }

    /// <summary>Call fn(); return the botocore (Error.Code, Error.Message). An accepted call raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task<ContinuousBackupsDescription> WaitContinuousBackupsEnabled(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription;
            if (desc.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"continuous backups for {tableName} never became ENABLED");
    }

    private static async Task<string> CreateBackupWithRetry(IAmazonDynamoDB ddb, string tableName, string backupName, double timeoutSeconds = 120.0, double intervalSeconds = 5.0)
    {
        // Immediately after a table becomes ACTIVE, its continuous-backups
        // infrastructure can still be settling ("Backups are being enabled for
        // ..."), which makes CreateBackup transiently reject with
        // ContinuousBackupsUnavailableException. Retry within a bound instead of
        // treating this as a hard failure.
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = backupName }));
            if (error is null)
            {
                return resp!.BackupDetails.BackupArn;
            }

            if (error.Code != "ContinuousBackupsUnavailableException")
            {
                throw error.Exception;
            }

            lastErr = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"CreateBackup still unavailable after {timeoutSeconds}s: {lastErr}");
    }

    private static async Task<BackupDescription> WaitBackupAvailable(IAmazonDynamoDB ddb, string backupArn, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription;
            if (desc.BackupDetails.BackupStatus == BackupStatus.AVAILABLE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"backup {backupArn} never became AVAILABLE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static async Task SafeDeleteBackup(IAmazonDynamoDB ddb, string backupArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        if (error is not null && error.Code != "BackupNotFoundException")
        {
            throw error.Exception;
        }
    }
}
