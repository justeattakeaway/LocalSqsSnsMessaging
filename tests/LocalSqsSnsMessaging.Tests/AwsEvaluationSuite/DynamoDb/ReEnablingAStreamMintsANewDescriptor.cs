// Translated from behavioral/dynamodb/check_re_enabling_a_stream_mints_a_new_descriptor.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.re-enabling-a-stream-mints-a-new-descriptor</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReEnablingAStreamMintsANewDescriptor
{
    /// <summary>Poll DescribeTable until the table is ACTIVE with no index still mutating.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, double deadlineS = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(deadlineS))
        {
            var t = (await ddb.DescribeTableAsync(table)).Table;
            var busy = (t.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (t.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineS}s");
    }

    /// <summary>
    /// Disabling and re-enabling a stream does not resume the old one: DynamoDB creates a NEW stream
    /// with a different descriptor, so a consumer holding the old ARN is reading a dead stream.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.re-enabling-a-stream-mints-a-new-descriptor")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var first = (await ddb.DescribeTableAsync(table)).Table.LatestStreamArn;
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, StreamSpecification = new StreamSpecification { StreamEnabled = false } });
        await WaitActive(ddb, table);
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        });
        var t = await WaitActive(ddb, table);
        var second = t.LatestStreamArn;
        Ensure(!string.IsNullOrEmpty(second) && second != first, $"re-enabling must mint a new stream descriptor, got the same one: {second}");
    }
}
