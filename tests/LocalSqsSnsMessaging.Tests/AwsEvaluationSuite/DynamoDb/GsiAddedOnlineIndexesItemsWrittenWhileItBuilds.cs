// Translated from behavioral/dynamodb/check_gsi_added_online_indexes_items_written_while_it_builds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-added-online-indexes-items-written-while-it-builds</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiAddedOnlineIndexesItemsWrittenWhileItBuilds
{
    private const string Index = "by-color";
    private static readonly string[] DuringPks = ["during-1", "during-2", "during-3"];
    private static readonly string[] Want = ["before", "during-1", "during-2", "during-3"];
    private static readonly string[] WantAll = ["before", "during-1", "during-2", "during-3", "during-blue"];

    /// <summary>
    /// Items written right after UpdateTable adds a GSI, while it builds, are queryable through it
    /// once ACTIVE, together with the item that existed before; an item without the key is not indexed.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.OnlineOps.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-added-online-indexes-items-written-while-it-builds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsibuild-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        await ddb.PutItemAsync(table, Item(("pk", S("before")), ("color", S("red"))));

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("color", ScalarAttributeType.S)],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = Index,
                        KeySchema = [new KeySchemaElement("color", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        foreach (var pk in DuringPks)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("color", S("red"))));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("during-blue")), ("color", S("blue"))));
        await ddb.PutItemAsync(table, Item(("pk", S("during-unkeyed")), ("shade", S("red"))));

        var desc = await WaitActive(ddb, ctx, table);
        var indexes = (desc.GlobalSecondaryIndexes ?? []).Select(g => (g.IndexName, g.IndexStatus?.Value)).ToList();
        Ensure(indexes.Count == 1 && indexes[0] == (Index, "ACTIVE"), $"after the build the table lists GSIs {Py.Repr(indexes)}");

        async Task<List<string>> Red()
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            var request = new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                KeyConditionExpression = "color = :c",
                ExpressionAttributeValues = Item((":c", S("red"))),
            };
            while (true)
            {
                var page = await ddb.QueryAsync(request);
                items.AddRange(page.Items);
                if (page.LastEvaluatedKey is not { Count: > 0 })
                {
                    break;
                }

                request.ExclusiveStartKey = page.LastEvaluatedKey;
            }

            return [.. items.Select(i => i["pk"].S).Order(StringComparer.Ordinal)];
        }

        async Task<List<string>> Indexed()
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            var request = new ScanRequest { TableName = table, IndexName = Index };
            while (true)
            {
                var page = await ddb.ScanAsync(request);
                items.AddRange(page.Items);
                if (page.LastEvaluatedKey is not { Count: > 0 })
                {
                    break;
                }

                request.ExclusiveStartKey = page.LastEvaluatedKey;
            }

            return [.. items.Select(i => i["pk"].S).Order(StringComparer.Ordinal)];
        }

        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, ctx.TimeLeftSeconds - 20.0));
        List<string> got;
        List<string> held;
        while (true)
        {
            got = await Red();
            held = await Indexed();
            if ((got.SequenceEqual(Want) && held.SequenceEqual(WantAll)) || clock.Elapsed >= budget)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(got.SequenceEqual(Want), $"the built GSI answers color = red with {Py.Repr(got)}");
        Ensure(held.SequenceEqual(WantAll), $"the built GSI holds {Py.Repr(held)}; the item without 'color' must be absent");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, ctx.TimeLeftSeconds - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }
}
