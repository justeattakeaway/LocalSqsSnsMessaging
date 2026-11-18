// Translated from behavioral/dynamodb/check_restore_table_to_point_in_time_creates_active_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-table-to-point-in-time-creates-active-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreTableToPointInTimeCreatesActiveTable
{
    private const string Check = "restore-table-to-point-in-time-creates-active-table";

    /// <summary>
    /// RestoreTableToPointInTime produces a new table that transitions to ACTIVE and carries
    /// over the data present on the source table at restore time.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-table-to-point-in-time-creates-active-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);

        // The harness enforces ONE hard wall-clock budget for the WHOLE check regardless of
        // any individual poll's own timeout_s — a poll timeout bigger than what's left of that budget
        // can never actually be honored; it only guarantees the process gets killed mid-poll with an
        // unhelpful "Check timed out" instead of a clean assertion. Track elapsed time from the start
        // and hand the dominant, unavoidably-async stage (the restore reaching ACTIVE) whatever of the
        // real budget remains, instead of a static value that outruns the cap.
        // `total` is the check's wall-clock budget less a 100 s margin for the steps after the
        // wait.
        var start = FidelityTime.StartNew();
        double RemainingBudget(double total = 1700.0, double floor = 30.0) => Math.Max(floor, total - start.Elapsed.TotalSeconds);

        var ddb = ctx.Dynamo;
        var sourceTable = FidelityContext.UniqueName("sim2real-behav-rpitr-src-");
        List<KeySchemaElement> keySchema = [new("pk", KeyType.HASH)];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = sourceTable,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = keySchema,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, sourceTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(sourceTable)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 60,
            intervalS: 2);
        await ddb.PutItemAsync(sourceTable, Item(("pk", S("seed-item")), ("val", S("world"))));
        var targetTable = FidelityContext.UniqueName("sim2real-behav-rpitr-dst-");

        // The harness enforces one hard wall-clock budget for the WHOLE check regardless of any
        // individual poll's own timeout_s, and RestoreTableToPointInTime's own completion is the
        // dominant cost against that budget. Enable PITR as the FIRST action once the source table is
        // ACTIVE (rather than folding it into the restore-attempt retry loop below) so its "latest
        // restorable time" gets the maximum head start to become usable while the item write above
        // runs, instead of only starting to settle deep into the check's wall-clock budget.
        await EnablePitrWithRetry(ddb, sourceTable);

        async Task<RestoreTableToPointInTimeResponse?> AttemptRestore()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = sourceTable,
                TargetTableName = targetTable,
                UseLatestRestorableTime = true,
            }));
            if (error is not null)
            {
                if (error.Code is "InvalidRestoreTimeException" or "PointInTimeRecoveryUnavailableException")
                {
                    return null;
                }

                throw error.Exception;
            }

            return resp;
        }

        var restoreResp = (await PollUntil(AttemptRestore, resp => resp is not null, timeoutS: 90, intervalS: 2))!;
        Ensure(restoreResp.TableDescription.TableName == targetTable, "restore_resp[\"TableDescription\"][\"TableName\"] == target_table");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, targetTable));

        // Restoring a table to a point in time (even a tiny table) commonly takes DynamoDB a couple
        // of minutes to reach ACTIVE on real AWS — give this dominant, unavoidably-async stage
        // whatever is left of the check's wall-clock budget (a static timeout_s bigger than that
        // remaining budget can never be honored, so the poll uses the real remaining time rather than
        // a number that outruns it). RestoreSummary is carried by RestoreTableToPointInTime's own TableDescription;
        // DescribeTable on the ACTIVE table was observed on real AWS to omit it.
        Ensure(restoreResp.TableDescription.RestoreSummary is not null, "\"RestoreSummary\" in restore_resp[\"TableDescription\"]");
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
        Ensure(restoredItem is not null, "restored table is missing the item written before restore");
        Ensure(restoredItem["val"].S == "world", "restored_item[\"val\"][\"S\"] == \"world\"");
    }

    /// <summary>
    /// A just-created table has continuous backups enabling in the background; an
    /// UpdateContinuousBackups issued too soon is rejected until that settles — retry for a
    /// bounded time rather than treating that as a hard failure.
    /// </summary>
    private static async Task EnablePitrWithRetry(IAmazonDynamoDB ddb, string tableName, double timeoutS = 60, double intervalS = 2)
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

            if (error.Code == "ContinuousBackupsUnavailableException" && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            throw error.Exception;
        }
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
