// Translated from behavioral/dynamodb/check_scan_filter_legacy_between_binary_attribute_reversed_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-between-binary-attribute-reversed-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyBetweenBinaryAttributeReversedBoundsRejected
{
    private const string Check = "scan-filter-legacy-between-binary-attribute-reversed-bounds-rejected";
    private static readonly byte[] Lo = [0x01];
    private static readonly byte[] Hi = [0x80];

    private static readonly (string? Code, string? Message) Rejected = (
        "ValidationException",
        "One or more parameter values were invalid: The BETWEEN condition was provided a range " +
        "where the lower bound is greater than the upper bound");

    /// <summary>The (Code, Message) <paramref name="fn"/> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static Dictionary<string, Condition> Between(byte[] lo, byte[] hi) => new()
    {
        ["bin"] = new() { ComparisonOperator = ComparisonOperator.BETWEEN, AttributeValueList = [B(lo), B(hi)] },
    };

    /// <summary>
    /// A legacy ScanFilter BETWEEN on a Binary attribute whose lower bound is greater than its
    /// upper bound under unsigned byte order is rejected with ValidationException; the ordered
    /// bounds still match.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-between-binary-attribute-reversed-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbscblr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("id", S("item1")), ("bin", B(Lo))));

        var got = await ErrorCodeAndMessage(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ScanFilter = Between(Hi, Lo),
        }));
        Ensure(
            got == Rejected,
            $"legacy ScanFilter BETWEEN with bounds {Py.Repr(Hi)},{Py.Repr(Lo)} returned {Py.Repr(got)}, expected " +
            $"{Py.Repr(Rejected)}");

        // Positive control: the same table, same attribute, bounds the right way round, succeeds.
        var ok = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            ScanFilter = Between(Lo, Hi),
        });
        var ids = ok.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(["item1"]), $"the correctly ordered BETWEEN returned {Py.Repr(ids)}, expected ['item1']");
    }
}
