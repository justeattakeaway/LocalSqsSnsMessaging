// Translated from behavioral/dynamodb/check_autoscaling_and_kinesis_destination_defaults.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.autoscaling-and-kinesis-destination-defaults</summary>
[Category("AwsEvaluationSuite")]
public sealed class AutoscalingAndKinesisDestinationDefaults
{
    private const string Check = "autoscaling-and-kinesis-destination-defaults";

    /// <summary>DescribeTableReplicaAutoScaling rejects a non-replicated table, and a fresh table has zero Kinesis destinations.</summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "DescribeTableReplicaAutoScaling on a plain table says 'Requested resource not found: Table: <name> is not a global table', not \"Global table with name: '<name>' does not exist.\"")]
    [DisplayName("dynamodb.autoscaling-and-kinesis-destination-defaults")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);

        // Real DynamoDB treats replica auto-scaling as a global-table-only concept: a plain
        // single-region table is reported as a non-existent global table.
        Task<DescribeTableReplicaAutoScalingResponse> DescribeAutoscaling() =>
            ddb.DescribeTableReplicaAutoScalingAsync(new DescribeTableReplicaAutoScalingRequest { TableName = tableName });

        var (code, message) = await Error(DescribeAutoscaling);
        Ensure(
            code == "ResourceNotFoundException",
            "expected ResourceNotFoundException for replica auto-scaling on a " +
            $"non-replicated table, got {code}");
        var expectedMsg = $"Global table with name: '{tableName}' does not exist.";
        Ensure(message == expectedMsg, $"expected {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        var kds = await ddb.DescribeKinesisStreamingDestinationAsync(new DescribeKinesisStreamingDestinationRequest { TableName = tableName });
        Ensure(kds.TableName == tableName, $"expected TableName {Py.Repr(tableName)} echoed back, got {Py.Repr(kds.TableName)}");
        Ensure(
            kds.KinesisDataStreamDestinations is { Count: 0 },
            "expected no Kinesis destinations on a fresh table, got " +
            $"{Py.Repr(kds.KinesisDataStreamDestinations)}");
    }

    /// <summary>The (Code, Message) of a call real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// Poll DescribeTable until ACTIVE. CreateTable is eventually consistent, so a
    /// transient ResourceNotFoundException counts as "not yet", never a failure.
    /// </summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 90.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            try
            {
                status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s (last={status})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);
        return tableName;
    }
}
