// Translated from behavioral/dynamodb/check_update_continuous_backups_nonexistent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-continuous-backups-nonexistent</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateContinuousBackupsNonexistent
{
    /// <summary>
    /// UpdateContinuousBackups on a table that does not exist MUST fail with
    /// TableNotFoundException (overly-permissive boundary).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-continuous-backups-nonexistent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-no-such-table-");
        var (result, err) = await FidelityContext.Capture(() => ddb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = name,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
        }));
        if (err is null)
        {
            throw Fail($"expected TableNotFoundException, but the call succeeded: {result}");
        }

        Ensure(err.Code == "TableNotFoundException", $"expected TableNotFoundException, got {err.Code}");
        var expected = $"Table not found: {name}";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }
}
