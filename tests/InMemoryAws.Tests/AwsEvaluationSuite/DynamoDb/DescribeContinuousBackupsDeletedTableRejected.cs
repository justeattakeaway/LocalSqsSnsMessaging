// Translated from behavioral/dynamodb/check_describe_continuous_backups_deleted_table_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-continuous-backups-deleted-table-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeContinuousBackupsDeletedTableRejected
{
    private const string CheckId = "describe-continuous-backups-deleted-table-rejected";

    /// <summary>
    /// DescribeContinuousBackups on a live table reports ContinuousBackupsStatus ENABLED; the
    /// identical call on that table's name after DeleteTable raises TableNotFoundException with the
    /// exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-continuous-backups-deleted-table-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-dcb-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        // Positive control: DescribeContinuousBackups on the live table succeeds and reports the
        // continuous-backups machinery as enabled. Without this, a target that returns
        // TableNotFoundException for every DescribeContinuousBackups call would pass the negative
        // below too.
        var described = await WaitContinuousBackupsEnabled(ddb, tableName);
        Ensure(described.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED, Py.Repr(described.ContinuousBackupsStatus));

        await ddb.DeleteTableAsync(tableName);
        await WaitTableGone(ddb, tableName);

        // The identical call on the now-deleted table name is rejected, not answered with a stale or
        // empty description.
        var (code, message) = await Error(() => ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName }));
        Ensure(code == "TableNotFoundException", $"expected TableNotFoundException, got {code}");
        var expected = $"Table not found: {tableName}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // A table name that never existed at all is rejected the identical way.
        var neverExisted = FidelityContext.UniqueName("sim2real-behav-dcb-never-");
        var (neverCode, neverMessage) = await Error(() => ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = neverExisted }));
        Ensure(neverCode == "TableNotFoundException", $"expected TableNotFoundException, got {neverCode}");
        var expectedNever = $"Table not found: {neverExisted}";
        Ensure(neverMessage == expectedNever, $"expected message {Py.Repr(expectedNever)}, got {Py.Repr(neverMessage)}");
    }

    /// <summary>Call fn(); return the (Error.Code, Error.Message). An accepted call raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call succeeded: {Py.Repr(result)}");
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            if ((await ddb.DescribeTableAsync(tableName)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} did not become ACTIVE within {timeoutS}s");
    }

    private static async Task<ContinuousBackupsDescription> WaitContinuousBackupsEnabled(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var desc = (await ddb.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName })).ContinuousBackupsDescription;
            if (desc.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"continuous backups for {tableName} never became ENABLED");
    }

    private static async Task WaitTableGone(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            try
            {
                await ddb.DescribeTableAsync(tableName);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} was never deleted within {timeoutS}s");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
