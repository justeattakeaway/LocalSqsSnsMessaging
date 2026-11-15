// Translated from behavioral/dynamodb/check_backup_lifecycle_create_describe_delete_404.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.backup-lifecycle-create-describe-delete-404</summary>
[Category("AwsEvaluationSuite")]
public sealed class BackupLifecycleCreateDescribeDelete404
{
    private const string Check = "backup-lifecycle-create-describe-delete-404";

    /// <summary>
    /// CreateBackup -> AVAILABLE, DescribeBackup reflects it, DeleteBackup removes it and a
    /// subsequent DescribeBackup 404s (BackupNotFoundException).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.backup-lifecycle-create-describe-delete-404")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-bk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);

        var backupName = FidelityContext.UniqueName("sim2real-behav-bkname-");

        async Task<CreateBackupResponse?> AttemptCreateBackup()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = tableName, BackupName = backupName }));
            if (error is null)
            {
                return resp;
            }

            // A just-created table has continuous backups enabling in the background; a
            // CreateBackup issued too soon is rejected until that settles.
            if (error.Code == "ContinuousBackupsUnavailableException")
            {
                return null;
            }

            throw error.Exception;
        }

        var createResp = (await PollUntil(AttemptCreateBackup, resp => resp is not null, timeoutS: 180, intervalS: 5))!;
        var backupArn = createResp.BackupDetails.BackupArn;
        Ensure(createResp.BackupDetails.BackupName == backupName, "assert create_resp['BackupDetails']['BackupName'] == backup_name");

        var described = await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription,
            desc => desc.BackupDetails.BackupStatus == BackupStatus.AVAILABLE,
            timeoutS: 180,
            intervalS: 3);
        Ensure(described.BackupDetails.BackupStatus == BackupStatus.AVAILABLE, "assert described['BackupDetails']['BackupStatus'] == 'AVAILABLE'");
        Ensure(described.BackupDetails.BackupType == BackupType.USER, "assert described['BackupDetails']['BackupType'] == 'USER'");
        Ensure(described.SourceTableDetails.TableName == tableName, "assert described['SourceTableDetails']['TableName'] == table_name");

        var listedBefore = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = tableName })).BackupSummaries ?? [];
        var matchingBefore = listedBefore.Where(b => b.BackupArn == backupArn).ToList();
        Ensure(matchingBefore.Count == 1, $"expected {backupArn} in ListBackups, got {Py.Repr(listedBefore.Select(b => b.BackupArn))}");
        Ensure(matchingBefore[0].BackupStatus == BackupStatus.AVAILABLE, "assert matching_before[0]['BackupStatus'] == 'AVAILABLE'");

        var deleteResp = await ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn });
        Ensure(
            deleteResp.BackupDescription.BackupDetails.BackupStatus == BackupStatus.DELETED,
            "assert delete_resp['BackupDescription']['BackupDetails']['BackupStatus'] == 'DELETED'");

        async Task<(string? State, string? Message)> DescribeAfterDelete()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn }));
            if (error is null)
            {
                return (resp!.BackupDescription.BackupDetails.BackupStatus.Value, null);
            }

            return (error.Code, error.Message ?? "");
        }

        var (finalState, finalMessage) = await PollUntil(DescribeAfterDelete, state => state.State == "BackupNotFoundException", timeoutS: 120, intervalS: 3);
        Ensure(finalState == "BackupNotFoundException", finalState ?? "None");
        var notFound = $"Backup not found: {backupArn}";
        Ensure(finalMessage == notFound, $"DescribeBackup message {Py.Repr(finalMessage)}, expected {Py.Repr(notFound)}");

        var listedAfter = (await ddb.ListBackupsAsync(new ListBackupsRequest { TableName = tableName })).BackupSummaries ?? [];
        Ensure(!listedAfter.Any(b => b.BackupArn == backupArn), Py.Repr(listedAfter.Select(b => b.BackupArn)));

        var (redeleteCode, redeleteMessage) = await ExpectErrorCode(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        Ensure(redeleteCode == "BackupNotFoundException", redeleteCode ?? "None");
        Ensure(redeleteMessage == notFound, $"DeleteBackup message {Py.Repr(redeleteMessage)}, expected {Py.Repr(notFound)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<(string? Code, string? Message)> ExpectErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected failure, call succeeded with {Py.Repr(result)}");
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
}
