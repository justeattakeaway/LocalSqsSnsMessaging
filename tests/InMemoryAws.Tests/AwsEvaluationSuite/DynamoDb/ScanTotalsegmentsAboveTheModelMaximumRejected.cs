// Translated from behavioral/dynamodb/check_scan_totalsegments_above_the_model_maximum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-totalsegments-above-the-model-maximum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanTotalsegmentsAboveTheModelMaximumRejected
{
    private const string Check = "scan-totalsegments-above-the-model-maximum-rejected";

    private static readonly (string Code, string Message) AboveMax = (
        "ValidationException",
        "1 validation error detected: Value at 'TotalSegments' failed to satisfy constraint: " +
        "Member must have value less than or equal to 1000000");

    // Fields, not literals: the SDK's analyzers reject a literal outside the model's range.
    private static readonly int AtMaximum = 1_000_000;
    private static readonly int AboveMaximum = 1_000_001;

    /// <summary>
    /// The error for a call real AWS refuses. Raises on unexpected success, so accepting invalid
    /// input fails distinctly.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this Scan, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return error;
    }

    /// <summary>
    /// A Scan's TotalSegments above its maximum of 1,000,000 is refused with ValidationException,
    /// TotalSegments at exactly 1,000,000 is accepted, and the refusal changes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-totalsegments-above-the-model-maximum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-tsmax-");
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

        await ddb.PutItemAsync(table, Item(("pk", S("anchor"))));

        // --- positive control: the maximum itself (1,000,000) is accepted ---
        var atMax = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            TotalSegments = AtMaximum,
            Segment = 0,
            ConsistentRead = true,
        });
        Ensure(
            atMax.ScannedCount == 0,
            "TotalSegments=1000000 Segment=0 of a 1-item table must be accepted; got " +
            $"{Py.Repr(atMax.ScannedCount)} scanned");

        // --- the refusal: one past the maximum ---
        var err = await ClientError(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            TotalSegments = AboveMaximum,
            Segment = 0,
            ConsistentRead = true,
        }));
        Ensure(
            err.Code == AboveMax.Code,
            $"TotalSegments=1000001 must be refused with {Py.Repr(AboveMax.Code)}; got " +
            $"{Py.Repr(err.Code)} / {Py.Repr(err.Message)}");
        Ensure(err.Message == AboveMax.Message, $"expected message {Py.Repr(AboveMax.Message)}, got {Py.Repr(err.Message)}");

        // --- effect-absent: the refused call created and deleted nothing ---
        var after = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            after.Count == 1 && Py.Equal(after.Items, new[] { Item(("pk", S("anchor"))) }),
            "the refused Scan must not change the table; an unsegmented Scan now returns " +
            $"{Py.Repr(after.Items)}, expected only the anchor item");
    }
}
