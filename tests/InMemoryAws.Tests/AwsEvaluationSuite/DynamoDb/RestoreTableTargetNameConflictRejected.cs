// Translated from behavioral/dynamodb/check_restore_table_target_name_conflict_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-table-target-name-conflict-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreTableTargetNameConflictRejected
{
    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav-restconf" },
    ];

    /// <summary>
    /// RestoreTableFromBackup to the name of a live table raises TableAlreadyExistsException with the exact message and
    /// leaves that table and its data unchanged; once the table is deleted, the same call is accepted and restores
    /// from that backup.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-table-target-name-conflict-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var sourceName = FidelityContext.UniqueName("sim2real-behav-restconf-src-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, sourceName));
        await WaitTableActive(ddb, sourceName);
        await ddb.PutItemAsync(sourceName, Item(("pk", S("k")), ("v", S("src-data"))));
        var backupName = FidelityContext.UniqueName("sim2real-behav-restconf-bkp-");

        async Task<CreateBackupResponse?> AttemptCreateBackup()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = sourceName, BackupName = backupName }));
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
        ctx.OnCleanup(() => DeleteBackupIgnore(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: 120,
            intervalS: 3);
        var collidingName = FidelityContext.UniqueName("sim2real-behav-restconf-collide-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = collidingName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, collidingName));
        await WaitTableActive(ddb, collidingName);
        await ddb.PutItemAsync(collidingName, Item(("pk", S("existing")), ("v", S("must-not-be-overwritten"))));
        var before = (await ddb.DescribeTableAsync(collidingName)).Table;
        var (code, message) = await ExpectRejection(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = collidingName, BackupArn = backupArn }));
        Ensure(code == "TableAlreadyExistsException", $"expected TableAlreadyExistsException, got {Py.Repr(code)}");
        var expected = $"Table already exists: {collidingName}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");
        var after = (await ddb.DescribeTableAsync(collidingName)).Table;
        Ensure(
            after.TableStatus == TableStatus.ACTIVE,
            $"a rejected restore left the colliding table as {Py.Repr(after.TableStatus.Value)}, expected it to remain ACTIVE");
        Ensure(
            after.CreationDateTime == before.CreationDateTime,
            $"the colliding table's CreationDateTime moved from {before.CreationDateTime:O} to {after.CreationDateTime:O} across a rejected restore, meaning it was replaced");
        Ensure(
            after.TableArn == before.TableArn,
            $"the colliding table's ARN changed from {Py.Repr(before.TableArn)} to {Py.Repr(after.TableArn)} across a rejected restore");
        var stillThere = await ddb.GetItemAsync(new GetItemRequest { TableName = collidingName, Key = Item(("pk", S("existing"))), ConsistentRead = true });
        Ensure(
            stillThere.Item?.GetValueOrDefault("v")?.S == "must-not-be-overwritten",
            $"the colliding table's pre-existing item did not survive the rejected restore: {Py.Repr(stillThere.Item)}");

        // The would-be restore target key ("k") from the source table must never appear.
        var absent = await ddb.GetItemAsync(new GetItemRequest { TableName = collidingName, Key = Item(("pk", S("k"))), ConsistentRead = true });
        Ensure(absent.Item is null, $"the source table's key leaked into the colliding table despite the restore being rejected: {Py.Repr(absent.Item)}");

        // Recovery leg: remove the one thing that made the call illegitimate, then repeat the
        // IDENTICAL call (same TargetTableName, same BackupArn). This is what proves the rejection
        // above is attributable to the name collision specifically -- a target that rejects every
        // RestoreTableFromBackup call (or one that hardcodes TableAlreadyExistsException) fails here
        // too, where a bare error-code check on the first call alone could not tell the difference.
        await ddb.DeleteTableAsync(collidingName);
        await WaitTableGone(ddb, collidingName);

        // The restore is asserted at acceptance, not polled to ACTIVE: a restore from backup takes
        // minutes even for a near-empty table.
        var restoreResp = await ExpectSuccess(() => ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = collidingName, BackupArn = backupArn }));
        ctx.OnCleanup(() => DeleteTableRetrying(ddb, collidingName));
        Ensure(
            restoreResp.TableDescription.TableName == collidingName,
            $"the recovered restore call reported TableName {Py.Repr(restoreResp.TableDescription.TableName)}, expected {Py.Repr(collidingName)}");
        Ensure(
            restoreResp.TableDescription.RestoreSummary?.SourceBackupArn == backupArn,
            $"the recovered restore call's own RestoreSummary names backup {Py.Repr(restoreResp.TableDescription.RestoreSummary?.SourceBackupArn)}, expected source {Py.Repr(backupArn)}");

        // Terminal assertion: an INDEPENDENT read (DescribeTable), never the mutating restore call's
        // own echo, confirming from a second vantage point that the table now exists at the
        // destination and is genuinely tied to our backup -- not merely a coincidentally-accepted
        // CreateTable. Taken immediately rather than after polling to ACTIVE (see docstring).
        var independent = (await ddb.DescribeTableAsync(collidingName)).Table;
        Ensure(
            independent.TableName == collidingName,
            $"the independent read named table {Py.Repr(independent.TableName)}, expected {Py.Repr(collidingName)}");
        Ensure(
            independent.RestoreSummary?.SourceBackupArn == backupArn,
            $"the independent read's RestoreSummary names backup {Py.Repr(independent.RestoreSummary?.SourceBackupArn)}, expected source {Py.Repr(backupArn)}");
    }

    /// <summary>
    /// Call fn(); return its botocore error code and message. An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ExpectRejection<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<T> ExpectSuccess<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            throw new FidelityAssertionException(
                $"expected this call to succeed now that the conflict is removed, but it was rejected with {Py.Repr(error.Code)}: {Py.Repr(error.Message)}",
                error.Exception);
        }

        return result!;
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

    private static Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value,
            s => s == "ACTIVE",
            timeoutS: timeoutS,
            intervalS: 3);

    private static Task WaitTableGone(IAmazonDynamoDB ddb, string tableName, double timeoutS = 60)
    {
        async Task<bool> Probe()
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(tableName));
            if (error is null)
            {
                return false;
            }

            if (error.Code == "ResourceNotFoundException")
            {
                return true;
            }

            throw error.Exception;
        }

        return PollUntil(Probe, gone => gone, timeoutS: timeoutS, intervalS: 2);
    }

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// A table just restored from backup is left in CREATING and DynamoDB refuses DeleteTable
    /// until it settles -- which routinely outlasts the whole cleanup budget. Retry within a modest
    /// slice of it, then log and give up rather than starving every other cleanup in this check.
    /// </summary>
    private static async Task DeleteTableRetrying(IAmazonDynamoDB ddb, string tableName, int attempts = 5, double intervalS = 5.0)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || attempt == attempts - 1)
            {
                throw new InvalidOperationException($"table {Py.Repr(tableName)} not deleted during cleanup: {error.Code}", error.Exception);
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
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
