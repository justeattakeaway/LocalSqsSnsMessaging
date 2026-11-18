// Translated from behavioral/dynamodb/check_string_sort_key_orders_by_utf8_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.string-sort-key-orders-by-utf8-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class StringSortKeyOrdersByUtf8Bytes
{
    private const string CheckId = "string-sort-key-orders-by-utf8-bytes";

    // Three sort keys whose UTF-8 byte order and UTF-16 code-unit order disagree. In UTF-8 the leading
    // bytes are EE, EF and F0, so this is the ascending order. Encoded as UTF-16 the third is a
    // surrogate pair beginning D83D, which is numerically BELOW the other two, so a comparison over
    // UTF-16 code units -- the natural string comparison in several runtimes -- sorts it FIRST.
    private const string PrivateUse = "";
    private const string Replacement = "�";
    private const string Supplementary = "\U0001f600";
    private static readonly string[] Utf8Order = [PrivateUse, Replacement, Supplementary];

    /// <summary><c>hex(ord(c))</c>.</summary>
    private static string Hex(string c) => "0x" + char.ConvertToUtf32(c, 0).ToString("x", CultureInfo.InvariantCulture);

    private static List<string> Hexes(IEnumerable<string> chars) => [.. chars.Select(Hex)];

    /// <summary>
    /// A String sort key orders by UTF-8 bytes: a supplementary-plane character sorts above a private-use and a
    /// replacement code point, a reverse Query returns the reverse order, and a BETWEEN range returns the two BMP keys.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.string-sort-key-orders-by-utf8-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-utf8sort-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // Written in an order that is neither the UTF-8 nor the UTF-16 order, so insertion order cannot
        // be mistaken for either answer.
        foreach (var sk in new[] { Supplementary, PrivateUse, Replacement })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("ORD")), ("sk", S(sk))));
        }

        var ascending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("ORD"))),
            ConsistentRead = true,
        });
        var got = ascending.Items.Select(item => item["sk"].S).ToList();
        Ensure(
            got.SequenceEqual(Utf8Order),
            $"a String sort key orders by UTF-8 bytes, so the ascending order of these three code points is {Py.Repr(Hexes(Utf8Order))}; got {Py.Repr(Hexes(got))}. Leading the supplementary character indicates a comparison over UTF-16 code units rather than UTF-8 bytes");

        var descending = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("ORD"))),
            ScanIndexForward = false,
            ConsistentRead = true,
        });
        var reversedGot = descending.Items.Select(item => item["sk"].S).ToList();
        Ensure(
            reversedGot.SequenceEqual(Enumerable.Reverse(Utf8Order)),
            $"ScanIndexForward=False must return exactly the reverse of the ascending order: expected {Py.Repr(Hexes(Enumerable.Reverse(Utf8Order)))}, got {Py.Repr(Hexes(reversedGot))}");

        // A range bounded at the BMP replacement code point excludes the supplementary key above it.
        var bounded = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk <= :hi",
            ExpressionAttributeValues = Item((":p", S("ORD")), (":hi", S(Replacement))),
            ConsistentRead = true,
        });
        var boundedGot = bounded.Items.Select(item => item["sk"].S).ToList();
        Ensure(
            boundedGot.SequenceEqual([PrivateUse, Replacement]),
            $"a sort-key range bounded at {Py.Repr(Hex(Replacement))} must include the two keys at or below it in UTF-8 order and exclude the supplementary one: expected {Py.Repr(new List<string> { Hex(PrivateUse), Hex(Replacement) })}, got {Py.Repr(Hexes(boundedGot))}");
    }
}
