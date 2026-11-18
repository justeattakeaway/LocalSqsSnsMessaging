// Translated from behavioral/dynamodb/check_partiql_update_set_subtraction_applies_and_multiply_divide_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-set-subtraction-applies-and-multiply-divide-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateSetSubtractionAppliesAndMultiplyDivideRejected
{
    private const string CheckId = "partiql-update-set-subtraction-applies-and-multiply-divide-rejected";
    private static readonly (string Code, string Message) Mul = ("ValidationException", "Unsupported operator in Update Expression. Operator: *");
    private static readonly (string Code, string Message) Div = ("ValidationException", "Unsupported operator in Update Expression. Operator: /");
    private const double WaitS = 60.0;

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < WaitS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE within {WaitS:0}s; last status {Py.Repr(status)}");
    }

    /// <summary>Call fn() and grade the ClientError it raises on exact code and message.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {result}");
        }

        Ensure(
            (error.Code, error.Message) == expected,
            $"expected {Py.Repr(expected)}, got {Py.Repr((error.Code, error.Message))}");
    }

    private static async Task<Dictionary<string, AttributeValue>> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;

    /// <summary>
    /// A PartiQL UPDATE SET applies + and - arithmetic in place, but * and / are each a
    /// ValidationException naming the operator, and the stored value is unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-set-subtraction-applies-and-multiply-divide-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbsetarith-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        const string pk = "p1", sk = "s1";
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'sk': ?, 'n': ?}}",
            Parameters = [S(pk), S(sk), N("20")],
        });

        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET n = n - ? WHERE pk = ? AND sk = ?",
            Parameters = [N("5"), S(pk), S(sk)],
        });
        var afterSub = await Stored(ddb, table, pk, sk);
        Ensure(Py.Equal(afterSub["n"], N("15")), $"SET n = n - 5 from n=20 gave {Py.Repr(afterSub.GetValueOrDefault("n"))}, expected N 15");

        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET n = n * ? WHERE pk = ? AND sk = ?",
                Parameters = [N("2"), S(pk), S(sk)],
            }),
            Mul);
        var afterMul = await Stored(ddb, table, pk, sk);
        Ensure(Py.Equal(afterMul["n"], N("15")), $"the refused SET n = n * 2 changed n to {Py.Repr(afterMul.GetValueOrDefault("n"))}");

        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET n = n / ? WHERE pk = ? AND sk = ?",
                Parameters = [N("2"), S(pk), S(sk)],
            }),
            Div);
        var afterDiv = await Stored(ddb, table, pk, sk);
        Ensure(Py.Equal(afterDiv["n"], N("15")), $"the refused SET n = n / 2 changed n to {Py.Repr(afterDiv.GetValueOrDefault("n"))}");

        // Positive control: the surviving + arithmetic still applies after both refusals.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET n = n + ? WHERE pk = ? AND sk = ?",
            Parameters = [N("5"), S(pk), S(sk)],
        });
        var afterAdd = await Stored(ddb, table, pk, sk);
        Ensure(Py.Equal(afterAdd["n"], N("20")), $"SET n = n + 5 from n=15 gave {Py.Repr(afterAdd.GetValueOrDefault("n"))}, expected N 20");
    }
}
