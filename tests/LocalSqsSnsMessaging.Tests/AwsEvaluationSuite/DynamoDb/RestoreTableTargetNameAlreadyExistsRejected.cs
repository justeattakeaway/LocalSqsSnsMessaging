// Translated from behavioral/dynamodb/check_restore_table_target_name_already_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-table-target-name-already-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreTableTargetNameAlreadyExistsRejected
{
    /// <summary>
    /// RestoreTableFromBackup onto a TargetTableName held by a live table is refused
    /// TableAlreadyExistsException and leaves that table untouched; once the occupying table is
    /// removed, the identical restore succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-table-target-name-already-exists-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var sourceTable = FidelityContext.UniqueName("sim2real-behav-rej-src-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceTable,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, sourceTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(sourceTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);

        // A freshly-created table's continuous backups (PITR plumbing) take a short while to
        // finish enabling; CreateBackup fails with ContinuousBackupsUnavailableException until
        // that's done, which is unrelated to the TableAlreadyExistsException this check targets.
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = sourceTable }))
                .ContinuousBackupsDescription.ContinuousBackupsStatus.Value,
            status => status == "ENABLED",
            timeoutS: 120,
            intervalS: 3);
        var backupArn = (await ddb.CreateBackupAsync(new CreateBackupRequest
        {
            TableName = sourceTable,
            BackupName = FidelityContext.UniqueName("sim2real-behav-rej-bk-"),
        })).BackupDetails.BackupArn;
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            status => status == "AVAILABLE",
            timeoutS: 180,
            intervalS: 3);

        // A second, already-existing, live table occupies the name we will (illegitimately) ask
        // RestoreTableFromBackup to target.
        var occupiedTable = FidelityContext.UniqueName("sim2real-behav-rej-occupied-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = occupiedTable,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, occupiedTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(occupiedTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);
        var (code, message) = await Error(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = occupiedTable, BackupArn = backupArn }));
        Ensure(code == "TableAlreadyExistsException", $"expected TableAlreadyExistsException, got {Py.Repr(code)}");
        var expected = $"Table already exists: {occupiedTable}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        // The occupied table must be completely unaffected by the rejected restore attempt.
        var stillActive = (await ddb.DescribeTableAsync(occupiedTable)).Table.TableStatus.Value;
        Ensure(stillActive == "ACTIVE", $"expected the occupied table to remain ACTIVE after the rejected restore, got {Py.Repr(stillActive)}");

        // Remove the obstruction and demand the identical call now succeeds. A target that
        // rejects RestoreTableFromBackup unconditionally with this code -- rather than only
        // when the name collides -- passes every assertion above and must fail here.
        await ddb.DeleteTableAsync(occupiedTable);
        await PollUntil(() => TableStatusOrGone(ddb, occupiedTable), status => status is null, timeoutS: 120, intervalS: 3);
        await RetryRestoreUntilAccepted(ddb, occupiedTable, backupArn, timeoutS: 60, intervalS: 3);
        var restoredStatus = await PollUntil(
            async () => (await ddb.DescribeTableAsync(occupiedTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 600,
            intervalS: 5);
        Ensure(restoredStatus == "ACTIVE", $"expected the identical restore to succeed once the target name was free, got {Py.Repr(restoredStatus)}");
    }

    // ===== helpers =====
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string?> TableStatusOrGone(IAmazonDynamoDB ddb, string tableName)
    {
        var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
        if (error is null)
        {
            return resp!.Table.TableStatus.Value;
        }

        if (error.Code == "ResourceNotFoundException")
        {
            return null;
        }

        throw error.Exception;
    }

    /// <summary>
    /// DynamoDB may briefly still consider a just-deleted name reserved; retry the
    /// restore call itself (not just the readiness poll) until it is accepted.
    /// </summary>
    private static async Task RetryRestoreUntilAccepted(IAmazonDynamoDB ddb, string tableName, string backupArn, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = tableName, BackupArn = backupArn }));
            if (error is null)
            {
                return;
            }

            if (error.Code != "TableAlreadyExistsException")
            {
                throw error.Exception;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"the identical restore was still rejected as TableAlreadyExistsException {timeoutS}s after the occupying table was deleted");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    // A table still transitioning (e.g. mid-restore, still CREATING) refuses DeleteTable with
    // ResourceInUseException: retried every 2 s for up to 300 s, within the suite's cleanup
    // budget. A table already gone is fine; any other error raises.
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
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

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
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

        throw Fail($"condition not met within {timeoutS}s, last value: {Py.Repr(last)}");
    }
}
