// Translated from behavioral/dynamodb/check_execute_statement_undefined_function_and_projection_function_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-undefined-function-and-projection-function-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementUndefinedFunctionAndProjectionFunctionRejected
{
    private const string CheckId = "execute-statement-undefined-function-and-projection-function-rejected";

    /// <summary>
    /// ExecuteStatement refuses a function it does not define, and refuses any function placed
    /// in the SELECT projection list, both with ValidationException naming the exact token position,
    /// and each leaves the table untouched.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-undefined-function-and-projection-function-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-");
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
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("row")), ("note", S("original"))));

        // A function DynamoDB does not define, used inside an UPDATE's WHERE clause.
        const string undefinedFn = "made_up_function";
        var updateStmt = $"UPDATE \"{table}\" SET note = 'changed' WHERE pk = 'item' AND sk = 'row' AND {undefinedFn}(note) = true";
        var col = updateStmt.IndexOf(undefinedFn, StringComparison.Ordinal) + 1;
        var expectedUndefinedMessage = $"Unrecognized function: {undefinedFn} at 1:{col}:{undefinedFn.Length}";

        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = updateStmt }));
        Ensure(
            code == "ValidationException",
            $"UPDATE calling undefined function {Py.Repr(undefinedFn)} answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == expectedUndefinedMessage, $"undefined-function message was {Py.Repr(message)}, expected {Py.Repr(expectedUndefinedMessage)}");
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            unchanged["note"].S == "original",
            $"UPDATE calling an undefined function must write nothing, but note is now {Py.Repr(unchanged["note"])}");

        // A function DynamoDB DOES define, used in the SELECT projection list rather than a
        // condition: the projection list itself cannot carry a function call.
        const string definedFn = "attribute_exists";
        var selectStmt = $"SELECT {definedFn}(note) FROM \"{table}\"";
        col = selectStmt.IndexOf(definedFn, StringComparison.Ordinal) + 1;
        var expectedProjectionMessage = $"Unexpected path component at 1:{col}:{definedFn.Length}";

        (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = selectStmt }));
        Ensure(
            code == "ValidationException",
            $"SELECT projecting {definedFn}(note) answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(
            message == expectedProjectionMessage,
            $"function-in-projection message was {Py.Repr(message)}, expected {Py.Repr(expectedProjectionMessage)}");
        var unread = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(unread["note"].S == "original", $"a rejected SELECT must write nothing, but note is now {Py.Repr(unread["note"])}");

        // Positive control: the same function, used in a WHERE clause rather than the projection
        // list, is accepted.
        var rows = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE {definedFn}(note)" })).Items;
        Ensure(
            rows.Count == 1 && rows[0]["note"].S == "original",
            $"attribute_exists(note) in a WHERE clause must select the item, got {Py.Repr(rows)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {Py.Repr(resp)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable until ACTIVE, bounded by the runner's own remaining budget.</summary>
    private static async Task WaitForTableActive(IAmazonDynamoDB ddb, FidelityContext ctx, string tableName)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(90.0, ctx.TimeLeftSeconds - 10.0)));
        string? status = null;
        while (clock.Elapsed < budget)
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            var remaining = budget - clock.Elapsed;
            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(2.0, Math.Max(0.0, remaining.TotalSeconds))));
        }

        throw Fail($"table {Py.Repr(tableName)} never went ACTIVE within budget (last={Py.Repr(status)})");
    }
}
