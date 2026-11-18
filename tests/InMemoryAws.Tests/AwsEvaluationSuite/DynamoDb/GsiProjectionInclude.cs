// Translated from behavioral/dynamodb/check_gsi_projection_include.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-projection-include</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiProjectionInclude
{
    private const string CheckId = "gsi-projection-include";

    /// <summary>GSI with INCLUDE projection returns only specified non-key attributes.</summary>
    [Test]
    [DisplayName("dynamodb.gsi-projection-include")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        List<GlobalSecondaryIndex> gsi =
        [
            new GlobalSecondaryIndex
            {
                IndexName = "cat-index",
                KeySchema = [new KeySchemaElement("category", KeyType.HASH)],
                Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["title"] },
            },
        ];
        var table = FidelityContext.UniqueName("sim2real-behav-");
        List<KeySchemaElement> ks = [new KeySchemaElement("pk", KeyType.HASH)];
        List<AttributeDefinition> ad = [new AttributeDefinition("pk", ScalarAttributeType.S)];
        foreach (var ix in gsi)
        {
            foreach (var ke in ix.KeySchema)
            {
                if (!ad.Any(a => a.AttributeName == ke.AttributeName))
                {
                    ad.Add(new AttributeDefinition(ke.AttributeName, ScalarAttributeType.S));
                }
            }
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = ks,
            AttributeDefinitions = ad,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes = gsi,
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("inc-1")), ("category", S("books")), ("title", S("Book1")), ("secret", S("hidden"))));

        var @base = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("inc-1"))), ConsistentRead = true })).Item;
        Ensure(
            @base["secret"].S == "hidden",
            $"the base item never carried 'secret', so the absence asserted below would be vacuous: {Py.Repr(@base)}");

        var resp = await PollUntil(
            probe: () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "cat-index",
                KeyConditionExpression = "category = :c",
                ExpressionAttributeValues = Item((":c", S("books"))),
            }),
            predicate: r => r.Count == 1,
            timeoutSeconds: 30,
            description: "GSI propagation");
        Ensure(resp.Count == 1, $"expected exactly one item from the GSI query, got {resp.Count}: {Py.Repr(resp.Items)}");
        var item = resp.Items[0];
        Ensure(
            Py.Equal(item.GetValueOrDefault("title"), S("Book1")),
            $"expected the INCLUDEd 'title' attribute to be 'Book1', got {Py.Repr(item.GetValueOrDefault("title"))}");
        Ensure(!item.ContainsKey("secret"), $"a non-projected attribute leaked out of the INCLUDE GSI: {Py.Repr(item)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 60, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutSeconds);
        T? last = default;
        while (clock.Elapsed < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }
}
