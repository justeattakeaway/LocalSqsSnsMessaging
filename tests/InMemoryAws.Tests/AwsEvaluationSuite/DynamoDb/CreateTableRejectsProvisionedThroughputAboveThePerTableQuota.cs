// Translated from behavioral/dynamodb/check_create_table_rejects_provisioned_throughput_above_the_per_table_quota.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-rejects-provisioned-throughput-above-the-per-table-quota</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableRejectsProvisionedThroughputAboveThePerTableQuota
{
    private const string CheckId = "create-table-rejects-provisioned-throughput-above-the-per-table-quota";

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success fails too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected a ClientError, but the call succeeded: {Py.Repr(result)}");
    }

    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 300.0, double intervalS = 3.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static CreateTableRequest Provisioned(string name, long rcu, long wcu) => new()
    {
        TableName = name,
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        BillingMode = BillingMode.PROVISIONED,
        ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = rcu, WriteCapacityUnits = wcu },
    };

    private static string Over(string member, long requested, string region, long max) =>
        $"The requested {member}, {requested}, is above the per table maximum for "
        + $"the account in {region}. Per table maximum: {max}. Refer to the "
        + "Amazon DynamoDB Developer Guide for current limits and how to request higher limits.";

    /// <summary>
    /// CreateTable refuses a ReadCapacityUnits or WriteCapacityUnits one above the per-table
    /// ceiling DescribeLimits reports, with LimitExceededException and no table created; a
    /// request within the ceiling succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-rejects-provisioned-throughput-above-the-per-table-quota")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var limits = await ddb.DescribeLimitsAsync(new DescribeLimitsRequest());
        var tableMaxRead = limits.TableMaxReadCapacityUnits!.Value;
        var tableMaxWrite = limits.TableMaxWriteCapacityUnits!.Value;

        var overRead = tableMaxRead + 1;
        var tRead = FidelityContext.UniqueName("sim2real-behav-ddb-qr-");
        await ExpectError(
            () => ddb.CreateTableAsync(Provisioned(tRead, overRead, 1)),
            ("LimitExceededException", Over("ReadCapacityUnits", overRead, ctx.Region, tableMaxRead)));
        await ExpectError(
            () => ddb.DescribeTableAsync(tRead),
            ("ResourceNotFoundException", $"Requested resource not found: Table: {tRead} not found"));

        var overWrite = tableMaxWrite + 1;
        var tWrite = FidelityContext.UniqueName("sim2real-behav-ddb-qw-");
        await ExpectError(
            () => ddb.CreateTableAsync(Provisioned(tWrite, 1, overWrite)),
            ("LimitExceededException", Over("WriteCapacityUnits", overWrite, ctx.Region, tableMaxWrite)));
        await ExpectError(
            () => ddb.DescribeTableAsync(tWrite),
            ("ResourceNotFoundException", $"Requested resource not found: Table: {tWrite} not found"));

        var overReadBoth = tableMaxRead + 2;
        var overWriteBoth = tableMaxWrite + 2;
        var tBoth = FidelityContext.UniqueName("sim2real-behav-ddb-qb-");
        await ExpectError(
            () => ddb.CreateTableAsync(Provisioned(tBoth, overReadBoth, overWriteBoth)),
            ("LimitExceededException", Over("ReadCapacityUnits", overReadBoth, ctx.Region, tableMaxRead)));
        await ExpectError(
            () => ddb.DescribeTableAsync(tBoth),
            ("ResourceNotFoundException", $"Requested resource not found: Table: {tBoth} not found"));

        var tOk = FidelityContext.UniqueName("sim2real-behav-ddb-qok-");
        var ok = Provisioned(tOk, 5, 5);
        ok.Tags = [new Tag { Key = "sim2real", Value = CheckId }];
        await ddb.CreateTableAsync(ok);
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(tOk);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        var desc = await WaitActive(ddb, tOk);
        Ensure(desc.ProvisionedThroughput.ReadCapacityUnits == 5, $"expected ReadCapacityUnits 5, got {Py.Repr(desc.ProvisionedThroughput.ReadCapacityUnits)}");
        Ensure(desc.ProvisionedThroughput.WriteCapacityUnits == 5, $"expected WriteCapacityUnits 5, got {Py.Repr(desc.ProvisionedThroughput.WriteCapacityUnits)}");
    }
}
