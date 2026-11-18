// Translated from behavioral/dynamodb/check_restore_from_backup_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-from-backup-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreFromBackupNotFoundRejected
{
    /// <summary>
    /// RestoreTableFromBackup naming a nonexistent backup returns BackupNotFoundException and leaves
    /// no table at the destination, while the identical call against a real backup succeeds, so the
    /// refusal is about the missing backup.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-from-backup-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var accountId = ctx.Account;

        // --- positive control: RestoreTableFromBackup against a real backup is accepted ---
        var sourceTable = FidelityContext.UniqueName("sim2real-rfb-src-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceTable,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "restore-from-backup-not-found-rejected" }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, sourceTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(sourceTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 60,
            intervalS: 2);
        var backupName = FidelityContext.UniqueName("sim2real-rfb-bk-");

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

        var backupArn = (await PollUntil(AttemptCreateBackup, resp => resp is not null, timeoutS: 60, intervalS: 3))!.BackupDetails.BackupArn;
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            status => status == "AVAILABLE",
            timeoutS: 90,
            intervalS: 3);
        var positiveTarget = FidelityContext.UniqueName("sim2real-rfb-pos-");
        var positiveResp = await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = positiveTarget, BackupArn = backupArn });
        Ensure(
            positiveResp.TableDescription.TableName == positiveTarget,
            $"positive control: expected RestoreTableFromBackup to accept a real backup, got table name {Py.Repr(positiveResp.TableDescription.TableName)}");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, positiveTarget));

        // --- negative: RestoreTableFromBackup referencing a nonexistent backup id ---
        // Real DynamoDB backup ids are a 14-digit (zero-padded) millisecond epoch timestamp,
        // a dash, and an 8-character hex suffix (e.g. "01639426382000-1509b1b3"). A backup id
        // of the wrong shape (e.g. only 12 digits) is rejected by ARN-shape validation with a
        // generic ValidationException *before* DynamoDB ever looks up whether a backup with
        // that id exists — so it never reaches BackupNotFoundException. Use a well-formed but
        // nonexistent id so the request actually exercises the not-found lookup.
        var fakeTable = FidelityContext.UniqueName("sim2real-notable-");
        var fakeBackupId = $"0{FidelityTime.UtcNow.ToUnixTimeMilliseconds()}-abcdef03";
        var fakeArn = $"arn:aws:dynamodb:{ctx.Region}:{accountId}:table/{fakeTable}/backup/{fakeBackupId}";
        var targetName = FidelityContext.UniqueName("sim2real-restored-");
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, targetName));
        var (code, message) = await Error(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = targetName, BackupArn = fakeArn }));
        Ensure(code == "BackupNotFoundException", $"expected BackupNotFoundException, got {code}");
        var expected = $"Backup not found: {fakeArn}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        // the rejected restore must not have created a table at the destination — independent read
        var (notFoundCode, notFoundMessage) = await Error(() => ddb.DescribeTableAsync(targetName));
        Ensure(
            notFoundCode == "ResourceNotFoundException",
            $"restore of {Py.Repr(targetName)} was rejected but the table is still describable (code={Py.Repr(notFoundCode)}) — a table was created despite the rejection");
        var expectedNotFound = $"Requested resource not found: Table: {targetName} not found";
        Ensure(notFoundMessage == expectedNotFound, $"unexpected message {Py.Repr(notFoundMessage)}");
    }

    // ===== helpers =====
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, call succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Teardown delete: a table already gone is fine; ResourceInUseException (a restore still
    /// creating the table) is retried every 2 s up to timeout_s; any other error raises.
    /// </summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutS = 300.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
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

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
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
