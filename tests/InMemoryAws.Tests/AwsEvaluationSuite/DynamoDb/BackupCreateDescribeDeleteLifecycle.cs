// Translated from behavioral/dynamodb/check_backup_create_describe_delete_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.backup-create-describe-delete-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BackupCreateDescribeDeleteLifecycle
{
    private const string Check = "backup-create-describe-delete-lifecycle";

    /// <summary>
    /// A backup created for a table becomes describable/listable and is removed
    /// (404) after DeleteBackup. Exercises CreateBackup, DescribeBackup,
    /// DeleteBackup, ListBackups.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.backup-create-describe-delete-lifecycle")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await WaitContinuousBackupsReady(ddb, t);

        var backupName = FidelityContext.UniqueName("sim2real-behav-bkp-");
        var resp = await ddb.CreateBackupAsync(new CreateBackupRequest { TableName = t, BackupName = backupName });
        var arn = resp.BackupDetails.BackupArn;
        ctx.OnCleanup(() => AbsentOk(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn }), "BackupNotFoundException"));
        Ensure(
            resp.BackupDetails.BackupStatus.Value is "CREATING" or "AVAILABLE",
            $"expected CreateBackup status CREATING or AVAILABLE, got {resp.BackupDetails.BackupStatus.Value}");

        var desc = await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = arn })).BackupDescription.BackupDetails,
            d => d.BackupStatus == BackupStatus.AVAILABLE,
            timeoutS: 90,
            intervalS: 2.0,
            description: "backup AVAILABLE");
        Ensure(desc.BackupStatus == BackupStatus.AVAILABLE, $"expected AVAILABLE, got {desc.BackupStatus.Value}");
        Ensure(desc.BackupName == backupName, $"expected {backupName}, got {desc.BackupName}");

        var listed = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = t })).BackupSummaries ?? [];
        Ensure(listed.Any(b => b.BackupArn == arn), $"expected {arn} in ListBackups, got {Py.Repr(listed.Select(b => b.BackupArn))}");

        await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn });

        var notFoundMsg = $"Backup not found: {arn}";
        var (code, message) = await Error(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = arn }));
        Ensure(code == "BackupNotFoundException", $"expected BackupNotFoundException, got {code}");
        Ensure(message == notFoundMsg, $"expected {Py.Repr(notFoundMsg)}, got {Py.Repr(message)}");

        var listedAfter = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = t })).BackupSummaries ?? [];
        Ensure(
            !listedAfter.Any(b => b.BackupArn == arn),
            $"expected {arn} absent from ListBackups after delete, got {Py.Repr(listedAfter.Select(b => b.BackupArn))}");

        var (redeleteCode, redeleteMsg) = await Error(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = arn }));
        Ensure(
            redeleteCode == "BackupNotFoundException",
            $"expected a second DeleteBackup on the now-deleted backup to also 404, got {redeleteCode}");
        Ensure(redeleteMsg == notFoundMsg, $"expected {Py.Repr(notFoundMsg)}, got {Py.Repr(redeleteMsg)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 1.0, string description = "condition")
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

    /// <summary>
    /// Teardown for a resource the check itself may already have deleted: only `absent_code`
    /// is tolerated, any other failure still surfaces.
    /// </summary>
    private static async Task AbsentOk(Func<Task> fn, string absentCode)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != absentCode)
        {
            throw error.Exception;
        }
    }

    private static Task<ContinuousBackupsDescription> WaitContinuousBackupsReady(IAmazonDynamoDB ddb, string name, double timeoutS = 90) =>
        PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = name })).ContinuousBackupsDescription,
            d => d.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED,
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"continuous backups ready for {name}");
}
