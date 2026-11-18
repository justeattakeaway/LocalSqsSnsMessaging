// Translated from behavioral/dynamodb/check_backup_create_describe_list_delete_404.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.backup-create-describe-list-delete-404</summary>
[Category("AwsEvaluationSuite")]
public sealed class BackupCreateDescribeListDelete404
{
    private const string Check = "backup-create-describe-list-delete-404";

    /// <summary>
    /// A table backup transitions CREATING->AVAILABLE, appears in ListBackups,
    /// and DescribeBackup 404s (BackupNotFoundException) once deleted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.backup-create-describe-list-delete-404")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-backup-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        var backupName = FidelityContext.UniqueName("sim2real-behav-backup-");
        var created = await CreateBackupWithRetry(ddb, tableName, backupName);
        var backupArn = created.BackupDetails.BackupArn;
        Ensure(created.BackupDetails.BackupStatus.Value is "CREATING" or "AVAILABLE", "assert created['BackupDetails']['BackupStatus'] in ('CREATING', 'AVAILABLE')");
        ctx.OnCleanup(() => DeleteBackupIgnore(ddb, backupArn));

        var status = await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: 120,
            intervalS: 3);
        Ensure(status == "AVAILABLE", "assert status == 'AVAILABLE'");

        var listed = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = tableName })).BackupSummaries ?? [];
        var matching = listed.Where(b => b.BackupArn == backupArn).ToList();
        Ensure(matching.Count == 1, $"expected backup {backupArn} in ListBackups, got {Py.Repr(listed.Select(b => b.BackupArn))}");
        Ensure(matching[0].BackupStatus == BackupStatus.AVAILABLE, "assert matching[0]['BackupStatus'] == 'AVAILABLE'");
        Ensure(matching[0].TableName == tableName, "assert matching[0]['TableName'] == table_name");

        var deleteResp = await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn });
        Ensure(
            deleteResp.BackupDescription.BackupDetails.BackupStatus == BackupStatus.DELETED,
            "assert delete_resp['BackupDescription']['BackupDetails']['BackupStatus'] == 'DELETED'");

        async Task<(string? Code, string? Message)> DescribeAfterDelete()
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }));
            return error is not null ? (error.Code, error.Message) : ("STILL_PRESENT", "");
        }

        var notFoundMsg = $"Backup not found: {backupArn}";
        var final = await PollUntil(DescribeAfterDelete, v => v.Code != "STILL_PRESENT", timeoutS: 60, intervalS: 3);
        Ensure(
            final == ("BackupNotFoundException", notFoundMsg),
            $"expected DescribeBackup after delete to give BackupNotFoundException {Py.Repr(notFoundMsg)}, got {Py.Repr(final)}");

        var listedAfterDelete = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = tableName })).BackupSummaries ?? [];
        Ensure(!listedAfterDelete.Any(b => b.BackupArn == backupArn), Py.Repr(listedAfterDelete.Select(b => b.BackupArn)));

        var (redeleteCode, redeleteMsg) = await Error(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        Ensure(
            redeleteCode == "BackupNotFoundException",
            $"expected a second DeleteBackup on the now-deleted backup to also 404, got {redeleteCode}");
        Ensure(redeleteMsg == notFoundMsg, $"expected {Py.Repr(notFoundMsg)}, got {Py.Repr(redeleteMsg)}");
    }

    /// <summary>The (Code, Message) of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static async Task<CreateBackupResponse> CreateBackupWithRetry(IAmazonDynamoDB ddb, string tableName, string backupName, double timeoutS = 120, double intervalS = 5)
    {
        // Immediately after a table becomes ACTIVE, its continuous-backups
        // infrastructure can still be settling ("Backups are being enabled for
        // ..."), which makes CreateBackup transiently reject with
        // ContinuousBackupsUnavailableException. Retry within a bound instead of
        // treating this as a hard failure.
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = backupName }));
            if (error is null)
            {
                return resp!;
            }

            if (error.Code != "ContinuousBackupsUnavailableException")
            {
                throw error.Exception;
            }

            lastErr = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"CreateBackup still unavailable after {timeoutS.ToString(CultureInfo.InvariantCulture)}s: {lastErr}");
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

    private static Task<string> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value,
            s => s == "ACTIVE",
            timeoutS: timeoutS,
            intervalS: 3);

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task DeleteBackupIgnore(IAmazonDynamoDB ddb, string backupArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        if (error is not null && error.Code != "BackupNotFoundException")
        {
            throw error.Exception;
        }
    }
}
