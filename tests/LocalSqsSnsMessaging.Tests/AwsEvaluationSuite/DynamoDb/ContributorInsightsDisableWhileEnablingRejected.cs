// Translated from behavioral/dynamodb/check_contributor_insights_disable_while_enabling_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.contributor-insights-disable-while-enabling-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContributorInsightsDisableWhileEnablingRejected
{
    private const string CheckId = "contributor-insights-disable-while-enabling-rejected";

    /// <summary>The code <see cref="Outcome"/> reports for a call the target accepted.</summary>
    private const string Accepted = "<accepted>";

    /// <summary>ENABLE requests tried before the check gives up on observing the ENABLING window.</summary>
    private const int Attempts = 5;

    /// <summary>Bound on each wait for contributor insights to settle; the runner's remaining time bounds it further.</summary>
    private const double SettleTimeoutSeconds = 600.0;

    private const string Expected =
        "Table or Index is not in a valid state to update Key Access Insights: ContributorInsights "
        + "Status must be ENABLED or FAILED to perform DISABLE operation.";

    /// <summary>
    /// DISABLE sent while contributor insights are still ENABLING is refused with ValidationException
    /// and its exact message, and the refused request leaves the insights to settle ENABLED.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.contributor-insights-disable-while-enabling-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddb-cins-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
        });
        ctx.OnCleanup(() => Drop(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);

        var tried = new List<string>();
        var refused = false;
        for (var i = 0; i < Attempts; i++)
        {
            var enabled = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
            {
                TableName = table,
                ContributorInsightsAction = ContributorInsightsAction.ENABLE,
            });
            var before = await Status(ddb, table);
            if (before != "ENABLING")
            {
                tried.Add($"ENABLE answered {Py.Repr(enabled.ContributorInsightsStatus?.Value)}, then read {Py.Repr(before)}");
                await Settle(ctx, ddb, table, "ENABLED");
                await DisableAndSettle(ctx, ddb, table);
                continue;
            }

            var (code, message) = await Outcome(() => ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
            {
                TableName = table,
                ContributorInsightsAction = ContributorInsightsAction.DISABLE,
            }));
            if ((code, message) == ("ValidationException", Expected))
            {
                refused = true;
                break;
            }

            Ensure(
                code == Accepted,
                $"DISABLE while ENABLING answered {Py.Repr(code)} {Py.Repr(message)}; expected ValidationException {Py.Repr(Expected)}");
            var after = await Settle(ctx, ddb, table, "DISABLED");
            Ensure(after == "DISABLED", $"the accepted DISABLE left the insights {Py.Repr(after)}, expected 'DISABLED'");
            tried.Add($"DISABLE accepted after the status read {Py.Repr(before)}; settled {Py.Repr(after)}");
        }

        Ensure(refused, $"DISABLE was never refused while ENABLING in {Attempts} attempts: {Py.Repr(tried)}");

        var final = await Settle(ctx, ddb, table, "ENABLED");
        Ensure(final == "ENABLED", $"after the refused DISABLE the insights settled {Py.Repr(final)}, expected 'ENABLED'");
    }

    /// <summary>
    /// (code, message) of the refusal <paramref name="fn"/> raises, or (<see cref="Accepted"/>, repr of the answer) when the
    /// target accepts the call.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Outcome(Func<Task<UpdateContributorInsightsResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        return (Accepted, Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TableName"] = result!.TableName,
            ["ContributorInsightsStatus"] = result.ContributorInsightsStatus?.Value,
        }));
    }

    private static async Task<string?> Status(IAmazonDynamoDB ddb, string table) =>
        (await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = table })).ContributorInsightsStatus?.Value;

    /// <summary>Poll until the insights reach <paramref name="target"/> or FAILED; returns the status reached.</summary>
    private static async Task<string?> Settle(FidelityContext ctx, IAmazonDynamoDB ddb, string table, string target)
    {
        var left = ctx.TimeLeftSeconds;
        var timeout = Math.Max(1.0, Math.Min(SettleTimeoutSeconds, left - Math.Min(60.0, left / 4)));
        var clock = FidelityTime.StartNew();
        var status = await Status(ddb, table);
        while (status != target && status != "FAILED")
        {
            if (clock.Elapsed.TotalSeconds >= timeout)
            {
                throw Fail($"contributor insights still {Py.Repr(status)} after {timeout.ToString("F0", CultureInfo.InvariantCulture)}s, waiting for {Py.Repr(target)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            status = await Status(ddb, table);
        }

        return status;
    }

    private static async Task DisableAndSettle(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = table,
            ContributorInsightsAction = ContributorInsightsAction.DISABLE,
        });
        await Settle(ctx, ddb, table, "DISABLED");
    }

    /// <summary>Delete the table; already gone counts as deleted.</summary>
    private static async Task Drop(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
