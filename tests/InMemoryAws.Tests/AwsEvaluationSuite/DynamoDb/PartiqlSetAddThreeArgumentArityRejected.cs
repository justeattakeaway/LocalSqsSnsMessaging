// Translated from behavioral/dynamodb/check_partiql_set_add_three_argument_arity_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-add-three-argument-arity-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetAddThreeArgumentArityRejected
{
    private const string CheckId = "partiql-set-add-three-argument-arity-rejected";
    private static readonly (string Code, string Message) Arity = ("ValidationException", "SET_ADD must have exactly two arguments at 1:49:7");

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task<TableDescription> WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string tableName)
    {
        var budgetS = Math.Max(5.0, Math.Min(90.0, ctx.TimeLeftSeconds - 10.0));
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < budgetS)
        {
            last = (await ddb.DescribeTableAsync(tableName)).Table;
            if (last.TableStatus == TableStatus.ACTIVE)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE in {Py.Repr(budgetS)}s: {last}");
    }

    /// <summary>Call fn() and assert the error it raises carries exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// ExecuteStatement refuses set_add called with three arguments with a ValidationException
    /// naming the two-argument arity and leaves the set unchanged; two arguments succeed.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-add-three-argument-arity-rejected")]
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

        var key = Item(("pk", S("s1")));
        await ddb.PutItemAsync(t, Item(("pk", S("s1")), ("tags", SS("a"))));

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // Negative: set_add called with three arguments is refused for the wrong arity.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'b'>>, <<'c'>>) WHERE pk = ?",
                Parameters = [S("s1")],
            }),
            Arity);
        var item = await Stored();
        Ensure(Py.Equal(item["tags"], SS("a")), $"the refused three-argument set_add must leave tags unchanged, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // Positive control: the identical statement with two arguments succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET tags = set_add(tags, <<'b'>>) WHERE pk = ?",
            Parameters = [S("s1")],
        });
        item = await Stored();
        Ensure(
            item["tags"].SS.ToHashSet().SetEquals(["a", "b"]),
            $"the positive control's two-argument set_add must store {{'a','b'}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");
    }
}
