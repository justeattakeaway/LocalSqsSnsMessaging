// Translated from behavioral/dynamodb/check_restore_backup_name_conflict.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-backup-name-conflict</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreBackupNameConflict
{
    private const string Check = "restore-backup-name-conflict";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 1.0, string description = "condition")
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

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Call fn() and return its error code and message; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected rejection, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// DeleteTable is refused with ResourceInUseException while the restore or a tag update is
    /// still in progress; retry to a deadline.
    /// </summary>
    private static async Task DeleteTablePastTagUpdate(IAmazonDynamoDB ddb, string name, double timeoutS = 600)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }
    }

    /// <summary>
    /// DeleteBackup is refused with BackupInUseException while a restore from it is in progress;
    /// retry to a deadline.
    /// </summary>
    private static async Task DeleteBackup(IAmazonDynamoDB ddb, string backupArn, double timeoutS = 300)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
            if (error is null || error.Code == "BackupNotFoundException")
            {
                return;
            }

            if (error.Code != "BackupInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }
    }

    private static Task<ContinuousBackupsDescription> WaitContinuousBackupsReady(IAmazonDynamoDB ddb, string name, double timeoutS = 90) =>
        PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = name })).ContinuousBackupsDescription,
            d => d.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED,
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"continuous backups ready for {name}");

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>
    /// RestoreTableFromBackup onto an existing ACTIVE table name fails with
    /// TableAlreadyExistsException and leaves that table untouched; a free name restores fine.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-backup-name-conflict")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var source = await SimpleTable(ctx, ddb);
        await ddb.PutItemAsync(source, Item(("pk", S("seed")), ("v", S("seed-data"))));
        var otherActiveName = await SimpleTable(ctx, ddb); // already-ACTIVE table whose name we'll collide with
        await ddb.PutItemAsync(otherActiveName, Item(("pk", S("sentinel")), ("v", S("untouched"))));
        await WaitContinuousBackupsReady(ddb, source);
        var backupArn = (await ddb.CreateBackupAsync(new CreateBackupRequest
        {
            TableName = source,
            BackupName = FidelityContext.UniqueName("sim2real-behav-bkp-"),
        })).BackupDetails.BackupArn;
        ctx.OnCleanup(() => DeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: 90,
            intervalS: 2.0,
            description: "backup AVAILABLE");

        // Positive control: proves the later rejection is about the name collision, not the backup.
        var freeName = FidelityContext.UniqueName("sim2real-behav-restored-");
        await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = freeName, BackupArn = backupArn });
        ctx.OnCleanup(() => DeleteTablePastTagUpdate(ddb, freeName));
        await ddb.WaitForTableExists(freeName, delaySeconds: 2, maxAttempts: 250);
        await ddb.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = (await ddb.DescribeTableAsync(freeName)).Table.TableArn,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        var controlItem = await ddb.GetItemAsync(new GetItemRequest { TableName = freeName, Key = Item(("pk", S("seed"))), ConsistentRead = true });
        Ensure(
            controlItem.Item?.GetValueOrDefault("v")?.S == "seed-data",
            $"positive control: restoring to a free name should carry the source data, got {Py.Repr(controlItem.Item)}");
        var before = (await ddb.DescribeTableAsync(otherActiveName)).Table;
        var (code, message) = await Error(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = otherActiveName, BackupArn = backupArn }));
        Ensure(code == "TableAlreadyExistsException", $"expected TableAlreadyExistsException, got {code}");
        var expected = $"Table already exists: {otherActiveName}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        // The collision target keeps its identity, status and data (no partial restore).
        var after = (await ddb.DescribeTableAsync(otherActiveName)).Table;
        Ensure(after.TableStatus == TableStatus.ACTIVE, $"other_active table status changed to {Py.Repr(after.TableStatus.Value)} after rejected restore");
        Ensure(after.TableArn == before.TableArn, "other_active table was replaced (ARN changed) by the rejected restore");
        Ensure(after.CreationDateTime == before.CreationDateTime, "other_active table was recreated (CreationDateTime changed) by the rejected restore");
        var sentinel = await ddb.GetItemAsync(new GetItemRequest { TableName = otherActiveName, Key = Item(("pk", S("sentinel"))), ConsistentRead = true });
        Ensure(sentinel.Item?.GetValueOrDefault("v")?.S == "untouched", $"expected original sentinel item intact, got {Py.Repr(sentinel.Item)}");
        var leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = otherActiveName, Key = Item(("pk", S("seed"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"source table data leaked into the collision target despite the rejection: {Py.Repr(leaked.Item)}");
    }
}
