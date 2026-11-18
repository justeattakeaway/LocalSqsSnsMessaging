// Translated from behavioral/dynamodb/check_ddbgsi_index_gains_no_entry_when_a_conditional_write_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-index-gains-no-entry-when-a-conditional-write-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiIndexGainsNoEntryWhenAConditionalWriteIsRejected
{
    private const string Check = "ddbgsi-index-gains-no-entry-when-a-conditional-write-is-rejected";
    private const string ErrCondition = "ConditionalCheckFailedException";
    private const string Index = "gsi1";
    private const int ConvergeSeconds = 180;

    /// <summary>Return (error code, error) for the error <paramref name="fn"/> raises; raise if the call succeeds.</summary>
    private static async Task<(string? Code, AwsError Error)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this conditional write, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error);
    }

    /// <summary>
    /// A rejected conditional write adds no GSI entry, while an accepted one moves the item's entry.
    /// A later barrier write reaching the index proves the refused key stayed empty, not unprocessed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-index-gains-no-entry-when-a-conditional-write-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-cond-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
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
        while (clock.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
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
            $"{Index} never became ACTIVE within {ConvergeSeconds}s: {Py.Repr(indexStatus)}");

        Task<QueryResponse> IndexQuery(string bucket) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S(bucket))),
        });

        async Task<List<Dictionary<string, AttributeValue>>> Converge(string bucket, List<string> wantPks, string what)
        {
            List<Dictionary<string, AttributeValue>> items = [];
            var giveUp = FidelityTime.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
            {
                items = (await IndexQuery(bucket)).Items;
                if (items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(wantPks))
                {
                    return items;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            throw Fail(
                $"{what}: the index never converged on {Py.Repr(wantPks)} under {Py.Repr(bucket)} within " +
                $"{ConvergeSeconds}s; the last read held {Py.Repr(items)}");
        }

        var subject = Item(("pk", S("ITEM#SUBJECT")));
        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#SUBJECT")), ("gsi1pk", S("BUCKET#ORIGINAL")), ("body", S("original"))));
        await Converge("BUCKET#ORIGINAL", ["ITEM#SUBJECT"], what: "the initial write");

        // Break exactly one thing: the item now exists, so attribute_not_exists(pk) cannot hold.
        var (code, response) = await ErrorCode(() => ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("ITEM#SUBJECT")), ("gsi1pk", S("BUCKET#REFUSED")), ("body", S("refused"))),
            ConditionExpression = "attribute_not_exists(pk)",
        }));
        Ensure(
            code == ErrCondition,
            $"expected {Py.Repr(ErrCondition)} for a PutItem whose attribute_not_exists condition cannot " +
            $"hold, got {Py.Repr(code)} ({Py.Repr(response.Message)})");
        Ensure(
            response.Message == "The conditional request failed",
            $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(response.Message)}");

        // Effect absent on the base table: the refused write replaced nothing.
        var baseItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = subject, ConsistentRead = true })).Item;
        Ensure(
            baseItem["gsi1pk"].S == "BUCKET#ORIGINAL" && baseItem["body"].S == "original",
            $"the refused conditional write mutated the base item anyway: {Py.Repr(baseItem)}");

        // Barrier: a write issued AFTER the refusal whose index entry we wait for. Once it has
        // arrived the index has processed work queued behind the refusal, so the emptiness asserted
        // next is a property of the index's contents and not of how soon we looked.
        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#BARRIER")), ("gsi1pk", S("BUCKET#BARRIER")), ("body", S("barrier"))));
        await Converge("BUCKET#BARRIER", ["ITEM#BARRIER"], what: "the barrier write");

        var refusedEntries = await IndexQuery("BUCKET#REFUSED");
        Ensure(
            refusedEntries.Items is not null && refusedEntries.Items.Count == 0,
            "the refused conditional write still produced an index entry under its would-be index " +
            $"key: {Py.Repr(refusedEntries.Items)}");
        var stillOriginal = (await IndexQuery("BUCKET#ORIGINAL")).Items;
        Ensure(
            stillOriginal.Select(i => i["pk"].S).SequenceEqual(["ITEM#SUBJECT"]),
            $"the refused write disturbed the index entry that did exist: {Py.Repr(stillOriginal)}");

        // Positive control: the SAME conditional-write mechanism, with a condition that holds, does
        // reach the index -- so the empty read above is not "conditional writes never index".
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = subject,
            UpdateExpression = "SET gsi1pk = :g, body = :b",
            ConditionExpression = "attribute_exists(pk)",
            ExpressionAttributeValues = Item((":g", S("BUCKET#MOVED")), (":b", S("moved"))),
        });
        var moved = await Converge("BUCKET#MOVED", ["ITEM#SUBJECT"], what: "the accepted conditional write");
        Ensure(
            moved[0]["body"].S == "moved",
            $"the accepted conditional write reached the index with the wrong payload: {Py.Repr(moved)}");
        await Converge("BUCKET#ORIGINAL", [], what: "the vacated old index key");

        // Index-wide: exactly two entries exist, under the barrier key and the moved key. The refused
        // key is absent from the index's own contents, not merely from one targeted query.
        var indexWide = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Index });
        var keys = indexWide.Items.Select(i => i["gsi1pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keys.SequenceEqual(["BUCKET#BARRIER", "BUCKET#MOVED"]),
            $"the index holds the wrong set of entries: {Py.Repr(keys)}");
    }
}
