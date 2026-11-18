// Translated from behavioral/dynamodb/check_contributor_insights_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.contributor-insights-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContributorInsightsNotFound
{
    private const string CheckId = "contributor-insights-not-found";

    /// <summary>
    /// Describe/List/UpdateContributorInsights return ResourceNotFoundException with AWS's exact
    /// message for a missing table; the same calls succeed on a real table, so the rejection is
    /// specific to the missing table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.contributor-insights-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var missing = FidelityContext.UniqueName("sim2real-behav-no-such-table-");

        var notFoundMsg = $"Requested resource not found: Table: {missing} not found";

        var (describeCode, describeMsg) = await Error(() => ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = missing }));
        Ensure(describeCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {describeCode}");
        Ensure(describeMsg == notFoundMsg, $"DescribeContributorInsights: expected {Py.Repr(notFoundMsg)}, got {Py.Repr(describeMsg)}");

        var (listCode, listMsg) = await Error(() => ddb.ListContributorInsightsAsync(new ListContributorInsightsRequest { TableName = missing }));
        Ensure(listCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {listCode}");
        Ensure(listMsg == notFoundMsg, $"ListContributorInsights: expected {Py.Repr(notFoundMsg)}, got {Py.Repr(listMsg)}");

        var (updateCode, updateMsg) = await Error(() => ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = missing,
            ContributorInsightsAction = ContributorInsightsAction.ENABLE,
        }));
        Ensure(updateCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {updateCode}");
        Ensure(updateMsg == notFoundMsg, $"UpdateContributorInsights: expected {Py.Repr(notFoundMsg)}, got {Py.Repr(updateMsg)}");

        // Positive control: the identical calls against a real table must succeed — otherwise
        // "rejects a nonexistent table" is indistinguishable from "rejects every call".
        var tableName = FidelityContext.UniqueName("sim2real-behav-ci-table-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        var described = await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName });
        Ensure(
            described.ContributorInsightsStatus == "DISABLED",
            $"expected DISABLED by default, got {described.ContributorInsightsStatus?.Value}");

        var listed = await ddb.ListContributorInsightsAsync(new ListContributorInsightsRequest { TableName = tableName });
        Ensure(listed.ContributorInsightsSummaries is not null, $"expected a summaries list, got {Py.Repr(new Dictionary<string, object?>())}");

        var updated = await ddb.UpdateContributorInsightsAsync(new UpdateContributorInsightsRequest
        {
            TableName = tableName,
            ContributorInsightsAction = ContributorInsightsAction.ENABLE,
        });
        Ensure(
            updated.ContributorInsightsStatus == "ENABLING" || updated.ContributorInsightsStatus == "ENABLED",
            $"expected ENABLING or ENABLED, got {updated.ContributorInsightsStatus?.Value}");
    }

    /// <summary>Call fn(); return its ClientError (Code, Message). An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected ResourceNotFoundException, but the call SUCCEEDED and returned {result}");
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
            if (last == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never reached ACTIVE, last={last}");
    }
}
