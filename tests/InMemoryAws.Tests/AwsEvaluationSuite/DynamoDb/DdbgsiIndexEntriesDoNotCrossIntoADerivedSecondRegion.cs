// Translated from behavioral/dynamodb/check_ddbgsi_index_entries_do_not_cross_into_a_derived_second_region.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-index-entries-do-not-cross-into-a-derived-second-region</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiIndexEntriesDoNotCrossIntoADerivedSecondRegion
{
    private const string Check = "ddbgsi-index-entries-do-not-cross-into-a-derived-second-region";
    private const string ErrValidation = "ValidationException";
    private const string Index = "gsi1";
    private const int ConvergeSeconds = 180;

    /// <summary>Return us-east-1, or us-west-2 when ctx.region is us-east-1 (both enabled by default).</summary>
    private static string SecondRegion(FidelityContext ctx) => ctx.Region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>Delete <paramref name="name"/>, retrying ResourceInUseException while the table is still CREATING.</summary>
    private static async Task DeleteTable(IAmazonDynamoDB client, string name)
    {
        var giveUp = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => client.DeleteTableAsync(name));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || giveUp.Elapsed >= TimeSpan.FromSeconds(300))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static Task Create(IAmazonDynamoDB client, string name) => client.CreateTableAsync(new CreateTableRequest
    {
        TableName = name,
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        AttributeDefinitions =
        [
            new AttributeDefinition("pk", ScalarAttributeType.S),
            new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
        ],
        GlobalSecondaryIndexes =
        [
            new GlobalSecondaryIndex
            {
                IndexName = Index,
                KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
            },
        ],
        BillingMode = BillingMode.PAY_PER_REQUEST,
        Tags = [new Tag { Key = "sim2real", Value = Check }],
    });

    private static async Task AwaitIndex(IAmazonDynamoDB client, string region, string name)
    {
        await client.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var giveUp = FidelityTime.StartNew();
        Dictionary<string, string?>? status = null;
        while (giveUp.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
        {
            var described = (await client.DescribeTableAsync(name)).Table;
            status = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (status.GetValueOrDefault(Index) == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"{Index} on {Py.Repr(name)} in {Py.Repr(region)} never became ACTIVE within " +
            $"{ConvergeSeconds}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// A table and its GSI are regional: same-named tables in two regions share no items or entries.
    /// Each region's index serves its own item; a consistent GSI query is refused there too.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-index-entries-do-not-cross-into-a-derived-second-region")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Dynamo;
        var otherRegion = SecondRegion(ctx);
        var away = ctx.DynamoClient(region: otherRegion);
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-region-");

        // One cleanup per create, registered immediately so neither region's table can leak.
        await Create(home, table);
        ctx.OnCleanup(() => DeleteTable(home, table));
        await Create(away, table);
        ctx.OnCleanup(() => DeleteTable(away, table));

        await AwaitIndex(home, ctx.Region, table);
        await AwaitIndex(away, otherRegion, table);

        var homeItem = Item(("pk", S("ITEM#HOME")), ("gsi1pk", S("BUCKET#HOME")), ("body", S("home-payload")));
        var awayItem = Item(("pk", S("ITEM#AWAY")), ("gsi1pk", S("BUCKET#AWAY")), ("body", S("away-payload")));
        await home.PutItemAsync(table, homeItem);
        await away.PutItemAsync(table, awayItem);

        Task<QueryResponse> IndexQuery(IAmazonDynamoDB client, string bucket) => client.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S(bucket))),
        });

        async Task<QueryResponse> Converge(IAmazonDynamoDB client, string region, string bucket, string wantPk)
        {
            List<Dictionary<string, AttributeValue>> items = [];
            var giveUp = FidelityTime.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
            {
                var resp = await IndexQuery(client, bucket);
                items = resp.Items;
                if (items.Select(i => i["pk"].S).SequenceEqual([wantPk]))
                {
                    return resp;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            throw Fail(
                $"the index in {Py.Repr(region)} never converged on {Py.Repr(wantPk)} under " +
                $"{Py.Repr(bucket)} within {ConvergeSeconds}s; the last read held {Py.Repr(items)}");
        }

        // Each region's own index works -- the positive control for both absences below.
        var homeSeen = await Converge(home, ctx.Region, "BUCKET#HOME", "ITEM#HOME");
        var awaySeen = await Converge(away, otherRegion, "BUCKET#AWAY", "ITEM#AWAY");
        Ensure(
            homeSeen.Items[0]["body"].S == "home-payload",
            $"{ctx.Region} returned the wrong item for its own index key: {Py.Repr(homeSeen.Items)}");
        Ensure(
            awaySeen.Items[0]["body"].S == "away-payload",
            $"{otherRegion} returned the wrong item for its own index key: {Py.Repr(awaySeen.Items)}");

        // Effect absent, in both directions: neither region's index or base table holds the other's
        // item, even though both tables carry the same name and the same index.
        var homeForeign = await IndexQuery(home, "BUCKET#AWAY");
        Ensure(
            homeForeign.Items is not null && homeForeign.Items.Count == 0,
            $"{ctx.Region}'s index returned an item written in {otherRegion}: " +
            $"{Py.Repr(homeForeign.Items)}");
        var awayForeign = await IndexQuery(away, "BUCKET#HOME");
        Ensure(
            awayForeign.Items is not null && awayForeign.Items.Count == 0,
            $"{otherRegion}'s index returned an item written in {ctx.Region}: " +
            $"{Py.Repr(awayForeign.Items)}");
        var homeBase = await home.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", awayItem["pk"])), ConsistentRead = true });
        Ensure(
            homeBase.Item is null,
            $"{ctx.Region}'s base table holds the item written in {otherRegion}: " +
            $"{Py.Repr(homeBase.Item)}");
        var awayBase = await away.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", homeItem["pk"])), ConsistentRead = true });
        Ensure(
            awayBase.Item is null,
            $"{otherRegion}'s base table holds the item written in {ctx.Region}: " +
            $"{Py.Repr(awayBase.Item)}");

        // The base table in the partner region is strongly consistent, and its GSI still refuses to be.
        var awayOwn = await away.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", awayItem["pk"])), ConsistentRead = true });
        Ensure(
            awayOwn.Item["body"].S == "away-payload",
            $"the strongly consistent base read in {otherRegion} did not see its own write: " +
            $"{Py.Repr(awayOwn.Item)}");
        var (accepted, refused) = await FidelityContext.Capture(() => away.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            ConsistentRead = true,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("BUCKET#AWAY"))),
        }));
        Ensure(
            accepted is null,
            $"ConsistentRead=True on a GSI must be refused in {otherRegion} as well, but it was " +
            $"accepted and returned {Py.Repr(accepted?.Items)}");
        Ensure(
            refused!.Code == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} for a consistent GSI read in {otherRegion}, got " +
            $"{Py.Repr(refused.Code)} ({Py.Repr(refused.Message)})");
        Ensure(
            refused.Message == "Consistent reads are not supported on global secondary indexes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(refused.Message)}");
    }
}
