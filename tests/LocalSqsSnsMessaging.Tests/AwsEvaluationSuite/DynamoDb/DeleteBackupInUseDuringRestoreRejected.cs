// Translated from behavioral/dynamodb/check_delete_backup_in_use_during_restore_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-backup-in-use-during-restore-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteBackupInUseDuringRestoreRejected
{
    private const string Check = "delete-backup-in-use-during-restore-rejected";

    private static double RemainingBudget(FidelityStopwatch start, double total = 850.0, double floor = 30.0) =>
        // Leave a margin under the 900s per-check ceiling for the steps after the restore wait.
        Math.Max(floor, total - start.Elapsed.TotalSeconds);

    /// <summary>
    /// DeleteBackup on a backup that is actively being restored from (RestoreTableFromBackup
    /// still in progress) raises BackupInUseException; once the restore completes, deleting the
    /// same backup succeeds (positive control) and the backup transitions to DELETED.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-backup-in-use-during-restore-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var start = FidelityTime.StartNew();
        var ddb = ctx.Dynamo;

        var sourceName = FidelityContext.UniqueName("sim2real-behav-src-");
        // PROVISIONED with a small fixed capacity (rather than PAY_PER_REQUEST) for both the
        // source and the restored table: on real AWS an on-demand table's restore needs
        // on-demand capacity metadata allocated on top of the storage-partition provisioning
        // every restore does, which measurably lengthens the CREATING->ACTIVE window versus a
        // small fixed-capacity PROVISIONED table.
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput(5, 5),
            Tags = [new Tag { Key = "sim2real-behav-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(sourceName));
        await WaitTableStatus(ddb, sourceName, "ACTIVE");
        await WaitContinuousBackupsReady(ddb, sourceName);
        await ddb.PutItemAsync(sourceName, Item(("pk", S("seed-item"))));

        var backupArn = (await ddb.CreateBackupAsync(new CreateBackupRequest
        {
            TableName = sourceName,
            BackupName = FidelityContext.UniqueName("sim2real-behav-bkp-"),
        })).BackupDetails.BackupArn;
        // DynamoDB's TagResource rejects a backup ARN outright ("Provided Arn is not a DynamoDB
        // resource arn") -- backups are not taggable; only the source and restored tables are.
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await WaitBackupStatus(ddb, backupArn, "AVAILABLE");

        var targetName = FidelityContext.UniqueName("sim2real-behav-restored-");
        var restoreDesc = (await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest
        {
            TargetTableName = targetName,
            BackupArn = backupArn,
            BillingModeOverride = BillingMode.PROVISIONED,
            ProvisionedThroughputOverride = new ProvisionedThroughput(5, 5),
        })).TableDescription;
        ctx.OnCleanup(() => CleanupDeleteTable(ddb, targetName));
        Ensure(
            restoreDesc.TableStatus == TableStatus.CREATING,
            $"expected the just-initiated restore target to be CREATING, got {restoreDesc.TableStatus}");
        Ensure(
            restoreDesc.RestoreSummary.RestoreInProgress == true,
            $"expected RestoreSummary.RestoreInProgress True on a fresh restore, got {Py.Repr(restoreDesc.RestoreSummary.RestoreInProgress)}");

        // Delete immediately, while the restore is (very likely) still in progress.
        var (code, message) = await ErrorDetail(async () => await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        Ensure(code == "BackupInUseException", $"expected BackupInUseException, got {code ?? "None"}");
        var inUseMessage = $"Backup is being used to restore table: {backupArn}";
        Ensure(message == inUseMessage, $"expected message {Py.Repr(inUseMessage)}, got {Py.Repr(message)}");

        // The backup must still exist and be usable, unaffected by the rejected delete.
        var stillThere = (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription;
        Ensure(
            stillThere.BackupDetails.BackupStatus == BackupStatus.AVAILABLE,
            $"expected the backup to remain AVAILABLE after its rejected delete, got {stillThere.BackupDetails.BackupStatus}");

        // Positive control: once the restore finishes, the backup is no longer "in use" and
        // DeleteBackup succeeds. A restore can take longer than 600s even for a small PROVISIONED
        // table, so the wait uses nearly all of the per-check budget.
        var activeDesc = await WaitTableStatus(ddb, targetName, "ACTIVE", timeoutSeconds: RemainingBudget(start), intervalSeconds: 5);
        // Only tag once the table is ACTIVE: right after RestoreTableFromBackup, TagResource on
        // its TableArn fails with ResourceNotFoundException.
        await ddb.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = activeDesc.TableArn,
            Tags = [new Tag { Key = "sim2real-behav-check", Value = Check }],
        });
        var deleteResp = (await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn })).BackupDescription;
        Ensure(
            deleteResp.BackupDetails.BackupStatus == BackupStatus.DELETED,
            $"expected the once-restore-completes delete to report DELETED, got {deleteResp.BackupDetails.BackupStatus}");

        // Read back through a different operation: the backup must actually be gone.
        var goneCode = await PollUntilNotFound(ddb, backupArn);
        Ensure(
            goneCode == "BackupNotFoundException",
            $"expected an independent DescribeBackup to 404 after the backup was deleted, got {goneCode ?? "None"}");
    }

    private static async Task CleanupDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 45, double intervalSeconds = 3)
    {
        // A still-CREATING or just-restored table can refuse DeleteTable with ResourceInUseException
        // for a while; retry within the 60s cleanup budget rather than leaking on the first refusal.
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
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

    /// <summary>The (code, message) a rejected call returned; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> ErrorDetail(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, the call succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<TableDescription> WaitTableStatus(IAmazonDynamoDB ddb, string tableName, string targetStatus, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            last = desc.TableStatus?.Value;
            if (last == targetStatus)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never reached {targetStatus}, last={last ?? "None"}");
    }

    private static async Task<BackupDescription> WaitBackupStatus(IAmazonDynamoDB ddb, string backupArn, string targetStatus, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription;
            last = desc.BackupDetails.BackupStatus?.Value;
            if (last == targetStatus)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"backup {backupArn} never reached {targetStatus}, last={last ?? "None"}");
    }

    private static async Task<string?> PollUntilNotFound(IAmazonDynamoDB ddb, string backupArn, double timeoutSeconds = 60, double intervalSeconds = 2)
    {
        // Real AWS does not always retire a just-deleted backup's DescribeBackup instantly; it may
        // answer DELETED for a short window before 404ing. Poll for the terminal not-found code
        // rather than trusting the first read.
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }));
            if (error is null)
            {
                last = resp!.BackupDescription.BackupDetails.BackupStatus?.Value;
            }
            else
            {
                last = error.Code;
                if (last == "BackupNotFoundException")
                {
                    return last;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        return last; // deliberately not raised: the caller's own assert is the check on this value
    }

    private static async Task<ContinuousBackupsDescription> WaitContinuousBackupsReady(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        // Immediately after a table reaches ACTIVE, DynamoDB is still provisioning the
        // continuous-backups infrastructure that CreateBackup depends on; calling CreateBackup
        // too early raises ContinuousBackupsUnavailableException ("Backups are being enabled
        // for this table"). Poll until the infra itself is ready before issuing any backup call.
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription;
            last = desc.ContinuousBackupsStatus?.Value;
            if (last == "ENABLED")
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"continuous backups for {tableName} never became ENABLED, last={last ?? "None"}");
    }
}
