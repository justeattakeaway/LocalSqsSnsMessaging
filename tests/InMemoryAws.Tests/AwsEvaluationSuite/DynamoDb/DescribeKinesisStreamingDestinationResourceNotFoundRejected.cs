// Translated from behavioral/dynamodb/check_describe_kinesis_streaming_destination_resource_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-kinesis-streaming-destination-resource-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeKinesisStreamingDestinationResourceNotFoundRejected
{
    /// <summary>
    /// DescribeKinesisStreamingDestination on a fresh table returns no destinations, while on a nonexistent table it
    /// raises ResourceNotFoundException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-kinesis-streaming-destination-resource-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var tableName = FidelityContext.UniqueName("sim2real-notable-real-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitTableActive(ddb, tableName);

        var realDests = (await ddb.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = tableName }))
            .KinesisDataStreamDestinations;
        Ensure(realDests is { Count: 0 }, $"expected no streaming destinations on a fresh table, got {Py.Repr(realDests)}");

        var fakeTable = FidelityContext.UniqueName("sim2real-notable-missing-");
        var (code, message) = await Error(() => ddb.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = fakeTable }));
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {code}");
        var expectedMessage = $"Requested resource not found: Table: {fakeTable} not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120, double intervalSeconds = 3)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
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
