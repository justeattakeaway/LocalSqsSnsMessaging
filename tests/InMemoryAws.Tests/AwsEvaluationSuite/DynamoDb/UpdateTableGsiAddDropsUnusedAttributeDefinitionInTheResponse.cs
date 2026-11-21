// Translated from behavioral/dynamodb/check_update_table_gsi_add_drops_unused_attribute_definition_in_the_response.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-add-drops-unused-attribute-definition-in-the-response</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiAddDropsUnusedAttributeDefinitionInTheResponse
{
    private const double WaitS = 1500.0;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Poll DescribeTable until the table and every index are ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableSettled(IAmazonDynamoDB ddb, string name, double timeoutS = WaitS, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? table = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            table = (await ddb.DescribeTableAsync(name)).Table;
            var busy = (table.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus != IndexStatus.ACTIVE).ToList();
            if (table.TableStatus == TableStatus.ACTIVE && busy.Count == 0)
            {
                return table;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{name} never settled to ACTIVE within {timeoutS}s; last={table}");
    }

    private static Dictionary<string, string> Attrs(IEnumerable<AttributeDefinition> definitions) =>
        definitions.ToDictionary(a => a.AttributeName, a => a.AttributeType.Value, StringComparer.Ordinal);

    /// <summary>
    /// UpdateTable's own response already drops an AttributeDefinitions entry no live key uses
    /// when it adds a GSI, matching the settled DescribeTable, and the index serves a query.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-add-drops-unused-attribute-definition-in-the-response")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsiresp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await WaitTableSettled(ddb, table);

        // Add a GSI keyed on g1, declaring an extra attribute no key (base or index) uses.
        var updateResp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("g1", ScalarAttributeType.S),
                new AttributeDefinition("unused", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-main",
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var responseAttrs = Attrs(updateResp.TableDescription.AttributeDefinitions);
        Ensure(
            Py.Equal(responseAttrs, new Dictionary<string, string> { ["pk"] = "S", ["g1"] = "S" }),
            $"expected UpdateTable's own response to already drop the unused attribute, leaving "
            + $"{{'pk': 'S', 'g1': 'S'}}, got {Py.Repr(responseAttrs)}");

        var settled = await WaitTableSettled(ddb, table);
        var settledAttrs = Attrs(settled.AttributeDefinitions);
        Ensure(
            Py.Equal(settledAttrs, responseAttrs),
            $"settled DescribeTable AttributeDefinitions {Py.Repr(settledAttrs)} must match what "
            + $"UpdateTable's own response already reported, {Py.Repr(responseAttrs)}");

        // Positive control: the index is real, not just a vanished attribute -- it serves a query,
        // polled because a GSI read is eventually consistent.
        await ddb.PutItemAsync(table, Item(("pk", S("row-1")), ("g1", S("alpha"))));
        var expected = new[] { Item(("pk", S("row-1")), ("g1", S("alpha"))) };
        var found = await PollUntil(
            async () => (await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi-main",
                KeyConditionExpression = "g1 = :v",
                ExpressionAttributeValues = Item((":v", S("alpha"))),
            })).Items,
            items => Py.Equal(items, expected),
            timeoutS: 60.0,
            description: "query through gsi-main returning the item it indexes");
        Ensure(Py.Equal(found, expected), $"expected the query through gsi-main to return the one item it indexes, got {Py.Repr(found)}");
    }
}
