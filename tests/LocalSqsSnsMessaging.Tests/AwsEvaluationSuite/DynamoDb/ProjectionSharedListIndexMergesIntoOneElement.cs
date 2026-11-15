// Translated from behavioral/dynamodb/check_projection_shared_list_index_merges_into_one_element.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-shared-list-index-merges-into-one-element</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionSharedListIndexMergesIntoOneElement
{
    private static readonly (string Alias, string Name)[] AllNames =
        [("#r", "rows"), ("#o", "outer"), ("#i", "inner"), ("#a", "a"), ("#b", "b"), ("#p", "p"), ("#q", "q")];

    /// <summary>
    /// Projection paths through the same list index merge into one element carrying both leaves,
    /// while paths through different indices (including a different inner index) stay separate.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-shared-list-index-merges-into-one-element")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        await ddb.PutItemAsync(
            table,
            Item(
                ("pk", S("shared-idx")),
                ("rows", L(
                    M(("a", S("a0")), ("b", S("b0")), ("c", S("c0"))),
                    M(("a", S("a1")), ("b", S("b1"))),
                    M(("a", S("a2")), ("b", S("b2"))))),
                ("outer", L(
                    M(("inner", L(
                        M(("p", S("p00")), ("q", S("q00"))),
                        M(("p", S("p01")), ("q", S("q01"))))))))));

        // Only the aliases expr references; DynamoDB rejects unused ExpressionAttributeNames.
        // Matched on token boundaries so `#a` is not considered used by `#ab`.
        static Dictionary<string, string> Used(string expr) =>
            Names([.. AllNames.Where(n => Regex.IsMatch(expr, $"(?<![A-Za-z0-9_]){Regex.Escape(n.Alias)}(?![A-Za-z0-9_])"))]);

        async Task<Dictionary<string, AttributeValue>> Project(string expr)
        {
            // A refused legal projection is a divergence, so report it as an assertion failure.
            var (resp, err) = await FidelityContext.Capture(() => ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("shared-idx"))),
                ConsistentRead = true,
                ProjectionExpression = expr,
                ExpressionAttributeNames = Used(expr),
            }));
            if (err is not null)
            {
                throw Fail(
                    $"ProjectionExpression {Py.Repr(expr)} is legal and must be SERVED, but the target refused it with {err.Code}: {err.Message}");
            }

            return resp!.Item;
        }

        // --- same index, two leaves: ONE element carrying both, and not the third leaf ---
        var merged = await Project("#r[0].#a, #r[0].#b");
        Ensure(
            Py.Equal(merged, Item(("rows", L(M(("a", S("a0")), ("b", S("b0"))))))),
            $"two paths through the SAME list index must merge into one response element carrying both leaves (and not the unprojected sibling 'c'); got {Py.Repr(merged)}");

        // --- order written must not change the merge ---
        var mergedRev = await Project("#r[0].#b, #r[0].#a");
        Ensure(
            Py.Equal(mergedRev, merged),
            $"the merge must not depend on the order the two paths were written; got {Py.Repr(mergedRev)} vs {Py.Repr(merged)}");

        // --- different indices: SEPARATE elements, compacted in index order ---
        var split = await Project("#r[0].#a, #r[2].#b");
        Ensure(
            Py.Equal(split, Item(("rows", L(M(("a", S("a0"))), M(("b", S("b2"))))))),
            $"paths through DIFFERENT indices must stay separate elements, compacted into index order; got {Py.Repr(split)}");

        // --- a shared index alongside a distinct one: merge the shared, keep the other ---
        var mixed = await Project("#r[0].#a, #r[0].#b, #r[2].#a");
        Ensure(
            Py.Equal(mixed, Item(("rows", L(M(("a", S("a0")), ("b", S("b0"))), M(("a", S("a2"))))))),
            $"the shared index must merge into one element while a distinct index remains its own, compacted next to it; got {Py.Repr(mixed)}");

        // --- nested list, SAME inner index: merge ---
        var innerSame = await Project("#o[0].#i[0].#p, #o[0].#i[0].#q");
        Ensure(
            Py.Equal(innerSame, Item(("outer", L(M(("inner", L(M(("p", S("p00")), ("q", S("q00")))))))))),
            $"two paths sharing BOTH the outer and inner index must merge into one inner element; got {Py.Repr(innerSame)}");

        // --- nested list, DIFFERENT inner index: stay separate ---
        var innerSplit = await Project("#o[0].#i[0].#p, #o[0].#i[1].#q");
        Ensure(
            Py.Equal(innerSplit, Item(("outer", L(M(("inner", L(M(("p", S("p00"))), M(("q", S("q01")))))))))),
            $"paths sharing the outer index but differing on the INNER index must remain two inner elements -- the merge key is the whole prefix; got {Py.Repr(innerSplit)}");

        // --- control: projecting a whole element returns every leaf it has ---
        var whole = await Project("#r[0]");
        Ensure(
            Py.Equal(whole, Item(("rows", L(M(("a", S("a0")), ("b", S("b0")), ("c", S("c0"))))))),
            $"projecting a whole element must return all of its leaves; got {Py.Repr(whole)}");

        // --- control: the stored item still has all three rows, unmerged ---
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared-idx"))), ConsistentRead = true })).Item;
        Ensure(stored["rows"].L.Count == 3, $"a projected read must not mutate the stored list; got {Py.Repr(stored["rows"])}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projmerge-");
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
