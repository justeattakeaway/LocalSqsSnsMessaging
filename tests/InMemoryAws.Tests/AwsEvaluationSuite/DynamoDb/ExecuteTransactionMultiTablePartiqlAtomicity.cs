// Translated from behavioral/dynamodb/check_execute_transaction_multi_table_partiql_atomicity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-multi-table-partiql-atomicity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionMultiTablePartiqlAtomicity
{
    private const string CheckId = "execute-transaction-multi-table-partiql-atomicity";
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// PartiQL ExecuteTransaction statements spanning two tables commit together, and when one
    /// statement's WHERE condition fails the transaction is cancelled and the other table's
    /// unconditional UPDATE rolls back too.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-multi-table-partiql-atomicity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-etma-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-etma-b-");
        static AttributeValue KeyVal() => S("shared-id");
        await ddb.PutItemAsync(tableA, Item(("pk", KeyVal()), ("v", N("1"))));
        await ddb.PutItemAsync(tableB, Item(("pk", KeyVal()), ("v", N("1"))));

        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"UPDATE \"{tableA}\" SET v = ? WHERE pk = ?", Parameters = [N("9"), KeyVal()] },
                new ParameterizedStatement { Statement = $"UPDATE \"{tableB}\" SET v = ? WHERE pk = ?", Parameters = [N("9"), KeyVal()] },
            ],
        });
        var aAfter = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableA, Key = Item(("pk", KeyVal())), ConsistentRead = true })).Item;
        var bAfter = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = Item(("pk", KeyVal())), ConsistentRead = true })).Item;
        Ensure(Py.Equal(aAfter["v"], N("9")), $"expected table_a v=9 after commit, got {Py.Repr(aAfter)}");
        Ensure(Py.Equal(bAfter["v"], N("9")), $"expected table_b v=9 after commit, got {Py.Repr(bAfter)}");

        var (code, message) = await ErrorCode(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"UPDATE \"{tableA}\" SET v = ? WHERE pk = ?", Parameters = [N("100"), KeyVal()] },
                new ParameterizedStatement { Statement = $"UPDATE \"{tableB}\" SET v = ? WHERE pk = ? AND v = ?", Parameters = [N("100"), KeyVal(), N("999")] },
            ],
        }));
        Ensure(code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {code}");
        Ensure(
            message == "Transaction cancelled, please refer cancellation reasons for specific reasons [None, ConditionalCheckFailed]",
            $"expected the exact TransactionCanceledException message AWS returns, got {Py.Repr(message)}");

        var aUnchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableA, Key = Item(("pk", KeyVal())), ConsistentRead = true })).Item;
        var bUnchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = Item(("pk", KeyVal())), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(aUnchanged["v"], N("9")),
            $"cross-table sibling statement must roll back with the txn: expected table_a v=9, got {Py.Repr(aUnchanged)}");
        Ensure(Py.Equal(bUnchanged["v"], N("9")), $"expected table_b v=9 (unchanged), got {Py.Repr(bUnchanged)}");
    }

    /// <summary>Return fn()'s error code and message; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the transaction to be canceled, but it SUCCEEDED: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
