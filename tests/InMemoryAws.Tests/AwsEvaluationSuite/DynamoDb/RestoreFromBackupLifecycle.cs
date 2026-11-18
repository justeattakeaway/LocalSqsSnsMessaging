// Translated from behavioral/dynamodb/check_restore_from_backup_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-from-backup-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreFromBackupLifecycle
{
    private const string Check = "restore-from-backup-lifecycle";

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

    /// <summary>A cleanup running <c>undo()</c> that returns only on the resource's own not-found codes.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>The (code, message) a rejected call returned; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> ErrorDetail<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, the call succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Task<ContinuousBackupsDescription> WaitContinuousBackupsReady(IAmazonDynamoDB ddb, string name, double timeoutS = 90) =>
        PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = name })).ContinuousBackupsDescription,
            d => d.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED,
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"continuous backups ready for {name}");

    private static async Task WaitTableDeleted(IAmazonDynamoDB ddb, string name, double timeoutS = 90)
    {
        async Task<string> Probe()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
            if (error is null)
            {
                return resp!.Table.TableStatus.Value;
            }

            if (error.Code == "ResourceNotFoundException")
            {
                return "GONE";
            }

            throw error.Exception;
        }

        await PollUntil(Probe, s => s == "GONE", timeoutS: timeoutS, intervalS: 1.0, description: $"table {name} deleted");
    }

    /// <summary>
    /// RestoreTableFromBackup produces a new independently describable and
    /// deletable table. Exercises RestoreTableFromBackup, DescribeTable,
    /// DeleteTable.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-from-backup-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var start = FidelityTime.StartNew();
        double RemainingBudget(double total = 1500.0, double floor = 30.0) => Math.Max(floor, total - start.Elapsed.TotalSeconds);

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
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("seed")), ("v", S("1"))));
        await WaitContinuousBackupsReady(ddb, t);
        var backupName = FidelityContext.UniqueName("sim2real-behav-bkp-");
        var arn = (await ddb.CreateBackupAsync(new CreateBackupRequest { TableName = t, BackupName = backupName })).BackupDetails.BackupArn;
        ctx.OnCleanup(Teardown(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn }), "BackupNotFoundException"));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = arn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: 90,
            intervalS: 2.0,
            description: "backup AVAILABLE");
        var targetName = FidelityContext.UniqueName("sim2real-behav-restored-");
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(targetName), "ResourceNotFoundException"));
        var restoreResp = await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = targetName, BackupArn = arn });
        var restoreStatus = restoreResp.TableDescription.TableStatus.Value;
        Ensure(
            restoreStatus is "CREATING" or "ACTIVE",
            $"expected RestoreTableFromBackup's own response to report CREATING or ACTIVE, got {Py.Repr(restoreStatus)}");
        var restoreSourceArn = restoreResp.TableDescription.RestoreSummary.SourceBackupArn;
        Ensure(restoreSourceArn == arn, $"expected RestoreSummary.SourceBackupArn {Py.Repr(arn)}, got {Py.Repr(restoreSourceArn)}");

        // Restore latency varies; the ACTIVE wait is bounded by the runner's remaining time, less
        // a reserve for the delete leg below, so a restore that never finishes fails here.
        var budget = RemainingBudget();
        var timeLeft = ctx.TimeLeftSeconds;
        budget = Math.Max(5.0, Math.Min(budget, timeLeft - 120.0));
        TableDescription table;
        try
        {
            table = await PollUntil(
                async () => (await ddb.DescribeTableAsync(targetName)).Table,
                tbl => tbl.TableStatus == TableStatus.ACTIVE,
                timeoutS: budget,
                intervalS: 3.0,
                description: $"table {targetName} ACTIVE");
        }
        catch (FidelityAssertionException)
        {
            // The poll's own deadline and this immediate re-read are not atomic: the restore can
            // cross into ACTIVE in that gap (observed on real AWS), so a fresh ACTIVE here is the
            // slow-but-correct case. Any other status fails: only ACTIVE is a finished restore.
            var current = (await ddb.DescribeTableAsync(targetName)).Table;
            Ensure(
                current.TableStatus == TableStatus.ACTIVE,
                $"expected the restored table {Py.Repr(targetName)} to reach ACTIVE within {budget.ToString("F0", CultureInfo.InvariantCulture)}s, got {Py.Repr(current.TableStatus.Value)}");
            table = current;
        }

        Ensure(table.TableName == targetName, $"expected the polled table's own name {Py.Repr(targetName)}, got {Py.Repr(table.TableName)}");
        var described = (await ddb.DescribeTableAsync(targetName)).Table;
        Ensure(described.TableName == targetName, $"expected DescribeTable to report {Py.Repr(targetName)}, got {Py.Repr(described.TableName)}");

        // Independently deletable without affecting the source table.
        await ddb.DeleteTableAsync(targetName);
        await WaitTableDeleted(ddb, targetName);
        var (code, message) = await ErrorDetail(() => ddb.DescribeTableAsync(targetName));
        Ensure(
            code == "ResourceNotFoundException",
            $"expected DescribeTable on the deleted restored table to raise ResourceNotFoundException, got {Py.Repr(code)}");
        var expectedMessage = $"Requested resource not found: Table: {targetName} not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        var (mutateCode, mutateMessage) = await ErrorDetail(() => ddb.PutItemAsync(targetName, Item(("pk", S("x")))));
        Ensure(
            mutateCode == "ResourceNotFoundException",
            $"expected PutItem against the deleted restored table to raise ResourceNotFoundException, got {Py.Repr(mutateCode)}");
        Ensure(mutateMessage == "Requested resource not found", $"expected message 'Requested resource not found', got {Py.Repr(mutateMessage)}");
        var sourceStatus = (await ddb.DescribeTableAsync(t)).Table.TableStatus.Value;
        Ensure(sourceStatus == "ACTIVE", $"expected the source table to remain ACTIVE after deleting the restored table, got {Py.Repr(sourceStatus)}");
        await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn });
    }
}
