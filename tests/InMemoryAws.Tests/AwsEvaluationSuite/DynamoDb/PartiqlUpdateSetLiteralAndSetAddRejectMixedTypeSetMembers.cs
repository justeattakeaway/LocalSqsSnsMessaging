// Translated from behavioral/dynamodb/check_partiql_update_set_literal_and_set_add_reject_mixed_type_set_members.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-set-literal-and-set-add-reject-mixed-type-set-members</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateSetLiteralAndSetAddRejectMixedTypeSetMembers
{
    private const string CheckId = "partiql-update-set-literal-and-set-add-reject-mixed-type-set-members";

    private static readonly (string Code, string Message) MixedLiteral = (
        "ValidationException",
        "Unsupported data type in Bag. DynamoDB only supports either numbers or strings in bags");

    private static readonly (string Code, string Message) MixedOperand = (
        "ValidationException",
        "An operand in the update expression has an incorrect data type");

    private static readonly string[] Xy = ["x", "y"];

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription?> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var left = ctx.TimeLeftSeconds;
        var budgetS = Math.Max(5.0, Math.Min(90.0, left - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {budgetS}s: {last}");
    }

    /// <summary>Call fn() and assert the ClientError it raises carries exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// UPDATE ... SET on a mixed-type set literal, and set_add() mixing a member of a different
    /// type into an existing set, are both refused and leave the set unchanged; UPDATE ... SET on a
    /// mixed-type list or map literal succeeds and stores each element with its own type.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-set-literal-and-set-add-reject-mixed-type-set-members")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await WaitTableActive(ctx, ddb, t);

        var nonce = FidelityContext.UniqueName("doc-");
        var key = Item(("pk", S(nonce)));

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // Seed the item with a homogeneous String Set literal (positive control).
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{t}\" VALUE {{'pk': ?, 'goodset': <<'x', 'y'>>}}",
            Parameters = [S(nonce)],
        });
        var item = await Stored();
        Ensure(item["goodset"].SS.ToHashSet().SetEquals(Xy), $"seeded item must have goodset {{'x','y'}}, got {Py.Repr(item.GetValueOrDefault("goodset"))}");

        // Negative 1: UPDATE ... SET on a mixed-type (number, string) set literal is refused, and the
        // set stays exactly as seeded.
        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{t}\" SET goodset = <<1, 'z'>> WHERE pk = ?",
                Parameters = [S(nonce)],
            }),
            MixedLiteral);
        item = await Stored();
        Ensure(item["goodset"].SS.ToHashSet().SetEquals(Xy), $"goodset must be unchanged after the refused literal UPDATE, got {Py.Repr(item.GetValueOrDefault("goodset"))}");

        // Negative 2: set_add() mixing a Number into an existing String Set is refused with the
        // operand-type message, and the set is unchanged.
        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{t}\" SET goodset = set_add(goodset, <<5>>) WHERE pk = ?",
                Parameters = [S(nonce)],
            }),
            MixedOperand);
        item = await Stored();
        Ensure(item["goodset"].SS.ToHashSet().SetEquals(Xy), $"goodset must be unchanged after the refused set_add, got {Py.Repr(item.GetValueOrDefault("goodset"))}");

        // Positive: UPDATE ... SET on a mixed-type list and a mixed-type map literal succeeds, and
        // each element stores with its own type exactly as written.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET mixedlist = [1, 'a', true], mixedmap = {{'a':1,'b':'x'}} WHERE pk = ?",
            Parameters = [S(nonce)],
        });
        item = await Stored();
        Ensure(
            Py.Equal(item["mixedlist"], L(N("1"), S("a"), Bool(true))),
            $"mixedlist must store [N,S,BOOL] in order, got {Py.Repr(item.GetValueOrDefault("mixedlist"))}");
        Ensure(
            Py.Equal(item["mixedmap"], M(("a", N("1")), ("b", S("x")))),
            $"mixedmap must store {{a:N, b:S}}, got {Py.Repr(item.GetValueOrDefault("mixedmap"))}");
        Ensure(item["goodset"].SS.ToHashSet().SetEquals(Xy), $"goodset must still be unchanged after the unrelated SET, got {Py.Repr(item.GetValueOrDefault("goodset"))}");
    }
}
