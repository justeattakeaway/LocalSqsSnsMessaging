// Translated from behavioral/dynamodb/check_ddbgsi_scan_consistent_read_on_an_index_rejected_while_base_scan_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-scan-consistent-read-on-an-index-rejected-while-base-scan-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiScanConsistentReadOnAnIndexRejectedWhileBaseScanSucceeds
{
    private const string Check = "ddbgsi-scan-consistent-read-on-an-index-rejected-while-base-scan-succeeds";
    private const string ErrValidation = "ValidationException";
    private const string Index = "gsi1";
    private const int VisibleSeconds = 180;

    /// <summary>Return the error code <paramref name="fn"/> raises; raise if the call succeeds.</summary>
    private static async Task<(string? Code, AwsError Error)> ErrorCode(Func<Task<ScanResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail(
                "real AWS refuses a strongly consistent Scan of a global secondary index, but the call " +
                $"SUCCEEDED and returned {Py.Repr(result!.Items)}");
        }

        return (error.Code, error);
    }

    /// <summary>
    /// A GSI Scan with ConsistentRead=True is refused with ValidationException and no data, while
    /// a consistent base Scan sees a just-written item and the index Scan without the flag works.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-scan-consistent-read-on-an-index-rejected-while-base-scan-succeeds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-scancr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string?> indexStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(VisibleSeconds))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            indexStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (indexStatus.GetValueOrDefault(Index) == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            indexStatus.GetValueOrDefault(Index) == "ACTIVE",
            $"{Index} never became ACTIVE within {VisibleSeconds}s: {Py.Repr(indexStatus)}");

        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#1")), ("sk", S("S1")), ("gsi1pk", S("BUCKET#A")), ("body", S("payload"))));

        // Positive control 1: ConsistentRead=True is accepted on the base table, and it really is
        // strongly consistent -- it sees the write it followed with no poll whatsoever. The table is
        // this check's own, so "exactly one item" is a property of the query, not of the account.
        var baseScan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            baseScan.Items.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"the strongly consistent base Scan did not see the write it followed: {Py.Repr(baseScan.Items)}");

        // Positive control 2: the identical index Scan, minus the flag, works once the entry arrives.
        var deadline = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> idxItems = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(VisibleSeconds))
        {
            idxItems = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index })).Items;
            if (idxItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            idxItems.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"the eventually consistent index Scan never returned the item within {VisibleSeconds}s, so " +
            $"the rejection below would not be about consistency: {Py.Repr(idxItems)}");

        // Break exactly one field: the same index Scan, with ConsistentRead=True.
        var (code, response) = await ErrorCode(() => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index, ConsistentRead = true }));
        Ensure(
            code == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} for a strongly consistent Scan of a GSI, got {Py.Repr(code)} " +
            $"({Py.Repr(response.Message)})");
        Ensure(
            response.Message == "Consistent reads are not supported on global secondary indexes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(response.Message)}");
        // Effect absent: the refused Scan carried no data whatsoever -- not an empty page, no page.
        // (The .NET SDK raises the error, so there is no response page to inspect.)

        // And the refusal did not disturb the table: the two accepted reads still answer as before.
        var stillBase = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            stillBase.Items.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"the refused call changed what the base table holds: {Py.Repr(stillBase.Items)}");
        var stillIdx = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index })).Items;
        Ensure(
            stillIdx.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"the refused call disturbed the index contents: {Py.Repr(stillIdx)}");
    }
}
