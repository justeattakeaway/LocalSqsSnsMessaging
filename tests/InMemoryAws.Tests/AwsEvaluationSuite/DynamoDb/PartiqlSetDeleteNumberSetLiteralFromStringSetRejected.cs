// Translated from behavioral/dynamodb/check_partiql_set_delete_number_set_literal_from_string_set_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-set-delete-number-set-literal-from-string-set-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSetDeleteNumberSetLiteralFromStringSetRejected
{
    private const string CheckId = "partiql-set-delete-number-set-literal-from-string-set-rejected";

    private static readonly (string Code, string Message) OperandType = (
        "ValidationException",
        "An operand in the update expression has an incorrect data type");

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
    /// ExecuteStatement refuses set_delete of a Number Set literal from a String Set attribute
    /// with the operand-type ValidationException, and leaves the set unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-set-delete-number-set-literal-from-string-set-rejected")]
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
        await ddb.PutItemAsync(t, Item(("pk", S("s1")), ("tags", SS("a", "b"))));

        async Task<Dictionary<string, AttributeValue>> Stored() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = key, ConsistentRead = true })).Item;

        // Negative: set_delete(tags, <<5>>) deletes a Number Set literal from a String Set attribute,
        // refused for the operand type mismatch.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{t}\" SET tags = set_delete(tags, <<5>>) WHERE pk = ?",
                Parameters = [S("s1")],
            }),
            OperandType);
        var item = await Stored();
        Ensure(
            item["tags"].SS.ToHashSet().SetEquals(["a", "b"]),
            $"the refused set_delete must leave tags unchanged, got {Py.Repr(item.GetValueOrDefault("tags"))}");

        // Positive control: set_delete of a same-typed (String Set) member succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{t}\" SET tags = set_delete(tags, <<'a'>>) WHERE pk = ?",
            Parameters = [S("s1")],
        });
        item = await Stored();
        Ensure(
            item["tags"].SS.ToHashSet().SetEquals(["b"]),
            $"the positive control's same-typed set_delete must leave {{'b'}}, got {Py.Repr(item.GetValueOrDefault("tags"))}");
    }
}
