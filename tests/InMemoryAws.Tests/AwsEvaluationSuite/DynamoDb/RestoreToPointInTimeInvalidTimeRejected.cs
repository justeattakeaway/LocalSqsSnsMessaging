// Translated from behavioral/dynamodb/check_restore_to_point_in_time_invalid_time_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-to-point-in-time-invalid-time-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreToPointInTimeInvalidTimeRejected
{
    private const string Check = "restore-to-point-in-time-invalid-time-rejected";

    /// <summary>
    /// RestoreTableToPointInTime with a RestoreDateTime a day before the table's creation raises
    /// InvalidRestoreTimeException and creates no target table, while a valid restore of the same source table is
    /// accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-to-point-in-time-invalid-time-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var sourceName = FidelityContext.UniqueName("sim2real-behav-src-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(sourceName));
        var created = await WaitTableStatus(ctx, sourceName, "ACTIVE");
        var creationTime = created.CreationDateTime!.Value;
        await EnablePitrWithRetry(ddb, sourceName);
        await WaitPitrStatus(ctx, sourceName, "ENABLED");

        // Positive control: a valid restore against this same source table must succeed. Without
        // this, a target that rejects EVERY RestoreTableToPointInTime call with
        // InvalidRestoreTimeException (valid input or not) would pass the negative assertion below
        // just as well as a target that correctly discriminates on the restore time.
        var controlName = FidelityContext.UniqueName("sim2real-behav-restored-ctrl-");
        var controlDesc = await RestoreWithRetry(ddb, sourceName, controlName);
        Ensure(controlDesc.TableName == controlName, $"positive control: valid RestoreTableToPointInTime did not accept, got {Py.Repr(controlDesc.TableName)}");
        ctx.OnCleanup(() => TryDeleteTable(ddb, controlName));
        var targetName = FidelityContext.UniqueName("sim2real-behav-restored-");
        var tooEarly = creationTime.AddDays(-1);
        var (code, _) = await Error(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
        {
            SourceTableName = sourceName,
            TargetTableName = targetName,
            RestoreDateTime = tooEarly,
        }));
        Ensure(code == "InvalidRestoreTimeException", $"expected InvalidRestoreTimeException, got {code}");

        // message varies: it embeds the live EarliestRestorableDateTime and LatestRestorableDateTime,
        // which AWS computes at call time to the second and the check cannot observe atomically.
        // The forbidden effect (target table creation) must not have happened.
        var tableNames = (await ddb.ListTablesAsync(new ListTablesRequest())).TableNames ?? [];
        Ensure(!tableNames.Contains(targetName), "target table should not exist after rejected restore");
        var (notFound, notFoundMessage) = await Error(() => ddb.DescribeTableAsync(targetName));
        Ensure(notFound == "ResourceNotFoundException", $"target table should not exist after rejected restore, describe_table gave {Py.Repr(notFound)}");
        var expectedNotFound = $"Requested resource not found: Table: {targetName} not found";
        Ensure(notFoundMessage == expectedNotFound, $"unexpected message {Py.Repr(notFoundMessage)}");
    }

    /// <summary>Return (code, message) of the ClientError <paramref name="fn"/> raises; an accepted call fails the check.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A just-created table has continuous backups enabling in the background; an
    /// UpdateContinuousBackups issued too soon is rejected until that settles — retry for a
    /// bounded time rather than treating it as a hard failure.
    /// </summary>
    private static async Task EnablePitrWithRetry(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120, double intervalS = 3)
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

    /// <summary>
    /// UseLatestRestorableTime can trail table/PITR creation by a few seconds; retry the
    /// transient not-yet-available codes for a bounded time rather than treating them as a hard
    /// failure.
    /// </summary>
    private static async Task<TableDescription> RestoreWithRetry(IAmazonDynamoDB ddb, string sourceName, string targetName, double timeoutS = 90, double intervalS = 3)
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
                return resp!.TableDescription;
            }

            if (error.Code is not ("PointInTimeRecoveryUnavailableException" or "InvalidRestoreTimeException"))
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

    private static async Task TryDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "ResourceInUseException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<TableDescription> WaitTableStatus(FidelityContext ctx, string tableName, string targetStatus, double timeoutS = 180, double intervalS = 3)
    {
        var ddb = ctx.Dynamo;
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            last = desc.TableStatus.Value;
            if (last == targetStatus)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never reached {targetStatus}, last={last ?? "None"}");
    }

    private static async Task<ContinuousBackupsDescription> WaitPitrStatus(FidelityContext ctx, string tableName, string targetStatus, double timeoutS = 120, double intervalS = 3)
    {
        var ddb = ctx.Dynamo;
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var desc = (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription;
            last = desc.PointInTimeRecoveryDescription.PointInTimeRecoveryStatus.Value;
            if (last == targetStatus)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"PITR on {tableName} never reached {targetStatus}, last={last ?? "None"}");
    }
}
