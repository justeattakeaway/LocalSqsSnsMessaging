// Translated from behavioral/dynamodb/check_execute_statement_attribute_exists_two_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-attribute-exists-two-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementAttributeExistsTwoOperandsRejected
{
    private const string CheckId = "execute-statement-attribute-exists-two-operands-rejected";

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {Py.Repr(result)}");
    }

    /// <summary>Poll DescribeTable until ACTIVE, bounded by the runner's own remaining budget.</summary>
    private static async Task WaitForTableActive(IAmazonDynamoDB ddb, FidelityContext ctx, string tableName)
    {
        var budget = Math.Max(1.0, Math.Min(90.0, ctx.TimeLeftSeconds - 10.0));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(2.0, Math.Max(0.0, budget - clock.Elapsed.TotalSeconds))));
        }

        throw Fail($"table {Py.Repr(tableName)} never went ACTIVE within budget (last={Py.Repr(status)})");
    }

    /// <summary>
    /// ExecuteStatement/UPDATE refuses attribute_exists given two operands with a
    /// ValidationException naming the operand count, and leaves the item untouched; the same
    /// function with one operand still gates the write.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-attribute-exists-two-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbae2-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitForTableActive(ddb, ctx, table);

        var key = Item(("pk", S("item")), ("sk", S("row")));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("row")), ("ss", SS("a", "b")), ("ns", NS("1", "2")), ("note", S("original"))));

        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET note = 'changed' WHERE pk = 'item' AND sk = 'row' "
                + "AND attribute_exists(ss, ns)",
        }));
        Ensure(
            code == "ValidationException",
            $"UPDATE with attribute_exists(ss, ns) answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expectedMessage =
            "Incorrect number of operands for operator or function; operator or function: "
            + "attribute_exists, number of operands: 2";
        Ensure(
            message == expectedMessage,
            $"UPDATE with attribute_exists(ss, ns) message was {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            unchanged["note"].S == "original",
            $"UPDATE with attribute_exists(ss, ns) must write nothing, but note is now {Py.Repr(unchanged["note"])}");

        // Positive control: the same function with one operand over a real path still gates the write.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET note = 'changed' WHERE pk = 'item' AND sk = 'row' "
                + "AND attribute_exists(ss)",
        });
        var gated = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(gated["note"].S == "changed", $"attribute_exists(ss) over a real path must gate the write, got {Py.Repr(gated["note"])}");
    }
}
