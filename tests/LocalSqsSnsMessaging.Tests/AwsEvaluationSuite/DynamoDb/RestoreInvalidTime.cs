// Translated from behavioral/dynamodb/check_restore_invalid_time.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-invalid-time</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreInvalidTime
{
    private const string ErrInvalidRestoreTime = "InvalidRestoreTimeException";

    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>Delete a table, waiting out a restore still writing it (ResourceInUseException).</summary>
    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string name, double timeoutS = 1800.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed > TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(15.0));
        }
    }

    private static async Task<string?> DescribeStatusOrNone(IAmazonDynamoDB ddb, string name)
    {
        var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
        if (error is null)
        {
            return resp!.Table.TableStatus.Value;
        }

        if (error.Code == "ResourceNotFoundException")
        {
            return null;
        }

        throw error.Exception;
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 120, double intervalS = 2.0, string description = "condition")
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

    private static async Task EnablePitr(IAmazonDynamoDB ddb, string name)
    {
        async Task<bool> Try()
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
            {
                TableName = name,
                PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            }));
            if (error is null)
            {
                return true;
            }

            if (error.Code != "ContinuousBackupsUnavailableException")
            {
                throw error.Exception;
            }

            return false;
        }

        await PollUntil(Try, ok => ok, timeoutS: 120, intervalS: 3.0, description: "PITR enable available");
        await PollUntil(
            async () => (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = name }))
                .ContinuousBackupsDescription.PointInTimeRecoveryDescription?.PointInTimeRecoveryStatus?.Value,
            s => s == "ENABLED",
            timeoutS: 120,
            intervalS: 2.0,
            description: "PITR ENABLED");
    }

    /// <summary>
    /// RestoreTableToPointInTime with a RestoreDateTime in the future raises InvalidRestoreTimeException whose
    /// message bounds the restorable window for the table, and creates no table, while the same call with
    /// UseLatestRestorableTime is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-invalid-time")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await EnablePitr(ddb, t);

        // Positive control: the same call shape with a valid restore time must be accepted, and an
        // independent read must show the target actually exists.
        var positiveTarget = FidelityContext.UniqueName("sim2real-behav-restore-ok-");
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, positiveTarget));

        async Task<bool> AttemptPositiveRestore()
        {
            var error = await FidelityContext.CaptureError(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
            {
                SourceTableName = t,
                TargetTableName = positiveTarget,
                UseLatestRestorableTime = true,
            }));
            if (error is null)
            {
                return true;
            }

            if (error.Code is "InvalidRestoreTimeException" or "PointInTimeRecoveryUnavailableException")
            {
                return false;
            }

            throw error.Exception;
        }

        await PollUntil(AttemptPositiveRestore, ok => ok, timeoutS: 90, intervalS: 3.0, description: "positive control: restore to latest restorable time accepted");
        var positiveStatus = await DescribeStatusOrNone(ddb, positiveTarget);
        Ensure(
            positiveStatus is not null,
            $"positive control: {Py.Repr(positiveTarget)} was accepted by RestoreTableToPointInTime but an independent describe_table cannot find it");
        var target = FidelityContext.UniqueName("sim2real-behav-restore-target-");
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, target));
        var future = FidelityTime.UtcNow.AddDays(1).UtcDateTime;
        var (code, message) = await ErrorCode(() => ddb.RestoreTableToPointInTimeAsync(new RestoreTableToPointInTimeRequest
        {
            SourceTableName = t,
            TargetTableName = target,
            RestoreDateTime = future,
        }));
        Ensure(code == ErrInvalidRestoreTime, $"expected {ErrInvalidRestoreTime}, got {code}");

        // The quoted instants (requested, earliest and latest restorable) are rendered and chosen by AWS.
        Ensure(
            Regex.IsMatch(message!, $@"\ARestoreDateTime '[^']+' must be within \w+ '[^']+' and \w+ '[^']+' for table '{Regex.Escape(t)}'\z"),
            message!);
        var notFoundStatus = await DescribeStatusOrNone(ddb, target);
        Ensure(
            notFoundStatus is null,
            $"a rejected RestoreTableToPointInTime must not leave a target table behind, but describe_table found {Py.Repr(target)} in status {Py.Repr(notFoundStatus)}");
    }
}
