// Translated from behavioral/dynamodb/check_gsi_query_filter_on_its_own_aliased_key_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-filter-on-its-own-aliased-key-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryFilterOnItsOwnAliasedKeyAttributeRejected
{
    private const string Check = "gsi-query-filter-on-its-own-aliased-key-attribute-rejected";
    private const string Gsi = "gsi1";
    private const string Gh = "shared";
    private const int VisibleS = 180;

    private static readonly (string Code, string Message) KeyInFilter = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: gh");

    /// <summary>
    /// A GSI Query's FilterExpression naming the index's own hash key through an
    /// ExpressionAttributeNames alias is refused with a message naming the resolved attribute,
    /// and the table is unchanged; an aliased filter on a non-key attribute still narrows.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-filter-on-its-own-aliased-key-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-aliasedkey-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gh", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gh", KeyType.HASH)],
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

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("s1")), ("gh", S(Gh)), ("note", S("alpha"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p2")), ("sk", S("s2")), ("gh", S(Gh)), ("note", S("beta"))));

        // GSIs are eventually consistent: wait for both items before trusting the control below.
        deadline = Monotonic() + VisibleS;
        QueryResponse unfiltered;
        while (true)
        {
            unfiltered = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gh = :g",
                ExpressionAttributeValues = Item((":g", S(Gh))),
            });
            if (unfiltered.Count == 2 || Monotonic() >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(unfiltered.Count == 2, $"expected both seeded items visible on {Gsi} within {VisibleS}s, got Count={unfiltered.Count}");

        // Positive control: the alias mechanism itself narrows correctly on a non-key attribute.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            KeyConditionExpression = "gh = :g",
            FilterExpression = "#n = :want",
            ExpressionAttributeNames = Names(("#n", "note")),
            ExpressionAttributeValues = Item((":g", S(Gh)), (":want", S("alpha"))),
        });
        Ensure(
            control.Items.Select(i => i["note"].S).SequenceEqual(["alpha"]),
            $"aliased filter on the non-key attribute returned {Py.Repr(control.Items)}, " +
            "expected only the item whose note is 'alpha'");

        // Break exactly one thing: the filter names the queried index's own hash key, but through
        // an alias instead of the bare attribute name.
        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Gsi,
                KeyConditionExpression = "gh = :g",
                FilterExpression = "#g2 = :g2",
                ExpressionAttributeNames = Names(("#g2", "gh")),
                ExpressionAttributeValues = Item((":g", S(Gh)), (":g2", S(Gh))),
            }),
            KeyInFilter.Code,
            KeyInFilter.Message);

        // The rejected call wrote or deleted nothing: the base table still holds only the seed.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        Ensure(
            final.Select(i => i["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(["p1", "p2"]),
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
