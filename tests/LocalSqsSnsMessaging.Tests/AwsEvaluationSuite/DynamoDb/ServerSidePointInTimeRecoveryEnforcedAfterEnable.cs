// Translated from behavioral/dynamodb/check_server_side_point_in_time_recovery_enforced_after_enable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.server-side-point-in-time-recovery-enforced-after-enable</summary>
[Category("AwsEvaluationSuite")]
public sealed class ServerSidePointInTimeRecoveryEnforcedAfterEnable
{
    private const string CheckId = "server-side-point-in-time-recovery-enforced-after-enable";

    /// <summary>
    /// PITR is DISABLED by default; once enabled, DescribeContinuousBackups shows a recovery window
    /// (Earliest &lt;= LatestRestorableDateTime). After DeleteTable, Describe and UpdateContinuousBackups
    /// both report the table not found.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.server-side-point-in-time-recovery-enforced-after-enable")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-pitr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });

        async Task Cleanup()
        {
            try
            {
                await ddb.DeleteTableAsync(tableName);
            }
            catch (ResourceNotFoundException)
            {
            }
        }

        ctx.OnCleanup(Cleanup);
        await WaitTableActive(ddb, tableName);

        async Task<ContinuousBackupsDescription> ContinuousBackupsDesc() =>
            (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription;

        // Continuous backups are enabled at creation but can take a moment to report ENABLED.
        var baseline = await PollUntil(
            ContinuousBackupsDesc,
            d => d.ContinuousBackupsStatus?.Value == "ENABLED",
            timeoutS: 60.0,
            intervalS: 2.0);
        Ensure(
            baseline.ContinuousBackupsStatus?.Value == "ENABLED",
            $"expected ContinuousBackupsStatus=ENABLED right after CreateTable, got {Py.Repr(baseline.ContinuousBackupsStatus?.Value)}");
        var baselinePitr = baseline.PointInTimeRecoveryDescription?.PointInTimeRecoveryStatus?.Value;
        Ensure(
            baselinePitr == "DISABLED",
            $"expected PointInTimeRecoveryStatus=DISABLED by default on a new table, got {Py.Repr(baselinePitr)}");

        await ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = tableName,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
        });

        async Task<PointInTimeRecoveryDescription> PitrDesc() =>
            (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName }))
                .ContinuousBackupsDescription.PointInTimeRecoveryDescription;

        var final = await PollUntil(
            PitrDesc,
            d => d.PointInTimeRecoveryStatus?.Value == "ENABLED",
            timeoutS: 60.0,
            intervalS: 2.0);
        Ensure(
            final.PointInTimeRecoveryStatus?.Value == "ENABLED",
            $"expected PointInTimeRecoveryStatus=ENABLED after UpdateContinuousBackups, got {Py.Repr(final.PointInTimeRecoveryStatus?.Value)}");
        var earliest = final.EarliestRestorableDateTime;
        var latest = final.LatestRestorableDateTime;
        Ensure(earliest is not null, "expected EarliestRestorableDateTime to be populated once PITR is enabled, got None");
        Ensure(latest is not null, "expected LatestRestorableDateTime to be populated once PITR is enabled, got None");
        Ensure(earliest <= latest, $"expected EarliestRestorableDateTime <= LatestRestorableDateTime, got {earliest:O} > {latest:O}");

        await ddb.DeleteTableAsync(tableName);

        async Task<string?> DescribeAfterDelete()
        {
            try
            {
                await ddb.DescribeTableAsync(tableName);
                return null;
            }
            catch (ResourceNotFoundException)
            {
                return "ResourceNotFoundException";
            }
        }

        var gone = await PollUntil(DescribeAfterDelete, code => code == "ResourceNotFoundException", timeoutS: 60.0, intervalS: 2.0);
        Ensure(
            gone == "ResourceNotFoundException",
            $"expected DescribeTable to report ResourceNotFoundException after DeleteTable, got {Py.Repr(gone)}");

        try
        {
            await ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = tableName,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = false },
            });
        }
        catch (TableNotFoundException exc)
        {
            var message = exc.Message;
            Ensure(message == $"Table not found: {tableName}", message);
            return;
        }

        throw Fail("expected UpdateContinuousBackups against a deleted table to raise TableNotFoundException");
    }

    private static Task WaitTableActive(IAmazonDynamoDB client, string tableName, double timeoutS = 120.0, double intervalS = 2.0) =>
        PollUntil(
            async () => (await client.DescribeTableAsync(tableName)).Table.TableStatus?.Value,
            s => s == "ACTIVE",
            timeoutS,
            intervalS);

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {Py.Repr(timeoutS)}s; last observed value: {Py.Repr(last)}");
    }
}
