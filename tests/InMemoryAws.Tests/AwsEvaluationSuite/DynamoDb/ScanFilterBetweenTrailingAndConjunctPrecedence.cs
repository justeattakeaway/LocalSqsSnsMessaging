// Translated from behavioral/dynamodb/check_scan_filter_between_trailing_and_conjunct_precedence.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-between-trailing-and-conjunct-precedence</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBetweenTrailingAndConjunctPrecedence
{
    private const string Check = "scan-filter-between-trailing-and-conjunct-precedence";
    private const string Lo = "10";
    private const string Hi = "20";

    /// <summary>
    /// Five items under an <c>id</c> (S) hash key: <c>price</c> (N) and <c>active</c> (BOOL) vary independently of
    /// <c>category</c> (S), so each filter below isolates one clause's contribution to the match set.
    /// </summary>
    private static Dictionary<string, AttributeValue>[] SeedItems() =>
    [
        Item(("id", S("item1")), ("price", N("15")), ("active", Bool(true)), ("category", S("A"))),
        Item(("id", S("item2")), ("price", N("15")), ("active", Bool(false)), ("category", S("A"))),
        Item(("id", S("item3")), ("price", N("5")), ("active", Bool(true)), ("category", S("A"))),
        Item(("id", S("item4")), ("price", N("25")), ("active", Bool(true)), ("category", S("A"))),
        Item(("id", S("item5")), ("price", N("15")), ("active", Bool(true)), ("category", S("B"))),
    ];

    private static Dictionary<string, AttributeValue> BetweenValues() =>
        Item((":lo", N(Lo)), (":hi", N(Hi)), (":true", Bool(true)));

    private static Dictionary<string, AttributeValue> BetweenAndCategoryValues()
    {
        var values = BetweenValues();
        values[":catA"] = S("A");
        return values;
    }

    private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>Derived from the seed: price in [10, 20] AND active = true selects item1 and item5.</summary>
    private static readonly List<string> BetweenAndActive =
    [
        .. SeedItems()
            .Where(i => Int(Lo) <= Int(i["price"].N) && Int(i["price"].N) <= Int(Hi) && i["active"].BOOL == true)
            .Select(i => i["id"].S)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The same two, further narrowed to category A: only item1.</summary>
    private static readonly List<string> BetweenAndActiveAndCategoryA =
    [
        .. SeedItems()
            .Where(i => BetweenAndActive.Contains(i["id"].S) && i["category"].S == "A")
            .Select(i => i["id"].S)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// A Scan FilterExpression's BETWEEN clause, followed by a trailing AND conjunct, parses
    /// as (BETWEEN) AND (conjunct): the conjunct narrows the match set the same way regardless of
    /// which side of BETWEEN it sits on, and a further AND clause chains onto it the same way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-between-trailing-and-conjunct-precedence")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        var trailing = await Scan(ddb, name, "price BETWEEN :lo AND :hi AND active = :true", BetweenValues());
        Ensure(
            Ids(trailing).SequenceEqual(BetweenAndActive),
            $"'price BETWEEN :lo AND :hi AND active = :true' returned {PyTuple(Ids(trailing))}, expected {PyTuple(BetweenAndActive)}");

        var leading = await Scan(ddb, name, "active = :true AND price BETWEEN :lo AND :hi", BetweenValues());
        Ensure(
            Ids(leading).SequenceEqual(BetweenAndActive),
            $"'active = :true AND price BETWEEN :lo AND :hi' returned {PyTuple(Ids(leading))}, expected {PyTuple(BetweenAndActive)} (same set as the conjunct trailing BETWEEN)");

        var chained = await Scan(
            ddb,
            name,
            "price BETWEEN :lo AND :hi AND active = :true AND category = :catA",
            BetweenAndCategoryValues());
        Ensure(
            Ids(chained).SequenceEqual(BetweenAndActiveAndCategoryA),
            $"'price BETWEEN :lo AND :hi AND active = :true AND category = :catA' returned {PyTuple(Ids(chained))}, expected {PyTuple(BetweenAndActiveAndCategoryA)}");
    }

    /// <summary>A Python tuple's repr: <c>()</c>, <c>('a',)</c>, <c>('a', 'b')</c>.</summary>
    private static string PyTuple(List<string> values) =>
        "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + (values.Count == 1 ? ",)" : ")");

    /// <summary>The table under a minted name: id (S) HASH, on demand.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbbtwc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>Seeds the table with ONE BatchWriteItem.</summary>
    private static async Task Seed(FidelityContext ctx, string name)
    {
        var resp = await ctx.Dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [name] = [.. SeedItems().Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } })],
            },
        });
        Ensure(
            resp.UnprocessedItems is not null && resp.UnprocessedItems.Count == 0,
            $"BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed while seeding");
    }

    /// <summary>
    /// Every page of a strongly consistent Scan: the items.
    ///
    /// Paginated, because Count and ScannedCount are PER PAGE; strongly consistent, because an
    /// eventually consistent read straight after the seeding may legitimately come back short.
    /// </summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> Scan(
        IAmazonDynamoDB ddb, string name, string filterExpression, Dictionary<string, AttributeValue> values)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items ?? []);
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return items;
            }
        }
    }

    private static List<string> Ids(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];
}
