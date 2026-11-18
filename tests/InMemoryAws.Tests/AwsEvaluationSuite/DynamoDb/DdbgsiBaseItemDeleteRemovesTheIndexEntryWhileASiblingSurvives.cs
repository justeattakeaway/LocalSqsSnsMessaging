// Translated from behavioral/dynamodb/check_ddbgsi_base_item_delete_removes_the_index_entry_while_a_sibling_survives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-base-item-delete-removes-the-index-entry-while-a-sibling-survives</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiBaseItemDeleteRemovesTheIndexEntryWhileASiblingSurvives
{
    private const string Check = "ddbgsi-base-item-delete-removes-the-index-entry-while-a-sibling-survives";
    private const string Index = "gsi1";
    private const int ConvergeSeconds = 180;

    /// <summary>
    /// Deleting a base item withdraws its GSI entry while a same-key sibling survives.
    /// The index converges on exactly the survivor (ScannedCount 1); consistent base reads agree.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-base-item-delete-removes-the-index-entry-while-a-sibling-survives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-del-");
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

        var doomed = Item(("pk", S("ITEM#DOOMED")), ("sk", S("S1")));
        var sibling = Item(("pk", S("ITEM#SIBLING")), ("sk", S("S1")));
        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#DOOMED")), ("sk", S("S1")), ("gsi1pk", S("BUCKET#A")), ("body", S("doomed-payload"))));
        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#SIBLING")), ("sk", S("S1")), ("gsi1pk", S("BUCKET#A")), ("body", S("sibling-payload"))));

        Task<QueryResponse> IndexRead() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("BUCKET#A"))),
        });

        // GSI propagation is eventual with no documented bound, so poll for the key set to a deadline.
        async Task<QueryResponse> Converge(List<string> want, string what)
        {
            var resp = new QueryResponse { Items = [], ScannedCount = 0 };
            var giveUp = FidelityTime.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
            {
                resp = await IndexRead();
                if (resp.Items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).SequenceEqual(want))
                {
                    return resp;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            throw Fail(
                $"{what}: the index never converged on {Py.Repr(want)} within {ConvergeSeconds}s; the last read " +
                $"held {Py.Repr(resp.Items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList())}");
        }

        // Effect present: both items are materialised into the index under the shared index key.
        var both = await Converge(["ITEM#DOOMED", "ITEM#SIBLING"], what: "the two writes");
        Ensure(
            both.ScannedCount == 2,
            $"the index read examined {Py.Repr(both.ScannedCount)} entries, not the 2 written");

        await ddb.DeleteItemAsync(table, doomed);

        // Effect absent, and only for the item that was deleted.
        var after = await Converge(["ITEM#SIBLING"], what: "the delete");
        Ensure(
            after.Items.Select(i => i["pk"].S).SequenceEqual(["ITEM#SIBLING"]),
            $"the index did not converge on exactly the surviving item: {Py.Repr(after.Items)}");
        Ensure(
            after.ScannedCount == 1,
            $"the index read that shows the withdrawal examined {Py.Repr(after.ScannedCount)} entries; a " +
            "read of an empty or absent index would make the absence vacuous");
        Ensure(
            after.Items[0]["body"].S == "sibling-payload",
            $"the surviving index entry is not the sibling that was written: {Py.Repr(after.Items[0])}");

        // The base table agrees, strongly consistently: one gone, one intact.
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = doomed, ConsistentRead = true });
        Ensure(
            gone.Item is null,
            "the deleted item is still on the base table, so the index withdrawal above is not about " +
            $"a delete at all: {Py.Repr(gone.Item)}");
        var kept = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = sibling, ConsistentRead = true });
        Ensure(
            kept.Item["body"].S == "sibling-payload",
            $"the delete took the sibling with it: {Py.Repr(kept.Item)}");
    }
}
