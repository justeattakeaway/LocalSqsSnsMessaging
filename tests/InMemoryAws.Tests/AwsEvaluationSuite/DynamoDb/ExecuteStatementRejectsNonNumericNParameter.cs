// Translated from behavioral/dynamodb/check_execute_statement_rejects_non_numeric_n_parameter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-rejects-non-numeric-n-parameter</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementRejectsNonNumericNParameter
{
    private static readonly string[] BadValues = ["abc", "", "1e", "--1"];

    /// <summary>
    /// ExecuteStatement rejects an N parameter that is not a numeric string with
    /// ValidationException in a WHERE comparison, an INSERT value and an UPDATE SET, and
    /// never writes or changes the item the statement targeted.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html#HowItWorks.DataTypes.Number
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-rejects-non-numeric-n-parameter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);

        const string basePk = "probe-pk";
        const string baseSk = "probe-sk";
        await ddb.PutItemAsync(tableName, Item(("pk", S(basePk)), ("sk", S(baseSk)), ("amt", N("5"))));

        // Positive control: a well-formed numeric parameter succeeds in each of the three
        // contexts below, so a rejection is attributable to the malformed value, not to
        // ExecuteStatement or these statement forms being broken.
        var control = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{tableName}\" WHERE \"pk\" = ? AND \"amt\" > ?",
            Parameters = [S(basePk), N("0")],
        });
        Ensure(control.Items is { Count: > 0 }, $"positive control WHERE comparison found no items: {Py.Repr(control.Items)}");

        foreach (var value in BadValues)
        {
            var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{tableName}\" WHERE \"pk\" = ? AND \"amt\" > ?",
                Parameters = [S(basePk), N(value)],
            }));
            Ensure(code == "ValidationException", $"WHERE comparison with N={Py.Repr(value)}: expected ValidationException, got {Py.Repr(code)}");
            var expected = ExpectedMessage(value);
            Ensure(message == expected, $"WHERE comparison with N={Py.Repr(value)}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        foreach (var value in BadValues)
        {
            var insertSk = $"insert-sk-{(value.Length > 0 ? value : "empty")}";
            var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"INSERT INTO \"{tableName}\" VALUE {{'pk': ?, 'sk': ?, 'amt': ?}}",
                Parameters = [S(basePk), S(insertSk), N(value)],
            }));
            Ensure(code == "ValidationException", $"INSERT value with N={Py.Repr(value)}: expected ValidationException, got {Py.Repr(code)}");
            var expected = ExpectedMessage(value);
            Ensure(message == expected, $"INSERT value with N={Py.Repr(value)}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(basePk)), ("sk", S(insertSk))), ConsistentRead = true })).Item;
            Ensure(item is null, $"INSERT value with N={Py.Repr(value)}: item was written despite the rejection: {Py.Repr(item)}");
        }

        foreach (var value in BadValues)
        {
            var (code, message) = await ErrorCodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{tableName}\" SET \"amt\" = ? WHERE \"pk\" = ? AND \"sk\" = ?",
                Parameters = [N(value), S(basePk), S(baseSk)],
            }));
            Ensure(code == "ValidationException", $"UPDATE SET with N={Py.Repr(value)}: expected ValidationException, got {Py.Repr(code)}");
            var expected = ExpectedMessage(value);
            Ensure(message == expected, $"UPDATE SET with N={Py.Repr(value)}: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Item(("pk", S(basePk)), ("sk", S(baseSk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(final["amt"], N("5")), $"UPDATE SET with an invalid N must never change the item, got amt={Py.Repr(final["amt"])}");
    }

    /// <summary>The exact ValidationException message AWS returns for this malformed N value.</summary>
    private static string ExpectedMessage(string value) =>
        value.Length == 0 ? "The parameter cannot be converted to a numeric value" : $"The parameter cannot be converted to a numeric value: {value}";

    /// <summary>
    /// Call fn(); return the botocore Error.Code and Error.Message if a ClientError is raised.
    /// An unexpected SUCCESS raises, because an emulator accepting input real AWS refuses must
    /// read as that, never as a code mismatch.
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

    /// <summary>Poll DescribeTable until ACTIVE, bounded by the time left in the run.</summary>
    private static async Task<string?> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, FidelityContext ctx)
    {
        var clock = FidelityTime.StartNew();
        var left = ctx.TimeLeftSeconds;
        var budget = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(90.0, (left > 0 ? left : 90.0) - 10.0)));
        string? status = null;
        while (clock.Elapsed < budget)
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                status = "PENDING";
            }
            else
            {
                status = resp!.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {tableName} never went ACTIVE (last={status})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName, ctx);
        return tableName;
    }
}
