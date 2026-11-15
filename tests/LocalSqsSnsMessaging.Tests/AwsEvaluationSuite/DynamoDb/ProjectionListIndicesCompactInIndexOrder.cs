// Translated from behavioral/dynamodb/check_projection_list_indices_compact_in_index_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-list-indices-compact-in-index-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionListIndicesCompactInIndexOrder
{
    /// <summary>
    /// A ProjectionExpression selecting list elements by index returns a dense list of only those
    /// elements, ordered by stored index (not written order) and renumbered from zero.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-list-indices-compact-in-index-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        await ddb.PutItemAsync(table, Item(
            ("pk", S("list-idx")),
            ("slots", L(S("zero"), S("one"), S("two"), S("three")))));

        async Task<Dictionary<string, AttributeValue>> Project(string expr)
        {
            // A refusal of a legal projection is a divergence, so report it as an assertion failure.
            var (response, error) = await FidelityContext.Capture(() => ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("list-idx"))),
                ConsistentRead = true,
                ProjectionExpression = expr,
                ExpressionAttributeNames = Names(("#s", "slots")),
            }));
            if (error is not null)
            {
                throw Fail($"ProjectionExpression {Py.Repr(expr)} is legal and must be SERVED, but the target refused it with {error.Code}: {error.Message}");
            }

            return response!.Item;
        }

        // --- a single middle index lands at response position 0 ---
        var one = await Project("#s[2]");
        Ensure(
            Py.Equal(one, Item(("slots", L(S("two"))))),
            $"projecting a single list element must return a ONE-element list holding it at response position 0 (not a sparse list padded to the original offset); got {Py.Repr(one)}");

        // --- paths written out of index order come back in index order ---
        var reordered = await Project("#s[3], #s[1]");
        Ensure(
            Py.Equal(reordered, Item(("slots", L(S("one"), S("three"))))),
            $"list-element paths must be returned ordered by their index in the STORED list, not by the order the paths were written: '#s[3], #s[1]' must yield [one, three]; got {Py.Repr(reordered)}");

        // --- a non-contiguous pair is compacted to adjacent positions ---
        var gapped = await Project("#s[0], #s[3]");
        Ensure(
            Py.Equal(gapped, Item(("slots", L(S("zero"), S("three"))))),
            $"two non-adjacent indices must compact to response positions 0 and 1 with no hole between them; got {Py.Repr(gapped)}");

        // --- every index, ascending: the full list, unchanged ---
        var everything = await Project("#s[0], #s[1], #s[2], #s[3]");
        Ensure(
            Py.Equal(everything, Item(("slots", L(S("zero"), S("one"), S("two"), S("three"))))),
            $"projecting every index must reproduce the whole list in order; got {Py.Repr(everything)}");

        // --- control: projecting the attribute itself returns the whole list ---
        var whole = await Project("#s");
        Ensure(
            Py.Equal(whole, Item(("slots", L(S("zero"), S("one"), S("two"), S("three"))))),
            $"projecting the list attribute must return it unchanged; got {Py.Repr(whole)}");

        // --- control: the stored item is untouched by any of the reads above ---
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("list-idx"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored["slots"], L(S("zero"), S("one"), S("two"), S("three"))),
            $"a projected read must not mutate the stored list; got {Py.Repr(stored["slots"])}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projidx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {name} never became ACTIVE");
    }
}
