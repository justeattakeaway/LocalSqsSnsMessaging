// Translated from behavioral/dynamodb/check_kinesis_destination_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.kinesis-destination-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class KinesisDestinationNotFound
{
    private const string CheckId = "kinesis-destination-not-found";

    /// <summary>The Error dict of a call that MUST be refused; RAISES if the call succeeds.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>
    /// DescribeKinesisStreamingDestination on a nonexistent table raises ResourceNotFoundException with the exact
    /// message, while on a real table it returns no destinations.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.kinesis-destination-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var table = FidelityContext.UniqueName("sim2real-behav-kds-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitForTableActive(ddb, table);

        var live = await ddb.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = table });
        Ensure(live.KinesisDataStreamDestinations is { Count: 0 }, Py.Repr(new Dictionary<string, object?> { ["TableName"] = live.TableName, ["KinesisDataStreamDestinations"] = live.KinesisDataStreamDestinations }));

        var missing = FidelityContext.UniqueName("sim2real-behav-no-such-table-");
        var err = await Error(() => ddb.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = missing }));
        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err.Code}");
        var expected = $"Requested resource not found: Table: {missing} not found";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
    }
}
