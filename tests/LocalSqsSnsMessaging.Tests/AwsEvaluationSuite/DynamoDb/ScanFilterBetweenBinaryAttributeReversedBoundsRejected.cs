// Translated from behavioral/dynamodb/check_scan_filter_between_binary_attribute_reversed_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-between-binary-attribute-reversed-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBetweenBinaryAttributeReversedBoundsRejected
{
    private const string Check = "scan-filter-between-binary-attribute-reversed-bounds-rejected";
    private static readonly byte[] Lo = [0x01];
    private static readonly byte[] Hi = [0x80];

    private static readonly (string? Code, string? Message) Rejected = (
        "ValidationException",
        "Invalid FilterExpression: The BETWEEN operator requires upper bound to be greater than or " +
        "equal to lower bound; lower bound operand: AttributeValue: {B:gA==}, upper bound operand: " +
        "AttributeValue: {B:AQ==}");

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

    /// <summary>
    /// A Scan FilterExpression BETWEEN on a Binary attribute with the bounds swapped is
    /// rejected with the exact bytes of both operands, read as unsigned values.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-between-binary-attribute-reversed-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbscbr-");
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
            FilterExpression = "bin BETWEEN :hi AND :lo",
            ExpressionAttributeValues = Item((":hi", B(Hi)), (":lo", B(Lo))),
        }));
        Ensure(got == Rejected, $"reversed BETWEEN bounds {Py.Repr(Hi)},{Py.Repr(Lo)} returned {Py.Repr(got)}, expected {Py.Repr(Rejected)}");

        // Positive control: the same table, same attribute, bounds the right way round, succeeds.
        var ok = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "bin BETWEEN :lo AND :hi",
            ExpressionAttributeValues = Item((":lo", B(Lo)), (":hi", B(Hi))),
        });
        var ids = ok.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(["item1"]), $"the correctly ordered BETWEEN returned {Py.Repr(ids)}, expected ['item1']");
    }
}
