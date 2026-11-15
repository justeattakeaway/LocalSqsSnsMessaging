// Translated from behavioral/dynamodb/check_ddb_frc_cross_type_string_operand_eq_empty_ne_everything.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-frc-cross-type-string-operand-eq-empty-ne-everything</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbFrcCrossTypeStringOperandEqEmptyNeEverything
{
    private const string Check = "ddb-frc-cross-type-string-operand-eq-empty-ne-everything";

    /// <summary>
    /// ===== inlined estate: seed =====
    /// Three items, every one carrying ResolvedAt as a Number. :v's digits ('100') equal item1's value
    /// as text, so a type-aware comparison and a lexicographic one would disagree if AWS compared loosely.
    /// </summary>
    private static Dictionary<string, AttributeValue>[] SeedItems() =>
    [
        Item(("id", S("item1")), ("ResolvedAt", N("100"))),
        Item(("id", S("item2")), ("ResolvedAt", N("200"))),
        Item(("id", S("item3")), ("ResolvedAt", N("300"))),
    ];

    /// <summary>Derived from the seed: every item's id, since every item carries ResolvedAt.</summary>
    private static readonly List<string> All = [.. SeedItems().Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>
    /// ResolvedAt (N) = :s and &lt;&gt; :s with :s an S of the same digits: '=' matches nothing and
    /// '&lt;&gt;' matches every item, because a cross-type AttributeValue is never equal.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-frc-cross-type-string-operand-eq-empty-ne-everything")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        async Task<List<string>> Ids(string expr, AttributeValue value) =>
            IdsOf((await Scan(ddb, name, expr, Item((":v", value)))).Items);

        var sameType = await Ids("ResolvedAt = :v", N("100"));
        Ensure(sameType.SequenceEqual(["item1"]), $"ResolvedAt = 100 (N) returned {PyTuple(sameType)}, expected ('item1',)");

        var crossEq = await Ids("ResolvedAt = :v", S("100"));
        Ensure(
            crossEq.Count == 0,
            $"ResolvedAt (N) = '100' (S) returned {PyTuple(crossEq)}; a cross-type AttributeValue never equals");

        var crossNe = await Ids("ResolvedAt <> :v", S("100"));
        Ensure(
            crossNe.SequenceEqual(All),
            $"ResolvedAt (N) <> '100' (S) returned {PyTuple(crossNe)}, expected every item with ResolvedAt: {PyTuple(All)}");
    }

    /// <summary>A Python tuple's repr: <c>()</c>, <c>('a',)</c>, <c>('a', 'b')</c>.</summary>
    private static string PyTuple(List<string> values) =>
        "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + (values.Count == 1 ? ",)" : ")");

    /// <summary>The table under a minted name: id (S) HASH, on demand.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbfrc-xtype-");
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

    /// <summary>Every page of a strongly consistent Scan: the items plus the summed Count.</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, int Count)> Scan(
        IAmazonDynamoDB ddb, string name, string filterExpression, Dictionary<string, AttributeValue> values)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        var count = 0;
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
            count += page.Count ?? 0;
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return (items, count);
            }
        }
    }

    private static List<string> IdsOf(List<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];
}
