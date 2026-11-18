// Translated from behavioral/dynamodb/check_ddbgsi_query_filter_on_a_non_projected_attribute_rejected_while_scan_matches_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-query-filter-on-a-non-projected-attribute-rejected-while-scan-matches-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiQueryFilterOnANonProjectedAttributeRejectedWhileScanMatchesNothing
{
    private const string Check = "ddbgsi-query-filter-on-a-non-projected-attribute-rejected-while-scan-matches-nothing";
    private const string ErrValidation = "ValidationException";
    private const string Index = "gsi1";
    private const string IndexLsi = "lsi1";
    private const int VisibleSeconds = 180;

    /// <summary>Return (error code, error) for the error <paramref name="fn"/> raises; raise if the call succeeds.</summary>
    private static async Task<(string? Code, AwsError Error)> ErrorCode(Func<Task<QueryResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this Query, but it SUCCEEDED and returned {Py.Repr(result!.Items)}");
        }

        return (error.Code, error);
    }

    /// <summary>
    /// A GSI Query filtering on a non-projected attribute is refused; the same filter on a GSI Scan
    /// is accepted and matches nothing. Controls: the projected-attribute filter and a base Scan match.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-query-filter-on-a-non-projected-attribute-rejected-while-scan-matches-nothing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-filt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept"] },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = IndexLsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept"] },
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

        await ddb.PutItemAsync(table, Item(
            ("pk", S("ITEM#1")),
            ("sk", S("S1")),
            ("gsi1pk", S("BUCKET#A")),
            ("lsk", S("L1")),
            ("kept", S("projected-value")),
            ("dropped", S("excluded-value"))));

        Task<QueryResponse> QueryIndex(string filterExpr, (string Name, AttributeValue Value) value) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi1pk = :g",
            FilterExpression = filterExpr,
            ExpressionAttributeValues = Item((":g", S("BUCKET#A")), value),
        });

        // Positive control 1: the same filter shape over a PROJECTED attribute returns the item from
        // the index, once the entry has arrived. Until then the rejection below proves nothing.
        var deadline = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> keptItems = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(VisibleSeconds))
        {
            keptItems = (await QueryIndex("kept = :k", (":k", S("projected-value")))).Items;
            if (keptItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            keptItems.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"a filter over the PROJECTED attribute never returned the item within {VisibleSeconds}s, so " +
            $"the refusal below would not be about the projection: {Py.Repr(keptItems)}");
        Ensure(
            !keptItems[0].ContainsKey("dropped"),
            "the non-projected attribute is present in the index entry, so nothing below is about a " +
            $"projection at all: {Py.Repr(keptItems[0])}");

        // Break exactly one thing: the filter names the NON-projected attribute instead.
        var (code, response) = await ErrorCode(() => QueryIndex("dropped = :d", (":d", S("excluded-value"))));
        Ensure(
            code == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} for a Query filter naming an attribute {Index} does not " +
            $"project, got {Py.Repr(code)} ({Py.Repr(response.Message)})");
        Ensure(
            response.Message == "One or more parameter values were invalid: Secondary index gsi1 does not project one or more filter attributes: [dropped]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(response.Message)}");
        // Effect absent: the refused Query carried no page of data at all, not an empty one. (The
        // .NET SDK raises the error, so there is no response page to inspect.)

        // The asymmetry: Scan accepts the identical filter and matches nothing, having really
        // looked -- ScannedCount counts the index entry it examined and rejected.
        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            IndexName = Index,
            FilterExpression = "dropped = :d",
            ExpressionAttributeValues = Item((":d", S("excluded-value"))),
        });
        Ensure(
            scan.Items is not null && scan.Items.Count == 0,
            $"the index Scan matched on an attribute the index does not project: {Py.Repr(scan.Items)}");
        Ensure(
            scan.ScannedCount == 1,
            "the index Scan did not examine the one index entry, so its empty result says nothing " +
            $"about the filter: ScannedCount={Py.Repr(scan.ScannedCount)}");

        // Positive control 2: the same filter over the same attribute DOES match on the base table,
        // strongly consistently -- so the empty index result above is the projection at work and not a
        // value that never existed.
        var baseScan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "dropped = :d",
            ConsistentRead = true,
            ExpressionAttributeValues = Item((":d", S("excluded-value"))),
        });
        Ensure(
            baseScan.Items.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            "the base-table Scan with the identical filter did not match, so the empty index result " +
            $"is not attributable to the projection: {Py.Repr(baseScan.Items)}");
        Ensure(
            baseScan.Items[0]["dropped"].S == "excluded-value",
            $"the base item does not carry the filtered value: {Py.Repr(baseScan.Items[0])}");

        // LSI arm: the identical refusal on a LOCAL secondary index, where an LSI Query can read
        // strongly consistent -- no poll for propagation is needed, unlike the GSI arm above.
        Task<QueryResponse> QueryLsi(string filterExpr, (string Name, AttributeValue Value) value) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = IndexLsi,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            FilterExpression = filterExpr,
            ExpressionAttributeValues = Item((":p", S("ITEM#1")), value),
        });

        // Positive control: the same filter shape over the PROJECTED attribute matches on the LSI.
        var lsiKept = (await QueryLsi("kept = :k", (":k", S("projected-value")))).Items;
        Ensure(
            lsiKept.Select(i => i["pk"].S).SequenceEqual(["ITEM#1"]),
            $"a filter over the PROJECTED attribute did not match on {IndexLsi}: {Py.Repr(lsiKept)}");
        Ensure(
            !lsiKept[0].ContainsKey("dropped"),
            "the non-projected attribute is present in the LSI entry, so nothing below is about a " +
            $"projection at all: {Py.Repr(lsiKept[0])}");

        // Break exactly one thing: the filter names the NON-projected attribute on the LSI.
        var (lsiCode, lsiResponse) = await ErrorCode(() => QueryLsi("dropped = :d", (":d", S("excluded-value"))));
        Ensure(
            lsiCode == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} for a Query filter naming an attribute {IndexLsi} does " +
            $"not project, got {Py.Repr(lsiCode)} ({Py.Repr(lsiResponse.Message)})");
        Ensure(
            lsiResponse.Message ==
                "One or more parameter values were invalid: Secondary index lsi1 does not project one " +
                "or more filter attributes: [dropped]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(lsiResponse.Message)}");
        // The refused LSI Query carried no page either (the SDK raises; there is no page to inspect).
    }
}
