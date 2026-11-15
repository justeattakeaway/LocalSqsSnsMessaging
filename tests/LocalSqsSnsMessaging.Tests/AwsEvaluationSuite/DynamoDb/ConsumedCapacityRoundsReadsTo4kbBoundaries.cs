// Translated from behavioral/dynamodb/check_consumed_capacity_rounds_reads_to_4kb_boundaries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-rounds-reads-to-4kb-boundaries</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityRoundsReadsTo4kbBoundaries
{
    private const string CheckId = "consumed-capacity-rounds-reads-to-4kb-boundaries";

    private const int Kb = 1024;

    // A strongly consistent read is billed one unit per 4KB block, rounded UP. The payload sizes below
    // sit well inside their blocks so the few bytes of attribute names and key cannot move either one
    // across a boundary: ~1KB lands in the first block, ~3KB in the first, ~5KB in the second.
    private const int TinyBytes = 1 * Kb;
    private const int UnderOneBlockBytes = 3 * Kb;
    private const int IntoSecondBlockBytes = 5 * Kb;

    /// <summary>
    /// Read capacity is the item size rounded UP to a 4KB block: 1KB and 3KB items cost 1.0 strongly
    /// consistent, 5KB costs 2.0, and an eventually consistent read costs exactly half (after
    /// rounding).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-rounds-reads-to-4kb-boundaries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rcu-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var sizes = new List<(string Name, int Size)>
        {
            ("tiny", TinyBytes),
            ("under", UnderOneBlockBytes),
            ("over", IntoSecondBlockBytes),
        };
        foreach (var (name, size) in sizes)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(name)), ("v", S(new string('x', size)))));
        }

        int SizeOf(string name) => sizes.First(s => s.Name == name).Size;

        async Task<double?> Units(string name, bool consistent)
        {
            var resp = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(name))),
                ConsistentRead = consistent,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            Ensure(resp.Item is not null, $"{Py.Repr(name)} must exist before its capacity can be read");
            return resp.ConsumedCapacity.CapacityUnits;
        }

        var expectedStrong = new List<(string Name, double Expected)> { ("tiny", 1.0), ("under", 1.0), ("over", 2.0) };
        foreach (var (name, expected) in expectedStrong)
        {
            var actual = await Units(name, consistent: true);
            Ensure(
                actual == expected,
                $"a strongly consistent GetItem on the ~{SizeOf(name) / Kb}KB item must cost {Py.Repr(expected)} capacity units -- the item size rounded up to a 4KB block -- got {Py.Repr(actual)}");
        }

        foreach (var (name, strong) in expectedStrong)
        {
            var actual = await Units(name, consistent: false);
            Ensure(
                actual == strong / 2,
                $"an eventually consistent read costs exactly half its strongly consistent cost, so the ~{SizeOf(name) / Kb}KB item must cost {Py.Repr(strong / 2)}, got {Py.Repr(actual)}. The discount applies AFTER rounding");
        }

        // The two items inside one block cost the same, which no byte-proportional figure can produce.
        Ensure(
            await Units("tiny", consistent: true) == await Units("under", consistent: true),
            $"a ~1KB item and a ~3KB item share a block and must cost the same: got {Py.Repr(await Units("tiny", consistent: true))} and {Py.Repr(await Units("under", consistent: true))}");
    }
}
