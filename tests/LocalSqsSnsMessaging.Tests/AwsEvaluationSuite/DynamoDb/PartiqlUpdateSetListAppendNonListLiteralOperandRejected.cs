// Translated from behavioral/dynamodb/check_partiql_update_set_list_append_non_list_literal_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-set-list-append-non-list-literal-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateSetListAppendNonListLiteralOperandRejected
{
    private const string CheckId = "partiql-update-set-list-append-non-list-literal-operand-rejected";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>
    /// PartiQL UPDATE SET's list_append refuses a non-list literal operand with
    /// ValidationException and leaves the stored list untouched; a list literal operand succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-set-list-append-non-list-literal-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlapprej-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        await ddb.PutItemAsync(table, Item(("pk", S("u1")), ("l", L(N("1"), N("2")))));

        var (code, message) = await Error(async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET l = list_append(l, 3) WHERE pk = ?",
            Parameters = [S("u1")],
        }));
        Ensure(
            code == "ValidationException",
            $"list_append with a Number literal operand answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expected = "Incorrect operand type for operator or function; operator or function: list_append, operand type: N";
        Ensure(message == expected, $"list_append refusal message is {Py.Repr(message)}, expected {Py.Repr(expected)}");
        var afterRefusal = await Stored(ddb, table, "u1");
        Ensure(
            Py.Equal(afterRefusal!["l"], L(N("1"), N("2"))),
            $"after the refused list_append the stored list is {Py.Repr(afterRefusal.GetValueOrDefault("l"))}, expected the original [1, 2] untouched");

        // Positive control: the identical statement with a list literal operand succeeds.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET l = list_append(l, [3]) WHERE pk = ?",
            Parameters = [S("u1")],
        });
        var accepted = await Stored(ddb, table, "u1");
        Ensure(
            Py.Equal(accepted!["l"], L(N("1"), N("2"), N("3"))),
            $"list_append(l, [3]) after the refusal stored {Py.Repr(accepted.GetValueOrDefault("l"))}, expected [1, 2, 3]");
    }
}
