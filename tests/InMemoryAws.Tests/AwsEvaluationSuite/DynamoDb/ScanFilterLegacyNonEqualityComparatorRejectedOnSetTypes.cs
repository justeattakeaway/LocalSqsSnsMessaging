// Translated from behavioral/dynamodb/check_scan_filter_legacy_non_equality_comparator_rejected_on_set_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-non-equality-comparator-rejected-on-set-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyNonEqualityComparatorRejectedOnSetTypes
{
    private const string Check = "scan-filter-legacy-non-equality-comparator-rejected-on-set-types";

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Legacy ScanFilter BEGINS_WITH on SS, IN on NS and BETWEEN on BS are each
    /// ValidationException naming the operator and the set type; EQ on the SS attribute matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-non-equality-comparator-rejected-on-set-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-setop-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")),
            ("ssattr", SS("a", "b")),
            ("nsattr", NS("1", "2")),
            ("bsattr", BS("x"u8.ToArray(), "y"u8.ToArray()))));

        var cases = new (string Attr, string TypeName, string Op, Func<List<AttributeValue>> Values)[]
        {
            ("ssattr", "SS", "BEGINS_WITH", () => [SS("a")]),
            ("nsattr", "NS", "IN", () => [NS("1"), NS("2")]),
            ("bsattr", "BS", "BETWEEN", () => [BS("a"u8.ToArray()), BS("z"u8.ToArray())]),
        };

        foreach (var (attr, typeName, op, values) in cases)
        {
            var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ScanFilter = new Dictionary<string, Condition>
                {
                    [attr] = new() { ComparisonOperator = op, AttributeValueList = values() },
                },
            }));
            Ensure(code == "ValidationException", $"{attr}/{op}: expected ValidationException, got {code}: {Py.Repr(message)}");
            var want = $"One or more parameter values were invalid: ComparisonOperator {op} is not valid for {typeName} AttributeValue type";
            Ensure(message == want, $"{attr}/{op}: expected {Py.Repr(want)}, got {Py.Repr(message)}");
        }

        // Positive control: EQ, the operator the set types do accept, still matches each attribute.
        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            ScanFilter = new Dictionary<string, Condition>
            {
                ["ssattr"] = new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [SS("b", "a")] },
            },
        });
        Ensure(resp.Count == 1, $"positive control: EQ on ssattr expected 1 match, got Count={resp.Count}");
    }
}
