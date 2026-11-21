// Translated from behavioral/dynamodb/check_scan_filter_between_dangling_and_conjunct_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-between-dangling-and-conjunct-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBetweenDanglingAndConjunctRejected
{
    private const string Check = "scan-filter-between-dangling-and-conjunct-rejected";

    private static Dictionary<string, AttributeValue>[] SeedItems() =>
    [
        Item(("id", S("item1")), ("price", N("15"))),
        Item(("id", S("item2")), ("price", N("25"))),
    ];

    private static Dictionary<string, AttributeValue> Values() => Item((":lo", N("10")), (":hi", N("20")));

    /// <summary>Derived from the seed: the control's own predicate selects only item1.</summary>
    private static readonly List<string> Match =
        [.. SeedItems().Where(i => int.Parse(i["price"].N, CultureInfo.InvariantCulture) is >= 10 and <= 20).Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A Scan FilterExpression ending in a dangling AND right after a BETWEEN clause, with no
    /// clause following it, is refused ValidationException rather than treated as the bare BETWEEN.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-between-dangling-and-conjunct-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        var control = await Scan(ddb, name, "price BETWEEN :lo AND :hi", Values());
        Ensure(
            Ids(control).SequenceEqual(Match),
            $"the control 'price BETWEEN :lo AND :hi' returned {PyTuple(Ids(control))}, expected {PyTuple(Match)}");

        var (code, message) = await ErrorCode(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = "price BETWEEN :lo AND :hi AND",
            ExpressionAttributeValues = Values(),
            ConsistentRead = true,
        }));
        Ensure(
            code == "ValidationException",
            $"the dangling trailing AND was refused {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expectedMessage = "Invalid FilterExpression: Syntax error; token: \"<EOF>\", near: \"AND\"";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var again = await Scan(ddb, name, "price BETWEEN :lo AND :hi", Values());
        Ensure(
            Ids(again).SequenceEqual(Match),
            $"after the refused Scan, the well-formed BETWEEN returned {PyTuple(Ids(again))}, expected {PyTuple(Match)} unchanged");
    }

    /// <summary>A Python tuple's repr: <c>()</c>, <c>('a',)</c>, <c>('a', 'b')</c>.</summary>
    private static string PyTuple(List<string> values) =>
        "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + (values.Count == 1 ? ",)" : ")");

    /// <summary>The table under a minted name: id (S) HASH, on demand.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbbtwd-");
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

    /// <summary>Every page of a strongly consistent Scan: the items.</summary>
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

    /// <summary>(code, message) of the error fn() raises. Raises when the call SUCCEEDS: real AWS rejects it.</summary>
    private static async Task<(string? Code, string Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail("the call succeeded; real AWS rejects it, so there is no error code");
        }

        return (error.Code, error.Message ?? "");
    }
}
