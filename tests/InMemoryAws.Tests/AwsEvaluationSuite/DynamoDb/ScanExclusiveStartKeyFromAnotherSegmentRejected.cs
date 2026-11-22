// Translated from behavioral/dynamodb/check_scan_exclusive_start_key_from_another_segment_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-exclusive-start-key-from-another-segment-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanExclusiveStartKeyFromAnotherSegmentRejected
{
    private const string Check = "scan-exclusive-start-key-from-another-segment-rejected";
    private const int TotalSegments = 2;
    private const int DonorSegment = 0;
    private const int OtherSegment = 1;
    private const int ItemCount = 60;

    /// <summary>The (code, message) <paramref name="fn"/> raises. An unexpected success raises too, naming the result.</summary>
    private static async Task<(string? Code, string Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>
    /// A parallel Scan's ExclusiveStartKey must pair with the Segment it was returned under;
    /// resuming it under a different Segment of the same TotalSegments is refused.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-exclusive-start-key-from-another-segment-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var items = Enumerable.Range(0, ItemCount).Select(i => Item(("pk", S($"item-{i:D3}")))).ToList();
        for (var start = 0; start < items.Count; start += 25)
        {
            var batch = items.Skip(start).Take(25);
            var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
                {
                    [t] = [.. batch.Select(it => new WriteRequest { PutRequest = new PutRequest { Item = it } })],
                },
            });
            var unprocessed = resp.UnprocessedItems ?? [];
            Ensure(unprocessed.Count == 0, $"BatchWriteItem left {Py.Repr(unprocessed)} unprocessed while seeding");
        }

        // Positive control: every written item is visible to an unsegmented, consistent scan.
        var whole = await ddb.ScanAsync(new ScanRequest { TableName = t, Select = Select.COUNT, ConsistentRead = true });
        Ensure(whole.Count == ItemCount, $"an unsegmented Scan counted {Py.Repr(whole.Count)}, expected the {Py.Repr(ItemCount)} seeded items");

        // The donor segment read whole, in one page: the reference for the Limit=1 page and its resume.
        var donorAll = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            Segment = DonorSegment,
            TotalSegments = TotalSegments,
            ConsistentRead = true,
        });
        Ensure(
            donorAll.LastEvaluatedKey is null,
            $"segment {Py.Repr(DonorSegment)} of {ItemCount} small items must fit on one page, got LastEvaluatedKey {Py.Repr(donorAll.LastEvaluatedKey)}");
        var donorItems = donorAll.Items;

        var donorPage = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            Segment = DonorSegment,
            TotalSegments = TotalSegments,
            Limit = 1,
            ConsistentRead = true,
        });
        var donorFirst = donorItems.Take(1).ToList();
        Ensure(
            Py.Equal(donorPage.Items, donorFirst),
            $"a Limit=1 Scan of segment {Py.Repr(DonorSegment)} returned {Py.Repr(donorPage.Items)}, expected the segment's first item {Py.Repr(donorFirst)}");
        var lek = donorPage.LastEvaluatedKey;
        Ensure(
            Py.Equal(lek, donorItems[0]),
            $"the Limit=1 page's LastEvaluatedKey {Py.Repr(lek)} must name its own item {Py.Repr(donorItems[0])}");

        var (code, message) = await ErrorCode(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            Segment = OtherSegment,
            TotalSegments = TotalSegments,
            ExclusiveStartKey = lek,
            ConsistentRead = true,
        }));
        var expectedMessage =
            "The provided starting key is invalid: Invalid ExclusiveStartKey. Please use " +
            $"ExclusiveStartKey with correct Segment. TotalSegments: {TotalSegments} " +
            $"Segment: {OtherSegment}";
        Ensure(
            code == "ValidationException" && message == expectedMessage,
            $"resuming segment {Py.Repr(DonorSegment)}'s key under segment {Py.Repr(OtherSegment)} raised {Py.Repr((code, message))}, expected {Py.Repr(("ValidationException", expectedMessage))}");

        // Positive control: the SAME key resumes cleanly under the segment it was returned under.
        var resumed = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            Segment = DonorSegment,
            TotalSegments = TotalSegments,
            ExclusiveStartKey = lek,
            ConsistentRead = true,
        });
        var donorRest = donorItems.Skip(1).ToList();
        Ensure(
            Py.Equal(resumed.Items, donorRest) && resumed.LastEvaluatedKey is null,
            $"resuming segment {Py.Repr(DonorSegment)}'s own key under its own segment must return the rest of that segment {Py.Repr(donorRest)}, got Items={Py.Repr(resumed.Items)} LastEvaluatedKey={Py.Repr(resumed.LastEvaluatedKey)}");
    }
}
