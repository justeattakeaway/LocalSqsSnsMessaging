// Translated from behavioral/dynamodb/check_update_item_arithmetic_clause_overhead_ignores_operand_name_length_and_alias.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-arithmetic-clause-overhead-ignores-operand-name-length-and-alias</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemArithmeticClauseOverheadIgnoresOperandNameLengthAndAlias
{
    private const string Check = "update-item-arithmetic-clause-overhead-ignores-operand-name-length-and-alias";
    private const string Key = "pk";
    private const string Target = "g";
    private static readonly string ShortP = new('p', 10);
    private static readonly string ShortQ = new('q', 10);
    private static readonly string LongP = new('p', 25);
    private static readonly string LongQ = new('q', 25);
    private const string LongPToken = "#reallylongaliastoken1";
    private const string LongQToken = "#reallylongaliastoken2";

    private static UpdateClause Plain(int n) => new("SET v = :v", Item((":v", S(new string('x', n)))));

    private static Func<int, UpdateClause> AddDirect(string pName, string qName) =>
        n => new($"SET v = :v, {Target} = {pName} + {qName}", Item((":v", S(new string('x', n)))));

    private static Func<int, UpdateClause> AddAlias(string pName, string qName, string pToken = "#p", string qToken = "#q") =>
        n => new(
            $"SET v = :v, #g = {pToken} + {qToken}",
            Item((":v", S(new string('x', n)))),
            Names(("#g", Target), (pToken, pName), (qToken, qName)));

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
    /// UpdateItem's "g = p + q" byte charge, isolated from the operands' own name bytes, is
    /// the same for a 10-character and a 25-character operand name, and the same whether an
    /// operand is named directly, through a short alias, or through a long alias.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-arithmetic-clause-overhead-ignores-operand-name-length-and-alias")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-namealias-");
        await CreateTable(ctx, ddb, table);
        var probe = new UpdateSizeBisection(ddb, Key);

        Dictionary<string, AttributeValue> BaseShort() => Item((Target, N("0")), (ShortP, N("0")), (ShortQ, N("0")));
        Dictionary<string, AttributeValue> BaseLong() => Item((Target, N("0")), (LongP, N("0")), (LongQ, N("0")));

        var (loPlainShort, _) = await probe.Bisect(table, BaseShort(), Plain);
        var (loPlainLong, _) = await probe.Bisect(table, BaseLong(), Plain);
        var (loAddDirectShort, hiAddDirectShort) = await probe.Bisect(table, BaseShort(), AddDirect(ShortP, ShortQ));
        var (loAddDirectLong, _) = await probe.Bisect(table, BaseLong(), AddDirect(LongP, LongQ));
        var (loAddAliasShort, _) = await probe.Bisect(table, BaseShort(), AddAlias(ShortP, ShortQ));
        var (loAddAliasLong, _) = await probe.Bisect(table, BaseLong(), AddAlias(LongP, LongQ));
        var (loAddLongAliasToken, _) = await probe.Bisect(table, BaseLong(), AddAlias(LongP, LongQ, LongPToken, LongQToken));

        var overheadShort = loPlainShort - loAddDirectShort;
        var overheadLong = loPlainLong - loAddDirectLong;
        Ensure(
            overheadShort == overheadLong,
            $"addition overhead was {overheadShort} byte(s) for a 10-char operand name but " +
            $"{overheadLong} byte(s) for a 25-char operand name");
        Ensure(
            loAddDirectShort == loAddAliasShort,
            $"naming the operand directly accepted up to {loAddDirectShort} bytes but an alias " +
            $"to the same name accepted up to {loAddAliasShort}");
        Ensure(
            loAddDirectLong == loAddAliasLong,
            $"naming the 25-char operand directly accepted up to {loAddDirectLong} bytes but a " +
            $"short alias to the same name accepted up to {loAddAliasLong}");
        Ensure(
            loAddAliasLong == loAddLongAliasToken,
            $"a short alias accepted up to {loAddAliasLong} bytes but a 22-char alias to the " +
            $"same real names accepted up to {loAddLongAliasToken}");

        // Positive control: the accepted boundary for the direct-short addition actually lands, and
        // the next byte up leaves no trace -- proven by a read, not only the absence of a ClientError.
        var controlTable = FidelityContext.UniqueName("sim2real-behav-namealias-control-");
        await CreateTable(ctx, ddb, controlTable);
        var accepted = BaseShort();
        accepted[Key] = S("accepted");
        await ddb.PutItemAsync(controlTable, accepted);
        await ddb.UpdateItemAsync(probe.Request(controlTable, "accepted", AddDirect(ShortP, ShortQ)(loAddDirectShort)));
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("accepted"))), ConsistentRead = true })).Item ?? [];
        Ensure(
            (stored.GetValueOrDefault("v")?.S ?? "").Length == loAddDirectShort,
            $"the accepted {loAddDirectShort}-byte update did not read back at that length, " +
            $"got {Py.Repr(stored)}");

        var refused = BaseShort();
        refused[Key] = S("refused");
        await ddb.PutItemAsync(controlTable, refused);
        var (code, message) = await UpdateSizeBisection.CodeAndMessage(
            () => ddb.UpdateItemAsync(probe.Request(controlTable, "refused", AddDirect(ShortP, ShortQ)(hiAddDirectShort))));
        Ensure(
            (code, message) == UpdateSizeBisection.OverLimit,
            $"refusal at {hiAddDirectShort} bytes was {Py.Repr((code, message))}, expected {Py.Repr(UpdateSizeBisection.OverLimit)}");
        var afterRefused = (await ddb.GetItemAsync(new GetItemRequest { TableName = controlTable, Key = Item((Key, S("refused"))), ConsistentRead = true })).Item ?? [];
        Ensure(!afterRefused.ContainsKey("v"), $"a refused update must leave no trace, item is {Py.Repr(afterRefused)}");
    }
}
