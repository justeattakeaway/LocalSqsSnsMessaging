// Translated from behavioral/dynamodb/check_update_table_decrease_after_increase_records_number_of_decreases_today.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-decrease-after-increase-records-number-of-decreases-today</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableDecreaseAfterIncreaseRecordsNumberOfDecreasesToday
{
    private const string Check = "update-table-decrease-after-increase-records-number-of-decreases-today";

    /// <summary>
    /// A provisioned-capacity decrease that follows an increase leaves DescribeTable reporting
    /// NumberOfDecreasesToday 1 and LastDecreaseDateTime no earlier than LastIncreaseDateTime.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-decrease-after-increase-records-number-of-decreases-today")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-incdec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        var active = await PollUntil(ddb, table, d => d.TableStatus == TableStatus.ACTIVE, timeoutS: 280, description: "ACTIVE");
        var pt0 = active.ProvisionedThroughput;
        Ensure(
            pt0.NumberOfDecreasesToday == 0,
            $"a fresh table's ProvisionedThroughput is {Repr(pt0)}, expected NumberOfDecreasesToday 0");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 3, WriteCapacityUnits = 3 },
        });
        var increased = (await PollUntil(
            ddb,
            table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.ProvisionedThroughput.ReadCapacityUnits, d.ProvisionedThroughput.WriteCapacityUnits) == (3, 3),
            timeoutS: 280,
            description: "ACTIVE at 3 read / 3 write")).ProvisionedThroughput;
        var lastIncrease = increased.LastIncreaseDateTime;
        Ensure(
            lastIncrease is not null,
            $"ProvisionedThroughput after the increase is {Repr(increased)}, expected LastIncreaseDateTime present");
        Ensure(
            increased.LastDecreaseDateTime is null,
            $"ProvisionedThroughput after only an increase is {Repr(increased)}, expected LastDecreaseDateTime absent");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
        });
        var decreased = (await PollUntil(
            ddb,
            table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.ProvisionedThroughput.ReadCapacityUnits, d.ProvisionedThroughput.WriteCapacityUnits) == (1, 1),
            timeoutS: 280,
            description: "ACTIVE at 1 read / 1 write")).ProvisionedThroughput;

        Ensure(
            decreased.NumberOfDecreasesToday == 1,
            $"ProvisionedThroughput after one decrease is {Repr(decreased)}, expected NumberOfDecreasesToday 1");
        var lastDecrease = decreased.LastDecreaseDateTime;
        Ensure(
            lastDecrease is not null,
            $"ProvisionedThroughput after the decrease is {Repr(decreased)}, expected LastDecreaseDateTime present");
        Ensure(
            lastDecrease >= lastIncrease,
            $"LastDecreaseDateTime {Py.Repr(lastDecrease)} is earlier than LastIncreaseDateTime {Py.Repr(lastIncrease)}");
        Ensure(
            decreased.LastIncreaseDateTime == lastIncrease,
            $"LastIncreaseDateTime changed from {Py.Repr(lastIncrease)} to {Py.Repr(decreased.LastIncreaseDateTime)} " +
            "across a call that only decreased capacity");
    }

    /// <summary>Poll DescribeTable every 2 s for <paramref name="table"/> until <paramref name="predicate"/> holds.</summary>
    private static async Task<TableDescription> PollUntil(IAmazonDynamoDB ddb, string table, Func<TableDescription, bool> predicate, double timeoutS, string description)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (predicate(desc))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} never reached {description}; last {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>The ProvisionedThroughput dict as boto3 hands it to the check.</summary>
    private static string Repr(ProvisionedThroughputDescription pt)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (pt.LastIncreaseDateTime is { } inc)
        {
            dict["LastIncreaseDateTime"] = inc;
        }

        if (pt.LastDecreaseDateTime is { } dec)
        {
            dict["LastDecreaseDateTime"] = dec;
        }

        if (pt.NumberOfDecreasesToday is { } n)
        {
            dict["NumberOfDecreasesToday"] = n;
        }

        if (pt.ReadCapacityUnits is { } r)
        {
            dict["ReadCapacityUnits"] = r;
        }

        if (pt.WriteCapacityUnits is { } w)
        {
            dict["WriteCapacityUnits"] = w;
        }

        return Py.Repr(dict);
    }
}
