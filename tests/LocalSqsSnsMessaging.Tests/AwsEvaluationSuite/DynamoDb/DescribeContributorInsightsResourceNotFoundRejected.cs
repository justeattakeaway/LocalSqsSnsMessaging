// Translated from behavioral/dynamodb/check_describe_contributor_insights_resource_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-contributor-insights-resource-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeContributorInsightsResourceNotFoundRejected
{
    private const string Check = "describe-contributor-insights-resource-not-found-rejected";

    /// <summary>
    /// DescribeContributorInsights on a nonexistent table raises ResourceNotFoundException with the exact message,
    /// while on a table the check created it answers with status DISABLED.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-contributor-insights-resource-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var tableName = FidelityContext.UniqueName("sim2real-behav-ci-notfound-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        var control = await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName });
        Ensure(
            control.ContributorInsightsStatus == ContributorInsightsStatus.DISABLED,
            $"expected positive control to succeed with DISABLED, got {Py.Repr(control.ContributorInsightsStatus)}");

        var fakeTable = FidelityContext.UniqueName("sim2real-notable-");
        var (code, message) = await Error(async () => await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = fakeTable }));
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {code ?? "None"}");
        var expectedMessage = $"Requested resource not found: Table: {fakeTable} not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus?.Value;
            if (last == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never reached ACTIVE, last={last ?? "None"}");
    }
}
