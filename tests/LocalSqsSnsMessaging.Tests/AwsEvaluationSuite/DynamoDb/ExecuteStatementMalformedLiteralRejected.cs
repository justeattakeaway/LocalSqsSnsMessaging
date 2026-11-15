// Translated from behavioral/dynamodb/check_execute_statement_malformed_literal_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-malformed-literal-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementMalformedLiteralRejected
{
    private const string EmptyTailMessage = "Statement wasn't well formed, can't be processed: ";
    private const string UnclosedItemMessage = "Statement wasn't well formed, can't be processed: Expected RIGHT_CURLY";
    private const string UnclosedSetMessage = "Statement wasn't well formed, can't be processed: Expected RIGHT_DOUBLE_ANGLE_BRACKET";

    /// <summary>
    /// ExecuteStatement rejects an INSERT whose literal is malformed at the lexical level —
    /// an unterminated string, a number split from its exponent, an unclosed {...} item
    /// literal, an unclosed &lt;&lt;...&gt;&gt; set literal — with ValidationException, and writes nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.insert.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-malformed-literal-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitActive(ddb, tableName);

        // Positive control: a well-formed INSERT succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'seed'}}" });
        Ensure((await ItemsAt(ddb, tableName, "seed")).Count > 0, "seed item missing after a well-formed INSERT");

        // Unterminated string literal.
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'px, 'sk': 'sx'}}", EmptyTailMessage);
        Ensure((await ItemsAt(ddb, tableName, "px")).Count == 0, "an unterminated-string INSERT still wrote an item");

        // Number split from its exponent (a trailing 'e' with no exponent digits).
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'py', 'n': 1e}}", EmptyTailMessage);
        Ensure((await ItemsAt(ddb, tableName, "py")).Count == 0, "a split-exponent INSERT still wrote an item");

        // Malformed {...} item literal: no closing brace.
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'pz'", UnclosedItemMessage);
        Ensure((await ItemsAt(ddb, tableName, "pz")).Count == 0, "an unclosed-item-literal INSERT still wrote an item");

        // Malformed <<...>> set literal: no closing double angle bracket.
        await AssertRejected(ddb, $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'pw', 'tags': <<'a', 'b'}}", UnclosedSetMessage);
        Ensure((await ItemsAt(ddb, tableName, "pw")).Count == 0, "an unclosed-set-literal INSERT still wrote an item");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll DescribeTable until ACTIVE.</summary>
    private static async Task<string?> WaitActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status})");
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> ItemsAt(IAmazonDynamoDB ddb, string tableName, string pk) =>
        (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{tableName}\" WHERE pk='{pk}'" })).Items;

    private static async Task AssertRejected(IAmazonDynamoDB ddb, string statement, string expectedMessage)
    {
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(message == expectedMessage, $"unexpected message {Py.Repr(message)}");
    }
}
