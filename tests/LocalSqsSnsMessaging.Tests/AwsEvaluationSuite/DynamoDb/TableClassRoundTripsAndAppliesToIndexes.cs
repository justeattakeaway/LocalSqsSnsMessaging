// Translated from behavioral/dynamodb/check_table_class_round_trips_and_applies_to_indexes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-class-round-trips-and-applies-to-indexes</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableClassRoundTripsAndAppliesToIndexes
{
    /// <summary>Poll DescribeTable until the table is ACTIVE with no index still mutating.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, string table, int deadlineSeconds = 240)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < deadlineSeconds)
        {
            var t = (await ddb.DescribeTableAsync(table)).Table;
            var busy = (t.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (t.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return t;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"{table} never settled to ACTIVE within {deadlineSeconds}s");
    }

    /// <summary><c>str(t.get('TableClassSummary'))</c>.</summary>
    private static string Summary(TableClassSummary? summary) =>
        summary is null
            ? "None"
            : Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["TableClass"] = summary.TableClass?.Value,
                ["LastUpdateDateTime"] = summary.LastUpdateDateTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// A table's class moves from STANDARD to STANDARD_INFREQUENT_ACCESS and back to STANDARD.
    /// After each UpdateTable, TableClassSummary carries the new class and a LastUpdateDateTime.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-class-round-trips-and-applies-to-indexes")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("g1", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "idx1",
                    KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var t = await WaitActive(ddb, table);
        // A default-class table OMITS TableClassSummary entirely (measured against real AWS); the
        // field appears only once the class has been set explicitly. Either shape is correct here.
        Ensure(
            (t.TableClassSummary?.TableClass?.Value ?? "STANDARD") == "STANDARD",
            $"a new table is STANDARD, got {Summary(t.TableClassSummary)}");
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, TableClass = TableClass.STANDARD_INFREQUENT_ACCESS });
        t = await WaitActive(ddb, table);
        Ensure(t.TableClassSummary is not null, "DescribeTable must report TableClassSummary after a class change");
        Ensure(
            t.TableClassSummary.TableClass == TableClass.STANDARD_INFREQUENT_ACCESS,
            $"expected STANDARD_INFREQUENT_ACCESS, got {Summary(t.TableClassSummary)}");
        Ensure(t.TableClassSummary.LastUpdateDateTime is not null, "a class change must stamp LastUpdateDateTime");
        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = table, TableClass = TableClass.STANDARD });
        t = await WaitActive(ddb, table);
        Ensure(
            t.TableClassSummary?.TableClass?.Value == "STANDARD",
            $"expected STANDARD after the class moves back, got {Summary(t.TableClassSummary)}");
    }
}
