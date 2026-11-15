// Translated from behavioral/dynamodb/check_restore_table_from_backup_creates_active_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-table-from-backup-creates-active-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreTableFromBackupCreatesActiveTable
{
    /// <summary>
    /// RestoreTableFromBackup produces a new table that reaches ACTIVE with the source
    /// backup's schema, and the data present at backup time is present in the restored table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-table-from-backup-creates-active-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);

        // The runner enforces ONE hard wall-clock budget for the WHOLE check regardless of any
        // individual poll's own timeout_s — a poll timeout bigger than what's left of that budget
        // can never actually be honored; it only guarantees the process gets killed mid-poll with an
        // unhelpful "Check timed out" instead of a clean assertion. Track elapsed time from the start
        // and hand the dominant, unavoidably-async stage (the restore reaching ACTIVE) whatever of the
        // real budget remains, instead of a static value that outruns the cap.
        // `total` is the check's wall-clock budget less a 100 s margin for the steps after the
        // restore-active wait.
        var start = FidelityTime.StartNew();
        double RemainingBudget(double total = 1700.0, double floor = 30.0) => Math.Max(floor, total - start.Elapsed.TotalSeconds);

        var ddb = ctx.Dynamo;
        var sourceTable = FidelityContext.UniqueName("sim2real-behav-rfb-src-");
        List<KeySchemaElement> keySchema = [new("pk", KeyType.HASH)];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceTable,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = keySchema,
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, sourceTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(sourceTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 60,
            intervalS: 2);
        await ddb.PutItemAsync(sourceTable, Item(("pk", S("seed-item")), ("val", S("hello"))));
        var backupName = FidelityContext.UniqueName("sim2real-behav-rfb-bk-");

        async Task<CreateBackupResponse?> AttemptCreateBackup()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = sourceTable, BackupName = backupName }));
            if (error is not null)
            {
                // A just-created table has continuous backups enabling in the background; a
                // CreateBackup issued too soon is rejected until that settles.
                if (error.Code == "ContinuousBackupsUnavailableException")
                {
                    return null;
                }

                throw error.Exception;
            }

            return resp;
        }

        // The harness enforces one hard wall-clock budget for the WHOLE check regardless of any
        // individual poll's own timeout_s, and RestoreTableFromBackup's own completion is the
        // dominant cost against that budget — keep every OTHER stage as tight as real AWS allows
        // so the terminal wait gets the maximum remaining share.
        var backupArn = (await PollUntil(AttemptCreateBackup, resp => resp is not null, timeoutS: 60, intervalS: 3))!.BackupDetails.BackupArn;
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            status => status == "AVAILABLE",
            timeoutS: 90,
            intervalS: 3);
        var targetTable = FidelityContext.UniqueName("sim2real-behav-rfb-dst-");
        var restoreResp = await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = targetTable, BackupArn = backupArn });
        Ensure(restoreResp.TableDescription.TableName == targetTable, "restore_resp[\"TableDescription\"][\"TableName\"] == target_table");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, targetTable));

        // Restoring a table from a backup (even a tiny one) commonly takes DynamoDB a couple of
        // minutes to reach ACTIVE on real AWS — give this dominant, unavoidably-async stage whatever
        // is left of the runner's hard wall-clock budget (a static timeout_s bigger than that
        // remaining budget can never be honored: the runner kills the whole check at the ceiling
        // regardless, so the poll should use the real remaining time rather than a number that
        // outruns it).
        // RestoreSummary is carried by RestoreTableFromBackup's own TableDescription; DescribeTable on
        // the ACTIVE table was observed on real AWS to omit it, for the whole 1700s poll.
        Ensure(
            restoreResp.TableDescription.RestoreSummary.SourceBackupArn == backupArn,
            "restore_resp[\"TableDescription\"][\"RestoreSummary\"][\"SourceBackupArn\"] == backup_arn");
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(targetTable)).Table,
            table => table.TableStatus == TableStatus.ACTIVE,
            timeoutS: RemainingBudget(),
            intervalS: 5);
        Ensure(described.TableStatus == TableStatus.ACTIVE, "described[\"TableStatus\"] == \"ACTIVE\"");
        Ensure(
            described.KeySchema.Select(k => (k.AttributeName, k.KeyType.Value)).SequenceEqual(keySchema.Select(k => (k.AttributeName, k.KeyType.Value))),
            "described[\"KeySchema\"] == key_schema");
        var restoredItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = targetTable, Key = Item(("pk", S("seed-item"))) })).Item;
        Ensure(restoredItem is not null, "restored table is missing the item present at backup time");
        Ensure(restoredItem["val"].S == "hello", "restored_item[\"val\"][\"S\"] == \"hello\"");
    }

    /// <summary>
    /// Delete the table; an absent table is done. A table still CREATING or UPDATING refuses the
    /// delete with ResourceInUseException, which is retried until <paramref name="timeoutS"/>.
    /// </summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutS = 240.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(tableName);
                return;
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (ResourceInUseException)
            {
                if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Delete the backup; one already gone is done.</summary>
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
