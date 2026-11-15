// Translated from behavioral/dynamodb/check_execute_statement_quoting_rules.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-quoting-rules</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementQuotingRules
{
    /// <summary>
    /// ExecuteStatement rejects a single-quoted table name and an unquoted reserved keyword
    /// attribute with ValidationException, each leaving the table unwritten, while a double-quoted
    /// string literal is ACCEPTED as a path reference rather than a value and matches nothing.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-quoting-rules")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-quote-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);

        await ddb.PutItemAsync(tableName, Item(("pk", S("seed-pk")), ("sk", S("seed-sk")), ("note", S("orig"))));

        // Case A: the table name in single quotes is rejected; nothing is written.
        var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO '{tableName}' VALUE {{'pk':'single-quoted-tbl','sk':'s1'}}",
        }));
        Ensure(code == "ValidationException", $"single-quoted table name: expected ValidationException, got {Py.Repr(code)}");
        const string expectedA = "Statement wasn't well formed, can't be processed: Expected identifier for simple path";
        Ensure(message == expectedA, $"single-quoted table name: expected message {Py.Repr(expectedA)}, got {Py.Repr(message)}");
        var unwrittenA = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("single-quoted-tbl")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(unwrittenA.Item is null, $"single-quoted table name: expected no write, got {Py.Repr(unwrittenA.Item)}");

        // Case B: an unquoted reserved keyword ('size') as an attribute name is rejected; nothing written.
        (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk':'reserved-attr','sk':'s1', size:'x'}}",
        }));
        Ensure(code == "ValidationException", $"unquoted reserved attribute: expected ValidationException, got {Py.Repr(code)}");
        const string expectedB = "Statement wasn't well formed, can't be processed: Unexpected keyword";
        Ensure(message == expectedB, $"unquoted reserved attribute: expected message {Py.Repr(expectedB)}, got {Py.Repr(message)}");
        var unwrittenB = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("reserved-attr")), ("sk", S("s1"))), ConsistentRead = true });
        Ensure(unwrittenB.Item is null, $"unquoted reserved attribute: expected no write, got {Py.Repr(unwrittenB.Item)}");

        // Case C: a string literal in double quotes is ACCEPTED, because double quotes denote a path
        // reference rather than a string value, so the comparison matches nothing.
        var accepted = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{tableName}\" WHERE pk = \"seed-pk\" AND sk = \"seed-sk\"",
        });
        Ensure(
            accepted.Items.Count == 0,
            $"double-quoted literal: expected the path-reference comparison to match nothing, got {Py.Repr(accepted.Items)}");

        // Positive control: the identical comparison written with single-quoted string literals
        // matches the seed item, proving Case C's empty result is the quoting, not a broken table.
        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{tableName}\" WHERE pk = 'seed-pk' AND sk = 'seed-sk'",
        });
        Ensure(
            Py.Equal(control.Items, new[] { Item(("pk", S("seed-pk")), ("sk", S("seed-sk")), ("note", S("orig"))) }),
            $"positive control: expected the seed item back, got {Py.Repr(control.Items)}");
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails,
    /// naming what came back rather than turning an over-permissive accept into a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string?> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
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
}
