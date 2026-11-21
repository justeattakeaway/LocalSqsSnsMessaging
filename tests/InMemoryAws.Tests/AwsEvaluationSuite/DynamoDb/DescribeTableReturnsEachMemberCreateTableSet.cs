// Translated from behavioral/dynamodb/check_describe_table_returns_each_member_create_table_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-table-returns-each-member-create-table-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeTableReturnsEachMemberCreateTableSet
{
    private const string CheckId = "describe-table-returns-each-member-create-table-set";

    /// <summary>cap_s, shortened when the runner stops the check sooner, keeping 5 s for the failure.</summary>
    private static double WaitCap(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>A cleanup deleting the table, backing off while a create or tag update holds it; a failure raises.</summary>
    private static Func<Task> DropTable(IAmazonDynamoDB ddb, string name)
    {
        async Task<string> Status()
        {
            try
            {
                return (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                return "GONE";
            }
        }

        return async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                // A table just created can still read not-found, so GONE still sends the delete.
                if (await Status() == "DELETING")
                {
                    return;
                }

                try
                {
                    await ddb.DeleteTableAsync(name);
                    return;
                }
                catch (ResourceNotFoundException)
                {
                    return;
                }
                catch (ResourceInUseException) when (attempt != 7)
                {
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(3 * Math.Pow(2, attempt), 60)));
            }
        };
    }

    /// <summary>CreateTable, its delete registered at once, then DescribeTable polled to ACTIVE.</summary>
    private static async Task<TableDescription> NewTable(FidelityContext ctx, IAmazonDynamoDB ddb, CreateTableRequest request)
    {
        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(DropTable(ddb, request.TableName));
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 240.0);
        TableDescription table;
        while (true)
        {
            try
            {
                table = (await ddb.DescribeTableAsync(request.TableName)).Table;
            }
            catch (ResourceNotFoundException) when (clock.Elapsed.TotalSeconds < deadline)
            {
                // DescribeTable can read not-found for a moment after CreateTable.
                table = new TableDescription { TableStatus = TableStatus.CREATING };
            }

            if (table.TableStatus == TableStatus.ACTIVE || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(table.TableStatus == TableStatus.ACTIVE, $"table {request.TableName} settled at {Py.Repr(table.TableStatus?.Value)}");
        return table;
    }

    private static List<Dictionary<string, object?>>? Attributes(IEnumerable<AttributeDefinition>? attributes) =>
        attributes?.OrderBy(a => a.AttributeName, StringComparer.Ordinal)
            .Select(a => new Dictionary<string, object?> { ["AttributeName"] = a.AttributeName, ["AttributeType"] = a.AttributeType?.Value })
            .ToList();

    private static List<Dictionary<string, object?>>? Keys(IEnumerable<KeySchemaElement>? keys) =>
        keys?.Select(k => new Dictionary<string, object?> { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType?.Value }).ToList();

    private static Dictionary<string, object?>? Stream(StreamSpecification? stream)
    {
        if (stream is null)
        {
            return null;
        }

        var dict = new Dictionary<string, object?>();
        if (stream.StreamEnabled is { } enabled)
        {
            dict["StreamEnabled"] = enabled;
        }

        if (stream.StreamViewType is { } view)
        {
            dict["StreamViewType"] = view.Value;
        }

        return dict;
    }

    /// <summary>
    /// DescribeTable on the ACTIVE table returns each member CreateTable set with the value sent:
    /// keys, attribute types, on-demand billing, table class and stream.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-table-returns-each-member-create-table-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-dtcm-");
        List<AttributeDefinition> attributes = [new("pk", ScalarAttributeType.S), new("sk", ScalarAttributeType.N)];
        List<KeySchemaElement> keys = [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)];
        var stream = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY };
        await NewTable(ctx, ddb, new CreateTableRequest
        {
            TableName = name,
            AttributeDefinitions = attributes,
            KeySchema = keys,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            TableClass = TableClass.STANDARD_INFREQUENT_ACCESS,
            StreamSpecification = stream,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });

        var table = (await ddb.DescribeTableAsync(name)).Table;
        var sent = new Dictionary<string, object?>
        {
            ["TableName"] = name,
            ["AttributeDefinitions"] = Attributes(attributes),
            ["KeySchema"] = Keys(keys),
            ["BillingMode"] = "PAY_PER_REQUEST",
            ["TableClass"] = "STANDARD_INFREQUENT_ACCESS",
            ["StreamSpecification"] = Stream(stream),
        };
        var read = new Dictionary<string, object?>
        {
            ["TableName"] = table.TableName,
            ["AttributeDefinitions"] = Attributes(table.AttributeDefinitions ?? []),
            ["KeySchema"] = Keys(table.KeySchema),
            ["BillingMode"] = table.BillingModeSummary?.BillingMode?.Value,
            ["TableClass"] = table.TableClassSummary?.TableClass?.Value,
            ["StreamSpecification"] = Stream(table.StreamSpecification),
        };
        var differ = sent.Keys
            .Where(m => !Py.Equal(read[m], sent[m]))
            .ToDictionary(m => m, m => new Dictionary<string, object?> { ["sent"] = sent[m], ["read"] = read[m] });
        Ensure(differ.Count == 0, $"DescribeTable does not return what CreateTable set: {Py.Repr(differ)}");
    }
}
