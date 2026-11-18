// Translated from behavioral/dynamodb/check_contributor_insights_enable_describe_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.contributor-insights-enable-describe-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContributorInsightsEnableDescribeList
{
    private const string CheckId = "contributor-insights-enable-describe-list";

    /// <summary>
    /// UpdateContributorInsights enabling status is reflected in DescribeContributorInsights
    /// and ListContributorInsights, and disabling it is reflected the same way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.contributor-insights-enable-describe-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ci-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 120,
            intervalSeconds: 3);

        var baseline = await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName });
        Ensure(
            baseline.ContributorInsightsStatus == "DISABLED",
            $"expected a freshly created table to start DISABLED, got {Py.Repr(baseline.ContributorInsightsStatus?.Value)}");

        var enableResp = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = tableName,
            ContributorInsightsAction = ContributorInsightsAction.ENABLE,
        });
        Ensure(
            enableResp.ContributorInsightsStatus == "ENABLING" || enableResp.ContributorInsightsStatus == "ENABLED",
            $"expected ENABLING or ENABLED right after ENABLE, got {Py.Repr(enableResp.ContributorInsightsStatus?.Value)}");

        var described = await PollUntil(
            () => ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName }),
            desc => desc.ContributorInsightsStatus == "ENABLED",
            timeoutSeconds: 120,
            intervalSeconds: 3);
        Ensure(
            described.ContributorInsightsStatus == "ENABLED",
            $"expected DescribeContributorInsights to settle on ENABLED, got {Py.Repr(described.ContributorInsightsStatus?.Value)}");

        var listed = await PollUntil(
            () => ListCiStatus(ddb, tableName),
            status => status == "ENABLED",
            timeoutSeconds: 60,
            intervalSeconds: 3);
        Ensure(
            listed == "ENABLED",
            $"expected ListContributorInsights to report ENABLED for {Py.Repr(tableName)}, got {Py.Repr(listed)}");

        var disableResp = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = tableName,
            ContributorInsightsAction = ContributorInsightsAction.DISABLE,
        });
        Ensure(
            disableResp.ContributorInsightsStatus == "DISABLING" || disableResp.ContributorInsightsStatus == "DISABLED",
            $"expected DISABLING or DISABLED right after DISABLE, got {Py.Repr(disableResp.ContributorInsightsStatus?.Value)}");

        var describedOff = await PollUntil(
            () => ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName }),
            desc => desc.ContributorInsightsStatus == "DISABLED",
            timeoutSeconds: 120,
            intervalSeconds: 3);
        Ensure(
            describedOff.ContributorInsightsStatus == "DISABLED",
            $"expected DescribeContributorInsights to settle on DISABLED, got {Py.Repr(describedOff.ContributorInsightsStatus?.Value)}");

        var listedOff = await PollUntil(
            () => ListCiStatus(ddb, tableName),
            status => status is null,
            timeoutSeconds: 60,
            intervalSeconds: 3);
        Ensure(
            listedOff is null,
            $"expected {Py.Repr(tableName)} to drop out of ListContributorInsights once DISABLED, still reported as {Py.Repr(listedOff)}");
    }

    private static async Task<string?> ListCiStatus(IAmazonDynamoDB ddb, string tableName)
    {
        var resp = await ddb.ListContributorInsightsAsync(new ListContributorInsightsRequest { TableName = tableName });
        foreach (var summary in resp.ContributorInsightsSummaries ?? [])
        {
            if (summary.TableName == tableName && summary.IndexName is null)
            {
                return summary.ContributorInsightsStatus?.Value;
            }
        }

        foreach (var summary in resp.ContributorInsightsSummaries ?? [])
        {
            if (summary.TableName == tableName)
            {
                return summary.ContributorInsightsStatus?.Value;
            }
        }

        return null;
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds)
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

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }
}
