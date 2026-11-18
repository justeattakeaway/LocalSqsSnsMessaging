// Translated from behavioral/dynamodb/check_ddbgsi_projected_value_converges_in_the_index_after_an_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbgsi-projected-value-converges-in-the-index-after-an-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbgsiProjectedValueConvergesInTheIndexAfterAnUpdate
{
    private const string Check = "ddbgsi-projected-value-converges-in-the-index-after-an-update";
    private const string Index = "gsi1";
    private const int ConvergeSeconds = 180;

    private static readonly HashSet<string> ProjectedNames = ["pk", "sk", "gsi1pk", "label"];

    /// <summary>
    /// A GSI entry converges on an updated projected label and never gains non-projected 'notes'.
    /// The base table shows both new values immediately; the index only ever holds one entry.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbgsi-projected-value-converges-in-the-index-after-an-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbgsi-conv-");
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
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["label"] },
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

        var key = Item(("pk", S("ITEM#1")), ("sk", S("S1")));

        async Task<List<Dictionary<string, AttributeValue>>> IndexEntries() => (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("BUCKET#A"))),
        })).Items;

        // Poll the index until the projected label equals want; collect every value seen.
        // GSI writes arrive eventually with no documented bound, so the deadline is a give-up bound.
        async Task<(Dictionary<string, AttributeValue> Entry, HashSet<string> Seen)> Converge(string want, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<Dictionary<string, AttributeValue>> entries = [];
            var giveUp = FidelityTime.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(ConvergeSeconds))
            {
                entries = await IndexEntries();
                foreach (var e in entries)
                {
                    if (e.TryGetValue("label", out var label))
                    {
                        seen.Add(label.S);
                    }
                }

                if (entries.Count == 1 && entries[0].TryGetValue("label", out var l) && Py.Equal(l, S(want)))
                {
                    return (entries[0], seen);
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }

            throw Fail(
                $"{what}: the index never converged on label={Py.Repr(want)} within {ConvergeSeconds}s; the last " +
                $"read was {Py.Repr(entries)} and the values seen over the whole poll were {Py.Repr(seen.Order(StringComparer.Ordinal).ToList())}");
        }

        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#1")), ("sk", S("S1")), ("gsi1pk", S("BUCKET#A")), ("label", S("v1")), ("notes", S("n1"))));

        var (first, seenBefore) = await Converge("v1", what: "the initial write");
        Ensure(
            first.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ProjectedNames),
            "the INCLUDE ['label'] projection must expose exactly the base and index keys plus " +
            $"'label', got {Py.Repr(first.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            !first.ContainsKey("notes"),
            $"a non-projected attribute leaked into the index entry: {Py.Repr(first)}");

        var baseBefore = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            baseBefore["notes"].S == "n1" && baseBefore["label"].S == "v1",
            "the base item does not carry the values the index absence above is measured against, so " +
            $"that absence would be vacuous: {Py.Repr(baseBefore)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET label = :l, notes = :n",
            ExpressionAttributeValues = Item((":l", S("v2")), (":n", S("n2"))),
        });

        // The base table is strongly consistent, so the new values are readable with no poll at all.
        var baseAfter = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            baseAfter["label"].S == "v2",
            $"the strongly consistent base read did not see the update it followed: {Py.Repr(baseAfter)}");
        Ensure(
            baseAfter["notes"].S == "n2",
            $"the non-projected attribute was not updated on the base item: {Py.Repr(baseAfter)}");

        var (after, seenAfter) = await Converge("v2", what: "the update");
        Ensure(
            after.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ProjectedNames),
            $"the projection stopped governing the index entry after the update: {Py.Repr(after.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            !after.ContainsKey("notes"),
            $"updating 'notes' pulled a non-projected attribute into the index entry: {Py.Repr(after)}");

        // Only 'v1' and 'v2' were ever stored, so any other label is invented index state.
        var union = seenBefore.Union(seenAfter).ToHashSet(StringComparer.Ordinal);
        Ensure(
            union.IsSubsetOf(["v1", "v2"]),
            "the index exposed a label value that was never written: " +
            $"{Py.Repr(union.Except(["v1", "v2"]).Order(StringComparer.Ordinal).ToList())}");
    }
}
