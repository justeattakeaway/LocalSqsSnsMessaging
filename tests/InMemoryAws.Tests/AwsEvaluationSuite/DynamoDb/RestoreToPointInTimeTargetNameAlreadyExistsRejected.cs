// Translated from behavioral/dynamodb/check_restore_to_point_in_time_target_name_already_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-to-point-in-time-target-name-already-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreToPointInTimeTargetNameAlreadyExistsRejected
{
    private static CreateTableRequest Table(string name) => new()
    {
        TableName = name,
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        BillingMode = BillingMode.PAY_PER_REQUEST,
    };

    /// <summary>
    /// RestoreTableToPointInTime onto a TargetTableName held by a live table is refused
    /// TableAlreadyExistsException and leaves that table untouched; once the occupying table is
    /// removed, the identical restore succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-to-point-in-time-target-name-already-exists-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var sourceTable = FidelityContext.UniqueName("sim2real-behav-rtpit-src-");
        await ddb.CreateTableAsync(Table(sourceTable));
        ctx.OnCleanup(() => SafeDeleteTable(ddb, sourceTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(sourceTable)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = sourceTable }))
                .ContinuousBackupsDescription.ContinuousBackupsStatus?.Value,
            status => status == "ENABLED",
            timeoutS: 120,
            intervalS: 3);
        await ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = sourceTable,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
        });
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = sourceTable }))
                .ContinuousBackupsDescription.PointInTimeRecoveryDescription?.PointInTimeRecoveryStatus?.Value,
            status => status == "ENABLED",
            timeoutS: 120,
            intervalS: 3);

        // A second, already-existing, live table occupies the name we will (illegitimately) ask
        // RestoreTableToPointInTime to target.
        var occupiedTable = FidelityContext.UniqueName("sim2real-behav-rtpit-occupied-");
        await ddb.CreateTableAsync(Table(occupiedTable));
        ctx.OnCleanup(() => SafeDeleteTable(ddb, occupiedTable));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(occupiedTable)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutS: 120,
            intervalS: 3);

        // Immediately after PITR turns on, a restorable time may take a few seconds to become
        // available (PointInTimeRecoveryUnavailableException) -- ride that out, but the collision
        // we are asserting is TableAlreadyExistsException.
        var (code, message) = await RetryRestorePastUnavailable(ddb, sourceTable, occupiedTable, timeoutS: 60, intervalS: 3);
        Ensure(code == "TableAlreadyExistsException", $"expected TableAlreadyExistsException, got {Py.Repr(code)}; message was {Py.Repr(message)}");
        var expected = $"Table already exists: {occupiedTable}";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        // The occupied table must be completely unaffected by the rejected restore attempt.
        var stillActive = (await ddb.DescribeTableAsync(occupiedTable)).Table.TableStatus?.Value;
        Ensure(stillActive == "ACTIVE", $"expected the occupied table to remain ACTIVE after the rejected restore, got {Py.Repr(stillActive)}");

        // Remove the obstruction and demand the identical call now succeeds. A target that rejects
        // RestoreTableToPointInTime unconditionally with this code -- rather than only when the name
        // collides -- passes every assertion above and must fail here.
        await ddb.DeleteTableAsync(occupiedTable);
        await PollUntil(() => TableStatusOrGone(ddb, occupiedTable), status => status is null, timeoutS: 120, intervalS: 3);
        var tableDesc = await RetryRestoreUntilAccepted(ddb, sourceTable, occupiedTable, timeoutS: 60, intervalS: 3);
        Ensure(tableDesc.TableStatus == TableStatus.CREATING || tableDesc.TableStatus == TableStatus.ACTIVE, $"{tableDesc}");
        Ensure(
            tableDesc.RestoreSummary?.SourceTableArn?.EndsWith($"table/{sourceTable}", StringComparison.Ordinal) == true,
            Py.Repr(tableDesc.RestoreSummary?.SourceTableArn));
    }

    // ===== helpers =====

    /// <summary>
    /// Retry RestoreTableToPointInTime past the transient PointInTimeRecoveryUnavailableException
    /// window; return the (code, message) of the first *other* rejection.
    /// </summary>
    private static async Task<(string? Code, string? Message)> RetryRestorePastUnavailable(
        IAmazonDynamoDB ddb, string sourceTable, string targetTable, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (result, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = sourceTable,
                TargetTableName = targetTable,
                UseLatestRestorableTime = true,
            }));
            if (error is not null)
            {
                if (error.Code != "PointInTimeRecoveryUnavailableException" || clock.Elapsed.TotalSeconds >= timeoutS)
                {
                    return (error.Code, error.Message);
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
                continue;
            }

            throw Fail($"expected a rejection, but the call succeeded and returned {result!.TableDescription}");
        }
    }

    /// <summary>
    /// DynamoDB may briefly still consider a just-deleted name reserved; retry the restore call
    /// itself (not just the readiness poll) until it is accepted.
    /// </summary>
    private static async Task<TableDescription> RetryRestoreUntilAccepted(
        IAmazonDynamoDB ddb, string sourceTable, string targetTable, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (result, error) = await FidelityContext.Capture(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = sourceTable,
                TargetTableName = targetTable,
                UseLatestRestorableTime = true,
            }));
            if (error is null)
            {
                return result!.TableDescription;
            }

            if (error.Code is not ("TableAlreadyExistsException" or "PointInTimeRecoveryUnavailableException"))
            {
                throw error.Exception;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"the identical restore was still rejected as {Py.Repr(error.Code)} {timeoutS}s after the occupying table was deleted");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    private static async Task<string?> TableStatusOrGone(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            return (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        // A table restored via RestoreTableToPointInTime can stay CREATING, and refuse DeleteTable
        // with ResourceInUseException, for several minutes.
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
            catch (ResourceInUseException) when (clock.Elapsed.TotalSeconds < 900)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
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

        throw Fail($"condition not met within {timeoutS}s, last value: {Py.Repr(last)}");
    }
}
