// Translated from behavioral/dynamodb/check_update_item_clause_overhead_ignores_operand_value_and_removed_members.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-clause-overhead-ignores-operand-value-and-removed-members</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemClauseOverheadIgnoresOperandValueAndRemovedMembers
{
    private const string Check = "update-item-clause-overhead-ignores-operand-value-and-removed-members";
    private const string Key = "pk";

    private static UpdateClause Plain(int n) => new("SET v = :v", Item((":v", S(new string('x', n)))));

    private static Func<int, UpdateClause> DeleteMembers(string[] members) =>
        n => new("SET v = :v DELETE s :op", Item((":v", S(new string('x', n))), (":op", SS(members))));

    private static UpdateClause CopyPath(int n) => new("SET v = :v, c = b", Item((":v", S(new string('x', n)))));

    private static async Task CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement(Key, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(Key, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
    }

    /// <summary>
    /// UpdateItem charges a DELETE clause the same whether it removes a 1-byte, a 200-byte or two
    /// set members, and a path-operand SET "c = b" charges the same whether b is a 50- or 300-byte
    /// string or a 50-byte binary value.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-clause-overhead-ignores-operand-value-and-removed-members")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-opvalue-");
        await CreateTable(ctx, ddb, table);
        var probe = new UpdateSizeBisection(ddb, Key);

        // DELETE: removing a 1-byte member, a 200-byte member, and two members together all cost
        // the same, once the natural shrinkage from each removal is isolated via a reference item
        // that never had the member(s) in the first place.
        var deleteVariants = new (string Label, string[] Members)[]
        {
            ("small", ["a"]),
            ("large", [new string('x', 200)]),
            ("multi", ["a", "z"]),
        };
        var deleteOverheads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, members) in deleteVariants)
        {
            var (loPlainWithout, _) = await probe.Bisect(table, [], Plain);
            var (loDelete, _) = await probe.Bisect(table, Item(("s", SS(members))), DeleteMembers(members));
            deleteOverheads[label] = loPlainWithout - loDelete;
        }

        Ensure(
            deleteOverheads.Values.Distinct().Count() == 1,
            $"DELETE overhead varied with the removed member(s): {Py.Repr(deleteOverheads)}");

        // Path operand: once the copied value is large enough that its own bytes dominate, "c = b"
        // charges nothing more as it grows further, and the same whether it is a string or binary
        // value. The reference item already holds c at the value the copy will produce, so only the
        // clause's own request-time charge remains once the cost of duplicating b's value is removed.
        var pathVariants = new (string Label, Func<AttributeValue> BValue)[]
        {
            ("string_50", () => S(new string('x', 50))),
            ("string_300", () => S(new string('x', 300))),
            ("binary_50", () => B(Enumerable.Repeat((byte)'y', 50).ToArray())),
        };
        var pathOverheads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, bValue) in pathVariants)
        {
            var (loPlainAlreadyCopied, _) = await probe.Bisect(table, Item(("b", bValue()), ("c", bValue())), Plain);
            var (loCopy, _) = await probe.Bisect(table, Item(("b", bValue())), CopyPath);
            pathOverheads[label] = loPlainAlreadyCopied - loCopy;
        }

        Ensure(
            pathOverheads.Values.Distinct().Count() == 1,
            $"path-operand overhead varied with the copied value: {Py.Repr(pathOverheads)}");

        // Positive control: the accepted DELETE boundary (small-member variant) actually lands, and
        // the member is actually removed -- proven by a read, not only the absence of a ClientError.
        var controlTable = FidelityContext.UniqueName("sim2real-behav-opvalue-control-");
        await CreateTable(ctx, ddb, controlTable);
        var (lo, hi) = await probe.Bisect(controlTable, Item(("s", SS("a"))), DeleteMembers(["a"]));

        await ddb.PutItemAsync(controlTable, Item((Key, S("accepted")), ("s", SS("a"))));
        await ddb.UpdateItemAsync(probe.Request(controlTable, "accepted", DeleteMembers(["a"])(lo)));
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("accepted"))), ConsistentRead = true })).Item ?? [];
        Ensure(!stored.ContainsKey("s"), $"DELETE of the sole member must remove the attribute, got {Py.Repr(stored)}");
        Ensure(
            (stored.GetValueOrDefault("v")?.S ?? "").Length == lo,
            $"the accepted {lo}-byte update did not read back at that length, got {Py.Repr(stored)}");

        await ddb.PutItemAsync(controlTable, Item((Key, S("refused")), ("s", SS("a"))));
        var (code, message) = await UpdateSizeBisection.CodeAndMessage(
            () => ddb.UpdateItemAsync(probe.Request(controlTable, "refused", DeleteMembers(["a"])(hi))));
        Ensure((code, message) == UpdateSizeBisection.OverLimit, $"refusal at {hi} bytes was {Py.Repr((code, message))}");
        var afterRefused = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("refused"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            Py.Equal(afterRefused.GetValueOrDefault("s"), SS("a")),
            $"a refused DELETE must leave the member in place, item is {Py.Repr(afterRefused)}");
    }
}
