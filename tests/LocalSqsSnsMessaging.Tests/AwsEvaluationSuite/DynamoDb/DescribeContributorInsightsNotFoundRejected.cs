// Translated from behavioral/dynamodb/check_describe_contributor_insights_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-contributor-insights-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeContributorInsightsNotFoundRejected
{
    /// <summary>
    /// DescribeContributorInsights on a table name that does not exist raises
    /// ResourceNotFoundException; the same call against a real table succeeds and reports
    /// contributor insights as disabled by default.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-contributor-insights-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var missingTable = FidelityContext.UniqueName("sim2real-behav-nope-");

        var (code, message) = await Error(async () => await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = missingTable }));
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {code ?? "None"}");
        var expectedMessage = $"Requested resource not found: Table: {missingTable} not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var tableName = FidelityContext.UniqueName("sim2real-behav-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableStatus(ddb, tableName, "ACTIVE");

        var desc = await ddb.DescribeContributorInsightsAsync(new DescribeContributorInsightsRequest { TableName = tableName });
        var status = desc.ContributorInsightsStatus;
        Ensure(status == ContributorInsightsStatus.DISABLED, $"expected DISABLED, got {status}");
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

    private static async Task<TableDescription> WaitTableStatus(IAmazonDynamoDB ddb, string tableName, string targetStatus, double timeoutSeconds = 180, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            last = desc.TableStatus?.Value;
            if (last == targetStatus)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never reached {targetStatus}, last={last ?? "None"}");
    }
}
