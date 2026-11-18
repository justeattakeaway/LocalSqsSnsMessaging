// Translated from behavioral/dynamodb/check_restore_to_point_in_time_invalid_restore_time_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-to-point-in-time-invalid-restore-time-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreToPointInTimeInvalidRestoreTimeRejected
{
    private const string Check = "restore-to-point-in-time-invalid-restore-time-rejected";

    /// <summary>
    /// RestoreTableToPointInTime with a restore time outside the PITR window returns
    /// InvalidRestoreTimeException and leaves no target table, while a restore to the latest
    /// restorable time on the same source succeeds (positive control).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-to-point-in-time-invalid-restore-time-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var start = FidelityTime.StartNew();
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-restoretime-");
        var goodTargetName = FidelityContext.UniqueName("sim2real-restoredok-");
        var badTargetName = FidelityContext.UniqueName("sim2real-restoredtgt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        // A table's continuous backups take a moment to finish being provisioned after creation;
        // an UpdateContinuousBackups issued too soon is transiently rejected with
        // ContinuousBackupsUnavailableException ("Backups are being enabled for this table...").
        // Retry for a bounded time instead of treating that as a hard failure.
        await EnablePitrWithRetry(ddb, tableName);

        // Positive control: a restore to the latest restorable time IS inside the window and
        // must succeed. Without this, the rejection below is indistinguishable from a target
        // that refuses every RestoreTableToPointInTime call regardless of the time given.
        // LatestRestorableDateTime can lag briefly behind PITR turning ENABLED; retry for a
        // bounded time rather than treating that transient state as a hard failure.
        var goodResp = await RestoreWithRetry(ddb, tableName, goodTargetName);
        Ensure(goodResp.TableDescription.TableName == goodTargetName, $"positive-control restore did not create {goodTargetName}");

        // DynamoDB's TagResource on a table still CREATING from a restore 404s for well over 20s, so
        // the restored table stays untagged and the cleanup below deletes it by name.
        ctx.OnCleanup(() => DeleteTableBestEffort(ddb, goodTargetName, start, 270.0));

        // A restore time long before the table existed is outside the restorable
        // window and must be rejected.
        var ancientTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, badTargetName));
        var (code, _) = await Error(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
        {
            SourceTableName = tableName,
            TargetTableName = badTargetName,
            RestoreDateTime = ancientTime,
        }));
        Ensure(code == "InvalidRestoreTimeException", $"expected InvalidRestoreTimeException, got {code}");

        // message varies: it embeds the live EarliestRestorableDateTime and LatestRestorableDateTime,
        // which AWS computes at call time to the second and the check cannot observe atomically.
        // The forbidden effect must not have happened: no target table was created.
        var (notFound, notFoundMessage) = await Error(() => ddb.DescribeTableAsync(badTargetName));
        Ensure(notFound == "ResourceNotFoundException", $"rejected restore must not leave behind a target table, describe_table returned {Py.Repr(notFound)}");
        var expectedNotFound = $"Requested resource not found: Table: {badTargetName} not found";
        Ensure(notFoundMessage == expectedNotFound, $"unexpected message {Py.Repr(notFoundMessage)}");
    }

    // ===== helpers =====
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} did not become ACTIVE within {timeoutS}s");
    }

    private static async Task EnablePitrWithRetry(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = tableName,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            }));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ContinuousBackupsUnavailableException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    private static async Task<RestoreTableToPointInTimeResponse> RestoreWithRetry(IAmazonDynamoDB ddb, string sourceName, string targetName, double timeoutS = 90.0, double intervalS = 3.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = sourceName,
                TargetTableName = targetName,
                UseLatestRestorableTime = true,
            }));
            if (error is null)
            {
                return resp!;
            }

            if (error.Code is not ("InvalidRestoreTimeException" or "PointInTimeRecoveryUnavailableException"))
            {
                throw error.Exception;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
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

    /// <summary>
    /// Cleanup of the positive-control's restored table: it can still be CREATING when the check
    /// body finishes, and waiting all the way to ACTIVE has been observed to outrun even the harness's
    /// 300s hard budget. Retry deletion for a bounded time, then leak loudly rather than hang the check
    /// or fail assertions that already passed on a slow-but-correct target.
    /// </summary>
    private static async Task DeleteTableBestEffort(IAmazonDynamoDB ddb, string tableName, FidelityStopwatch start, double deadlineS)
    {
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || start.Elapsed >= TimeSpan.FromSeconds(deadlineS))
            {
                throw new InvalidOperationException($"table {Py.Repr(tableName)} not deleted: {error.Code}", error.Exception);
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }
    }
}
