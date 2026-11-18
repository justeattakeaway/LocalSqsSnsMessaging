// Translated from behavioral/dynamodb/check_partiql_single_quote_literal_escaping.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-single-quote-literal-escaping</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSingleQuoteLiteralEscaping
{
    private const string CheckId = "partiql-single-quote-literal-escaping";

    /// <summary>Call fn(); assert the ClientError it raises carries exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var actual = (error.Code, error.Message);
            Ensure(actual == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(actual)}");
            return;
        }

        throw Fail($"expected ExecuteStatement to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
    }

    private static async Task WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS}s (last={status})");
    }

    /// <summary>
    /// ExecuteStatement writes a doubled single quote inside a string literal as one literal
    /// apostrophe, and refuses a backslash before a quote with ValidationException, writing nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-single-quote-literal-escaping")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-pqesc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);

        // Case A: a doubled single quote inside a string literal is one literal apostrophe.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'doubled', 'val': 'rock''n''roll'}}",
        });
        var written = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("doubled"))), ConsistentRead = true });
        Ensure(
            Py.Equal(written.Item?.GetValueOrDefault("val"), S("rock'n'roll")),
            $"expected val to round-trip as {{'S': \"rock'n'roll\"}}, got {Py.Repr(written.Item)}");

        var selected = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT val FROM \"{tableName}\" WHERE pk = 'doubled'",
        });
        Ensure(
            Py.Equal(selected.Items, new[] { Item(("val", S("rock'n'roll"))) }),
            $"expected SELECT to return the doubled-quote value, got {Py.Repr(selected.Items)}");

        // Case B: a backslash before a quote does not escape it; the statement is refused and
        // nothing is written. Positive control: Case A already proves a well-formed INSERT succeeds.
        var backslashRefused = ("ValidationException", "Statement wasn't well formed, can't be processed: ");
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': 'backslash', 'val': 'rock\\'n\\'roll'}}",
            }),
            backslashRefused.Item1,
            backslashRefused.Item2);
        var unwritten = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S("backslash"))), ConsistentRead = true });
        Ensure(unwritten.Item is null, $"expected no write after refusal, got {Py.Repr(unwritten.Item)}");
    }
}
