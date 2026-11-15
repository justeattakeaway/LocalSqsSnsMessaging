// Translated from behavioral/dynamodb/check_describe_limits_returns_account_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-limits-returns-account-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeLimitsReturnsAccountCapacity
{
    /// <summary>
    /// DescribeLimits is a server-enforced, account-scoped read: it returns
    /// positive account-level throughput ceilings, and the per-table ceilings
    /// never exceed the account-level ceilings, on every call.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-limits-returns-account-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var limits = await ddb.DescribeLimitsAsync(new DescribeLimitsRequest());
        var accountRead = limits.AccountMaxReadCapacityUnits;
        var accountWrite = limits.AccountMaxWriteCapacityUnits;
        var tableRead = limits.TableMaxReadCapacityUnits;
        var tableWrite = limits.TableMaxWriteCapacityUnits;

        foreach (var value in (long?[])[accountRead, accountWrite, tableRead, tableWrite])
        {
            Ensure(value is > 0, $"expected a positive integer limit, got {Py.Repr(value)}");
        }

        Ensure(tableRead <= accountRead, "a single table's read limit exceeded the account-wide read limit");
        Ensure(tableWrite <= accountWrite, "a single table's write limit exceeded the account-wide write limit");

        // Server-enforced account limits are stable across back-to-back calls.
        var limitsAgain = await ddb.DescribeLimitsAsync(new DescribeLimitsRequest());
        Ensure(
            limitsAgain.AccountMaxReadCapacityUnits == accountRead,
            $"AccountMaxReadCapacityUnits changed between calls: {Py.Repr(accountRead)} -> {Py.Repr(limitsAgain.AccountMaxReadCapacityUnits)}");
        Ensure(
            limitsAgain.AccountMaxWriteCapacityUnits == accountWrite,
            $"AccountMaxWriteCapacityUnits changed between calls: {Py.Repr(accountWrite)} -> {Py.Repr(limitsAgain.AccountMaxWriteCapacityUnits)}");
        Ensure(
            limitsAgain.TableMaxReadCapacityUnits == tableRead,
            $"TableMaxReadCapacityUnits changed between calls: {Py.Repr(tableRead)} -> {Py.Repr(limitsAgain.TableMaxReadCapacityUnits)}");
        Ensure(
            limitsAgain.TableMaxWriteCapacityUnits == tableWrite,
            $"TableMaxWriteCapacityUnits changed between calls: {Py.Repr(tableWrite)} -> {Py.Repr(limitsAgain.TableMaxWriteCapacityUnits)}");
    }
}
