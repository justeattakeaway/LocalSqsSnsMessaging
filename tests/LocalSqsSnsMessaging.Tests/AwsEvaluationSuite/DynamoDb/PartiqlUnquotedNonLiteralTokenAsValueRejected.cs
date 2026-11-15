// Translated from behavioral/dynamodb/check_partiql_unquoted_non_literal_token_as_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-unquoted-non-literal-token-as-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUnquotedNonLiteralTokenAsValueRejected
{
    private const string CheckId = "partiql-unquoted-non-literal-token-as-value-rejected";

    /// <summary>Bound on the table's CREATING -> ACTIVE transition.</summary>
    private const double WaitS = 40.0;

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = WaitS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            string status = (await ddb.DescribeTableAsync(table)).Table.TableStatus;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s; last status {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>Call fn() and grade the ClientError it raises against the exact (code, message) pair.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == code, $"expected error code {Py.Repr(code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
            return error;
        }

        throw Fail($"expected ({code}, {Py.Repr(message)}), but the call succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// An unquoted bareword that is not the true/false/null literal is refused as a value in a
    /// PartiQL INSERT, leaving the item absent, while true still inserts the same way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-unquoted-non-literal-token-as-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlbw-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ddb, table);

        var badStmt = $"INSERT INTO \"{table}\" VALUE {{'pk': 'bareword', 'flag': yes}}";
        var badMessage = $"Unsupported data type: yes under key root at 1:{badStmt.IndexOf("yes", StringComparison.Ordinal) + 1}:3";
        await ExpectError(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = badStmt }), "ValidationException", badMessage);

        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bareword"))), ConsistentRead = true })).Item;
        Ensure(absent is null, $"the refused INSERT left an item behind: {Py.Repr(absent)}");

        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': 'control', 'flag': true}}",
        });
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(control, Item(("pk", S("control")), ("flag", Bool(true)))),
            $"the control INSERT with the true literal stored {Py.Repr(control)}");
    }
}
