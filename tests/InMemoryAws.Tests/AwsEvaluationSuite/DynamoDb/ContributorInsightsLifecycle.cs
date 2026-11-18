// Translated from behavioral/dynamodb/check_contributor_insights_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.contributor-insights-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContributorInsightsLifecycle
{
    private const string CheckId = "contributor-insights-lifecycle";

    /// <summary>
    /// UpdateContributorInsights(ENABLE) moves a DISABLED table to ENABLED in
    /// DescribeContributorInsights and ListContributorInsights; DISABLE moves it back to DISABLED.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.contributor-insights-lifecycle")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var baseline = await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = t });
        Ensure(
            baseline.ContributorInsightsStatus == "DISABLED",
            $"expected ContributorInsightsStatus=DISABLED before ENABLE, got {Py.Repr(Boto(baseline))}");

        var enableResp = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = t,
            ContributorInsightsAction = ContributorInsightsAction.ENABLE,
        });
        Ensure(
            enableResp.ContributorInsightsStatus == "ENABLING" || enableResp.ContributorInsightsStatus == "ENABLED",
            $"expected ContributorInsightsStatus in (ENABLING, ENABLED) from UpdateContributorInsights(ENABLE), got {Py.Repr(Boto(enableResp))}");

        var enabled = await PollUntil(
            () => ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = t }),
            d => d.ContributorInsightsStatus == "ENABLED",
            Boto,
            timeoutSeconds: 60,
            intervalSeconds: 2.0,
            description: "contributor insights ENABLED");
        Ensure(
            enabled.ContributorInsightsStatus == "ENABLED",
            $"expected ContributorInsightsStatus=ENABLED from DescribeContributorInsights, got {Py.Repr(Boto(enabled))}");

        var listed = await PollUntil(
            async () => (await ddb.ListContributorInsightsAsync(new ListContributorInsightsRequest { TableName = t })).ContributorInsightsSummaries,
            summaries => summaries.Any(s => s.TableName == t),
            Boto,
            timeoutSeconds: 30,
            intervalSeconds: 2.0,
            description: "table visible in ListContributorInsights");
        Ensure(
            listed.Any(s => s.TableName == t),
            $"expected table {t} present in ListContributorInsights summaries, got {Py.Repr(Boto(listed))}");

        var disableResp = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = t,
            ContributorInsightsAction = ContributorInsightsAction.DISABLE,
        });
        Ensure(
            disableResp.ContributorInsightsStatus == "DISABLING" || disableResp.ContributorInsightsStatus == "DISABLED",
            $"expected ContributorInsightsStatus in (DISABLING, DISABLED) from UpdateContributorInsights(DISABLE), got {Py.Repr(Boto(disableResp))}");

        var disabled = await PollUntil(
            () => ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = t }),
            d => d.ContributorInsightsStatus == "DISABLED",
            Boto,
            timeoutSeconds: 60,
            intervalSeconds: 2.0,
            description: "contributor insights DISABLED");
        Ensure(
            disabled.ContributorInsightsStatus == "DISABLED",
            $"expected ContributorInsightsStatus=DISABLED from DescribeContributorInsights after DISABLE, got {Py.Repr(Boto(disabled))}");
    }

    private static async Task<T> PollUntil<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        Func<T, object?> repr,
        double timeoutSeconds = 60,
        double intervalSeconds = 1.0,
        string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last is null ? null : repr(last))}");
    }

    private static Dictionary<string, object?> Boto(DescribeContributorInsightsResponse r) => new(StringComparer.Ordinal)
    {
        ["TableName"] = r.TableName,
        ["ContributorInsightsStatus"] = r.ContributorInsightsStatus?.Value,
    };

    private static Dictionary<string, object?> Boto(UpdateContributorInsightsResponse r) => new(StringComparer.Ordinal)
    {
        ["TableName"] = r.TableName,
        ["ContributorInsightsStatus"] = r.ContributorInsightsStatus?.Value,
    };

    private static List<Dictionary<string, object?>> Boto(List<ContributorInsightsSummary>? summaries) =>
        [.. (summaries ?? []).Select(s => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TableName"] = s.TableName,
            ["ContributorInsightsStatus"] = s.ContributorInsightsStatus?.Value,
        })];
}
