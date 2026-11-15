// Translated from behavioral/dynamodb/check_restore_to_point_in_time_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-to-point-in-time-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreToPointInTimeLifecycle
{
    private const string Check = "restore-to-point-in-time-lifecycle";

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

    /// <summary>
    /// A table restored via RestoreTableToPointInTime can stay CREATING for minutes, and AWS
    /// refuses DeleteTable on a CREATING table with ResourceInUseException, so that code is retried
    /// up to timeout_s; any other error, or the deadline, raises.
    /// </summary>
    private static async Task DeleteTableWithRetry(IAmazonDynamoDB ddb, string name, double timeoutS = 600, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    private static Task<ContinuousBackupsDescription> WaitContinuousBackupsReady(IAmazonDynamoDB ddb, string name, double timeoutS = 90) =>
        PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = name })).ContinuousBackupsDescription,
            d => d.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED,
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"continuous backups ready for {name}");

    /// <summary>
    /// RestoreTableToPointInTime produces a new table whose state is visible
    /// via DescribeTable. Exercises RestoreTableToPointInTime, DescribeTable.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-to-point-in-time-lifecycle")]
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
        await ddb.PutItemAsync(t, Item(("pk", S("seed")), ("v", S("1"))));
        await WaitContinuousBackupsReady(ddb, t);
        await ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = t,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
        });
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = t }))
                .ContinuousBackupsDescription.PointInTimeRecoveryDescription.PointInTimeRecoveryStatus.Value,
            s => s == "ENABLED",
            timeoutS: 60,
            intervalS: 2.0,
            description: "PITR enabled");
        var targetName = FidelityContext.UniqueName("sim2real-behav-pitr-restored-");
        ctx.OnCleanup(() => DeleteTableWithRetry(ddb, targetName));

        // Immediately after PITR turns on, a restorable time may take a few
        // seconds to become available -- retry via _poll_until.
        var lastErr = new List<AwsError>();

        async Task<TableDescription?> TryRestore()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = t,
                TargetTableName = targetName,
                UseLatestRestorableTime = true,
            }));
            if (error is not null)
            {
                lastErr.Add(error);
                return null;
            }

            return resp!.TableDescription;
        }

        var tableDesc = await PollUntil(
            TryRestore,
            d => d is not null,
            timeoutS: 30,
            intervalS: 3.0,
            description: "restore_table_to_point_in_time succeeds once a restorable time exists");
        Ensure(tableDesc is not null, $"restore_table_to_point_in_time never succeeded: {(lastErr.Count > 0 ? lastErr[^1].ToString() : "None")}");
        Ensure(tableDesc.TableStatus.Value is "CREATING" or "ACTIVE", "table_desc['TableStatus'] in ('CREATING', 'ACTIVE')");
        Ensure(
            tableDesc.RestoreSummary.SourceTableArn.EndsWith($"table/{t}", StringComparison.Ordinal),
            "table_desc['RestoreSummary']['SourceTableArn'].endswith(f'table/{t}')");

        // Restoring to ACTIVE can take several minutes even for a tiny table, so
        // this check asserts only that the restored table is visible and CREATING
        // or ACTIVE via DescribeTable, not that it reaches ACTIVE.
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(targetName)).Table,
            tbl => tbl.TableStatus.Value is "CREATING" or "ACTIVE",
            timeoutS: 60,
            intervalS: 3.0,
            description: "restored table visible via DescribeTable");
        Ensure(described.TableName == targetName, "described['TableName'] == target_name");
        Ensure(
            described.RestoreSummary.SourceTableArn.EndsWith($"table/{t}", StringComparison.Ordinal),
            "described['RestoreSummary']['SourceTableArn'].endswith(f'table/{t}')");
    }
}
