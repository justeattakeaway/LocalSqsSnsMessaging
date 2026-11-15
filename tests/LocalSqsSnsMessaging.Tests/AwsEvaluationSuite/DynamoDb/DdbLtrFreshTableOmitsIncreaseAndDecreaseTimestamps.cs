// Translated from behavioral/dynamodb/check_ddb_ltr_fresh_table_omits_increase_and_decrease_timestamps.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-ltr-fresh-table-omits-increase-and-decrease-timestamps</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbLtrFreshTableOmitsIncreaseAndDecreaseTimestamps
{
    private const string Check = "ddb-ltr-fresh-table-omits-increase-and-decrease-timestamps";

    /// <summary>
    /// A fresh PROVISIONED table omits LastIncreaseDateTime and LastDecreaseDateTime; after an
    /// UpdateTable increase, LastIncreaseDateTime appears no earlier than the table's own
    /// CreationDateTime while LastDecreaseDateTime stays absent.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-ltr-fresh-table-omits-increase-and-decrease-timestamps")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddb-ltr-incdec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        var table = await PollTable(ddb, name, t => t.TableStatus == TableStatus.ACTIVE, "ACTIVE");
        var created = table.CreationDateTime;
        var pt = table.ProvisionedThroughput;
        Ensure(
            pt.LastIncreaseDateTime is null && pt.LastDecreaseDateTime is null,
            $"a fresh table's ProvisionedThroughput is {Repr(pt)}, expected neither timestamp present");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = name,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 10, WriteCapacityUnits = 10 },
        });
        // UpdateTable can return before DescribeTable leaves ACTIVE, so wait for the new capacities.
        var pt2 = (await PollTable(
            ddb,
            name,
            t => t.TableStatus == TableStatus.ACTIVE
                && t.ProvisionedThroughput.ReadCapacityUnits == 10 && t.ProvisionedThroughput.WriteCapacityUnits == 10,
            "ACTIVE at 10 read / 10 write")).ProvisionedThroughput;

        Ensure(
            pt2.ReadCapacityUnits == 10 && pt2.WriteCapacityUnits == 10,
            $"ProvisionedThroughput after the increase is {Repr(pt2)}, expected 10 read / 10 write");
        var increased = pt2.LastIncreaseDateTime;
        Ensure(
            increased is not null,
            $"ProvisionedThroughput after an increase is {Repr(pt2)}, expected LastIncreaseDateTime present");
        Ensure(
            increased >= created,
            $"LastIncreaseDateTime {Py.Repr(increased)} is earlier than the table's CreationDateTime {Py.Repr(created)}");
        Ensure(
            pt2.LastDecreaseDateTime is null,
            $"ProvisionedThroughput after only an increase is {Repr(pt2)}, expected LastDecreaseDateTime absent");
    }

    /// <summary>The ProvisionedThroughput description as boto3 hands it back: only the members present.</summary>
    private static string Repr(ProvisionedThroughputDescription pt)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (pt.LastIncreaseDateTime is { } inc)
        {
            d["LastIncreaseDateTime"] = inc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (pt.LastDecreaseDateTime is { } dec)
        {
            d["LastDecreaseDateTime"] = dec.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (pt.NumberOfDecreasesToday is { } n)
        {
            d["NumberOfDecreasesToday"] = n;
        }

        if (pt.ReadCapacityUnits is { } r)
        {
            d["ReadCapacityUnits"] = r;
        }

        if (pt.WriteCapacityUnits is { } w)
        {
            d["WriteCapacityUnits"] = w;
        }

        return Py.Repr(d);
    }

    /// <summary>Poll DescribeTable until <c>done(table)</c> holds, against a clock deadline.</summary>
    private static async Task<TableDescription> PollTable(IAmazonDynamoDB ddb, string name, Func<TableDescription, bool> done, string description)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? t = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(280))
        {
            t = (await ddb.DescribeTableAsync(name)).Table;
            if (done(t))
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {name} never reached {description}; last {t?.TableStatus}");
    }
}
