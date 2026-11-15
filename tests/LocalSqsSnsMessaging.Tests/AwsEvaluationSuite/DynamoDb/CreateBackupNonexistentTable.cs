// Translated from behavioral/dynamodb/check_create_backup_nonexistent_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-backup-nonexistent-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateBackupNonexistentTable
{
    private const string CheckId = "create-backup-nonexistent-table";

    /// <summary>
    /// CreateBackup on a nonexistent table fails with TableNotFoundException and AWS's exact message and
    /// lists no backup, DescribeTable confirms the table is absent, and CreateBackup on a real table
    /// succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-backup-nonexistent-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var realTable = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = realTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, realTable));
        await WaitTableActive(ddb, realTable);
        await WaitContinuousBackupsEnabled(ddb, realTable);

        var realBackupName = FidelityContext.UniqueName("sim2real-behav-bkp-");
        var created = await CreateBackupWithRetry(ddb, realTable, realBackupName);
        var realBackupArn = created.BackupDetails.BackupArn;
        Ensure(
            created.BackupDetails.BackupStatus == "CREATING" || created.BackupDetails.BackupStatus == "AVAILABLE",
            $"positive control: expected CreateBackup to succeed on a real table, got {Py.Repr(new Dictionary<string, object?> { ["BackupArn"] = created.BackupDetails.BackupArn, ["BackupStatus"] = created.BackupDetails.BackupStatus?.Value })}");
        ctx.OnCleanup(() => DeleteBackupIgnore(ddb, realBackupArn));

        var missingTable = realTable + "-absent";
        var (code, message) = await Error(() => ddb.CreateBackupAsync(new CreateBackupRequest
        {
            TableName = missingTable,
            BackupName = FidelityContext.UniqueName("sim2real-behav-bkp-"),
        }));
        Ensure(code == "TableNotFoundException", $"expected TableNotFoundException, got {code}");
        var expectedMsg = $"Table not found: {missingTable}";
        Ensure(message == expectedMsg, $"expected {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        var listed = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = missingTable })).BackupSummaries;
        Ensure(listed is { Count: 0 }, $"expected no backups for a nonexistent table, got {Py.Repr(listed?.Select(b => b.BackupArn).ToList())}");

        var (describeCode, describeMsg) = await Error(() => ddb.DescribeTableAsync(missingTable));
        Ensure(describeCode == "ResourceNotFoundException", $"expected the target table to independently not exist, got {describeCode}");
        var expectedDescribeMsg = $"Requested resource not found: Table: {missingTable} not found";
        Ensure(describeMsg == expectedDescribeMsg, $"expected {Py.Repr(expectedDescribeMsg)}, got {Py.Repr(describeMsg)}");
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

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
    }

    private static async Task<CreateBackupResponse> CreateBackupWithRetry(IAmazonDynamoDB ddb, string tableName, string backupName, double timeoutSeconds = 120.0, double intervalSeconds = 5.0)
    {
        // Immediately after a table becomes ACTIVE, its continuous-backups
        // infrastructure can still be settling, which makes CreateBackup
        // transiently reject with ContinuousBackupsUnavailableException. Retry
        // within a bound instead of treating this as a hard failure.
        var clock = FidelityTime.StartNew();
        AwsError? lastErr = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (response, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = backupName }));
            if (error is null)
            {
                return response!;
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

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == "ACTIVE")
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
            if (desc.ContinuousBackupsStatus == "ENABLED")
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"continuous backups for {tableName} never became ENABLED");
    }

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
