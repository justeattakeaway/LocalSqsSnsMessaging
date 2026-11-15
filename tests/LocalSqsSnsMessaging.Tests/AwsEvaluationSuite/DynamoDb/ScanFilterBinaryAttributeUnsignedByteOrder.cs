// Translated from behavioral/dynamodb/check_scan_filter_binary_attribute_unsigned_byte_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-binary-attribute-unsigned-byte-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBinaryAttributeUnsignedByteOrder
{
    private const string Check = "scan-filter-binary-attribute-unsigned-byte-order";

    /// <summary>
    /// Four one-byte values spanning the signed/unsigned boundary, plus a value that is a byte-for-byte
    /// prefix of another. Every expected set below is derived from these at run time.
    /// </summary>
    private static readonly (string Id, byte[] Value)[] Items =
    [
        ("v01", [0x01]),
        ("v7f", [0x7f]),
        ("v80", [0x80]),
        ("v80ext", [0x80, 0x00]),
        ("vff", [0xff]),
    ];

    private static Dictionary<string, Condition> Legacy(ComparisonOperator op, byte[] value) =>
        new(StringComparer.Ordinal) { ["bin"] = new Condition { ComparisonOperator = op, AttributeValueList = [B(value)] } };

    /// <summary>
    /// ScanFilter and FilterExpression compare a Binary attribute as unsigned bytes for EQ, GT,
    /// BEGINS_WITH and CONTAINS -- 0x80 and 0xff sort above 0x01 and 0x7f, not below.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-binary-attribute-unsigned-byte-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scanbin-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 30);
        foreach (var (itemId, value) in Items)
        {
            await ddb.PutItemAsync(table, Item(("id", S(itemId)), ("bin", B(value))));
        }

        async Task<List<string>> ScanIds(Dictionary<string, Condition>? scanFilter = null, string? filterExpression = null, Dictionary<string, AttributeValue>? values = null)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ScanFilter = scanFilter,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
            });
            return [.. resp.Items.Select(item => item["id"].S).Order(StringComparer.Ordinal)];
        }

        string[] greaterThan7f = ["v80", "v80ext", "vff"];
        var gt = await ScanIds(scanFilter: Legacy(ComparisonOperator.GT, [0x7f]));
        Ensure(
            gt.ToHashSet(StringComparer.Ordinal).SetEquals(greaterThan7f),
            $"ScanFilter GT 0x7f returned {Py.Repr(gt)}; unsigned order puts 0x80, 0x80-0x00 and 0xff above 0x7f, not below");
        var gtExpr = await ScanIds(filterExpression: "bin > :v", values: Item((":v", B([0x7f]))));
        Ensure(
            gtExpr.ToHashSet(StringComparer.Ordinal).SetEquals(greaterThan7f),
            $"FilterExpression 'bin > :v' returned {Py.Repr(gtExpr)}, must match legacy GT's {Py.Repr(gt)}");
        var eq = await ScanIds(scanFilter: Legacy(ComparisonOperator.EQ, [0x80]));
        Ensure(eq.SequenceEqual(["v80"]), $"ScanFilter EQ 0x80 returned {Py.Repr(eq)}, expected only the exact match");
        var beginsWith = await ScanIds(scanFilter: Legacy(ComparisonOperator.BEGINS_WITH, [0x80]));
        Ensure(
            beginsWith.ToHashSet(StringComparer.Ordinal).SetEquals(["v80", "v80ext"]),
            $"ScanFilter BEGINS_WITH 0x80 returned {Py.Repr(beginsWith)}, expected the exact byte and its extension, not the unrelated values");
        var contains = await ScanIds(scanFilter: Legacy(ComparisonOperator.CONTAINS, [0x00]));
        Ensure(contains.SequenceEqual(["v80ext"]), $"ScanFilter CONTAINS 0x00 returned {Py.Repr(contains)}, expected only the value holding it as a subsequence");
        var containsExpr = await ScanIds(filterExpression: "contains(bin, :v)", values: Item((":v", B([0x00]))));
        Ensure(
            containsExpr.SequenceEqual(contains),
            $"FilterExpression 'contains(bin, :v)' returned {Py.Repr(containsExpr)}, must match legacy CONTAINS's {Py.Repr(contains)}");
    }
}
