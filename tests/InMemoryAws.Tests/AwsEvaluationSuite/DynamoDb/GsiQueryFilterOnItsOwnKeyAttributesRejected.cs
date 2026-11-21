// Translated from behavioral/dynamodb/check_gsi_query_filter_on_its_own_key_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-filter-on-its-own-key-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryFilterOnItsOwnKeyAttributesRejected
{
    private const string Check = "gsi-query-filter-on-its-own-key-attributes-rejected";
    private const string Gsi = "gsi1";
    private const int VisibleS = 180;

    private static readonly (string Code, string Message) HashKeyInFilter = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: gpk");

    private static readonly (string Code, string Message) RangeKeyInFilter = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: gsk");

    /// <summary>
    /// A GSI Query's FilterExpression naming the index's own hash or range key attribute is
    /// refused with the exact message, while a filter on the base table's sort key still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-filter-on-its-own-key-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-ownkey-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        double Monotonic() => clock.Elapsed.TotalSeconds;
        var deadline = Monotonic() + VisibleS;
        while (true)
        {
            var gsis = (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes ?? [];
            if (gsis.Count > 0 && gsis[0].IndexStatus == IndexStatus.ACTIVE)
            {
                break;
            }

            Ensure(Monotonic() < deadline, $"{Gsi} did not reach ACTIVE, last saw {Py.Repr(gsis.Select(g => g.IndexStatus).ToList())}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("keep")), ("gpk", S("g1")), ("gsk", S("k1"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("skip")), ("gpk", S("g1")), ("gsk", S("k2"))));

        // Positive control: a filter on the base table's sort key -- not a key of this index --
        // is accepted and selects between the two seeded items, once both have propagated.
        deadline = Monotonic() + VisibleS;
        QueryResponse control;
        while (true)
        {
            control = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gpk = :g",
                ExpressionAttributeValues = Item((":g", S("g1"))),
            });
            if (control.Count == 2 || Monotonic() >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(control.Count == 2, $"expected both seeded items visible on {Gsi} within {VisibleS}s, got Count={control.Count}");

        var filtered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gpk = :g",
            FilterExpression = "sk = :keep",
            ExpressionAttributeValues = Item((":g", S("g1")), (":keep", S("keep"))),
        });
        Ensure(
            filtered.Items.Select(i => i["sk"].S).SequenceEqual(["keep"]),
            $"filtering on the base table's sort key returned {Py.Repr(filtered.Items)}, expected only " +
            "the item whose sk is 'keep'");

        // Break exactly one thing: the filter names the queried index's own hash key.
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gpk = :g",
                FilterExpression = "gpk = :g2",
                ExpressionAttributeValues = Item((":g", S("g1")), (":g2", S("g1"))),
            }),
            HashKeyInFilter.Code,
            HashKeyInFilter.Message);

        // Break exactly one thing: the filter names the queried index's own range key.
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gpk = :g",
                FilterExpression = "gsk = :k",
                ExpressionAttributeValues = Item((":g", S("g1")), (":k", S("k1"))),
            }),
            RangeKeyInFilter.Code,
            RangeKeyInFilter.Message);

        // Neither rejected call wrote or deleted anything: the base table still holds only the seed.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(
            final.Select(i => i["sk"].S).Order(StringComparer.Ordinal).SequenceEqual(["keep", "skip"]),
            $"expected only the two seeded items to remain, got {Py.Repr(final)}");
    }

    /// <summary><paramref name="fn"/> is refused with exactly (<paramref name="code"/>, <paramref name="message"/>); an unexpected success fails.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(
            (error.Code, error.Message) == (code, message),
            $"expected {code}: {Py.Repr(message)}, got {error.Code}: {Py.Repr(error.Message)}");
    }
}
