// Translated from behavioral/dynamodb/check_update_item_addition_and_if_not_exists_overhead_ignores_growth_and_path_absence.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-addition-and-if-not-exists-overhead-ignores-growth-and-path-absence</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAdditionAndIfNotExistsOverheadIgnoresGrowthAndPathAbsence
{
    private const string Check = "update-item-addition-and-if-not-exists-overhead-ignores-growth-and-path-absence";
    private const string Key = "pk";

    private static Func<int, UpdateClause> SetTo(string value) =>
        n => new("SET v = :v, n = :val", Item((":v", S(new string('x', n))), (":val", N(value))));

    private static Func<int, UpdateClause> Add(string x, string y) =>
        n => new("SET v = :v, n = :x + :y", Item((":v", S(new string('x', n))), (":x", N(x)), (":y", N(y))));

    private static UpdateClause IfNotExistsFallback(int n) =>
        new("SET v = :v, f = if_not_exists(f, :fval)", Item((":v", S(new string('x', n))), (":fval", S("a"))));

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
    /// UpdateItem's "n = x + y" byte charge depends on the operands' own size, not on the
    /// arithmetic result's size, even when the result needs one more digit than either operand;
    /// and if_not_exists charges the same whether its target path already exists or is absent.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-addition-and-if-not-exists-overhead-ignores-growth-and-path-absence")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-growth-");
        await CreateTable(ctx, ddb, table);
        var probe = new UpdateSizeBisection(ddb, Key);

        Dictionary<string, AttributeValue> BaseNZero() => Item(("n", N("0")));

        // A plain SET from a 2-digit to a 3-digit number is strictly more expensive -- the
        // bisection resolution can see a 1-digit growth in a stored number.
        var (loPlain2Digit, _) = await probe.Bisect(table, BaseNZero(), SetTo("98"));
        var (loPlain3Digit, _) = await probe.Bisect(table, BaseNZero(), SetTo("198"));
        Ensure(
            loPlain2Digit > loPlain3Digit,
            "a plain SET to the 3-digit number did not cost more room than the 2-digit one: " +
            $"{loPlain2Digit} vs {loPlain3Digit}");

        // "n = x + y" with two 2-digit operands that sum to a 2-digit result (49+49=98) costs the
        // exact same room as the same two 2-digit operands summing to a 3-digit result (99+99=198):
        // the operands are the same size in both calls, and that is what the charge tracks.
        var (loAddNoGrowth, _) = await probe.Bisect(table, BaseNZero(), Add("49", "49"));
        var (loAddGrowth, hiAddGrowth) = await probe.Bisect(table, BaseNZero(), Add("99", "99"));
        Ensure(
            loAddNoGrowth == loAddGrowth,
            $"\"49 + 49\" (no digit growth) accepted up to {loAddNoGrowth} bytes but " +
            $"\"99 + 99\" (one extra digit in the result) accepted up to {loAddGrowth}");

        // if_not_exists: creating f via the fallback (path absent) costs the same as leaving an
        // identically-sized f untouched (path present), once the created value matches in size.
        var (loPathPresent, _) = await probe.Bisect(table, Item(("f", S("a"))), IfNotExistsFallback);
        var (loPathAbsent, _) = await probe.Bisect(table, [], IfNotExistsFallback);
        Ensure(
            loPathPresent == loPathAbsent,
            $"if_not_exists accepted up to {loPathPresent} bytes when the path already existed " +
            $"but only {loPathAbsent} when the fallback had to create it");

        // Positive control: the accepted growing-addition boundary actually lands with n grown to
        // 198, and the refused size leaves n untouched -- proven by a read.
        var controlTable = FidelityContext.UniqueName("sim2real-behav-growth-control-");
        await CreateTable(ctx, ddb, controlTable);

        await ddb.PutItemAsync(controlTable, Item((Key, S("accepted")), ("n", N("0"))));
        await ddb.UpdateItemAsync(probe.Request(controlTable, "accepted", Add("99", "99")(loAddGrowth)));
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("accepted"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            stored.GetValueOrDefault("n")?.N == "198",
            $"the accepted growing update left n={Py.Repr(stored.GetValueOrDefault("n"))}, expected N \"198\"");
        Ensure(
            (stored.GetValueOrDefault("v")?.S ?? "").Length == loAddGrowth,
            $"the accepted {loAddGrowth}-byte update did not read back at that length, " +
            $"got {Py.Repr(stored)}");

        await ddb.PutItemAsync(controlTable, Item((Key, S("refused")), ("n", N("0"))));
        var (code, message) = await UpdateSizeBisection.CodeAndMessage(
            () => ddb.UpdateItemAsync(probe.Request(controlTable, "refused", Add("99", "99")(hiAddGrowth))));
        Ensure((code, message) == UpdateSizeBisection.OverLimit, $"refusal at {hiAddGrowth} bytes was {Py.Repr((code, message))}");
        var afterRefused = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("refused"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            Py.Equal(afterRefused.GetValueOrDefault("n"), N("0")),
            $"a refused addition must leave n unchanged, item is {Py.Repr(afterRefused)}");
    }
}
