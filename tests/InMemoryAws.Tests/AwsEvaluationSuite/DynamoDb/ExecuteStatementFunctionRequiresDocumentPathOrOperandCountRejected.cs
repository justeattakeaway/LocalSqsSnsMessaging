// Translated from behavioral/dynamodb/check_execute_statement_function_requires_document_path_or_operand_count_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-function-requires-document-path-or-operand-count-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementFunctionRequiresDocumentPathOrOperandCountRejected
{
    private const string CheckId = "execute-statement-function-requires-document-path-or-operand-count-rejected";

    /// <summary>
    /// ExecuteStatement/UPDATE refuses attribute_exists given a literal or a `?` parameter in
    /// place of a document path, and given 0 or 3 operands, each with ValidationException, and
    /// leaves the item untouched; the same function over a real path still gates the write.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-function-requires-document-path-or-operand-count-rejected")]
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

        var baseUpdate = $"UPDATE \"{table}\" SET note = 'changed' WHERE pk = 'item' AND sk = 'row' AND ";

        const string docPathMessage = "Operator or function requires a document path; operator or function: attribute_exists";
        static string OperandCountMessage(int n) =>
            $"Incorrect number of operands for operator or function; operator or function: attribute_exists, number of operands: {n}";

        (string Label, string Statement, List<AttributeValue>? Params, string ExpectedMessage)[] cases =
        [
            ("literal in place of a document path", baseUpdate + "attribute_exists('note')", null, docPathMessage),
            ("? parameter in place of a document path", baseUpdate + "attribute_exists(?)", [S("note")], docPathMessage),
            ("0 operands", baseUpdate + "attribute_exists()", null, OperandCountMessage(0)),
            ("3 operands", baseUpdate + "attribute_exists(note, note, note)", null, OperandCountMessage(3)),
        ];

        foreach (var (label, statement, @params, expectedMessage) in cases)
        {
            var request = new ExecuteStatementRequest { Statement = statement };
            if (@params is not null)
            {
                request.Parameters = @params;
            }

            var (code, message) = await Error(() => ddb.ExecuteStatementAsync(request));
            Ensure(
                code == "ValidationException",
                $"UPDATE with attribute_exists ({label}) answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
            Ensure(
                message == expectedMessage,
                $"UPDATE with attribute_exists ({label}) message was {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");

            var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
            Ensure(
                unchanged["note"].S == "original",
                $"UPDATE with attribute_exists ({label}) must write nothing, but note is now {Py.Repr(unchanged["note"])}");
        }

        // Positive control: the same function over a real document path gates the write correctly.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET note = 'changed' WHERE pk = 'item' AND sk = 'row' AND attribute_exists(note)",
        });
        var gated = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(gated["note"].S == "changed", $"attribute_exists(note) over a real path must gate the write, got {Py.Repr(gated["note"])}");
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
