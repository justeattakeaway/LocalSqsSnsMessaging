// Translated from behavioral/dynamodb/check_partiql_write_and_transaction_condition_function_ccf.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-write-and-transaction-condition-function-ccf</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlWriteAndTransactionConditionFunctionCcf
{
    private const string CheckId = "partiql-write-and-transaction-condition-function-ccf";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");
    private static readonly (string Code, string Message) Canceled = (
        "TransactionCanceledException",
        "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed]");

    /// <summary>A pk/sk table for PartiQL write statements.</summary>
    private static async Task<string> Table(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqlwc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetItem(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;

    /// <summary>Call fn(); assert it raises exactly (code, message). An unexpected success raises too.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused with {Py.Repr(expected.Code)}, but it succeeded: {result}");
        }

        Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
    }

    /// <summary>
    /// A PartiQL write WHERE condition built from NOT begins_with or attribute_exists applies when it
    /// holds, else fails (ConditionalCheckFailedException, or TransactionCanceledException inside
    /// ExecuteTransaction) with the item unchanged; attribute_not_exists(pk) never creates an absent key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-write-and-transaction-condition-function-ccf")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx);

        // DELETE WHERE pk AND NOT begins_with(sk, ...): true branch deletes, proven by a read.
        await ddb.PutItemAsync(t, Item(("pk", S("p2")), ("sk", S("c1"))));
        var deleted = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"DELETE FROM \"{t}\" WHERE pk='p2' AND sk='c1' AND NOT begins_with(\"sk\", 'z') RETURNING ALL OLD *",
        });
        Ensure(
            Py.Equal(deleted.Items, new[] { Item(("pk", S("p2")), ("sk", S("c1"))) }),
            $"DELETE with a true NOT begins_with condition must apply, got {Py.Repr(deleted.Items)}");
        Ensure(await GetItem(ddb, t, "p2", "c1") is null, "the deleted item must be gone");

        // False branch: NOT begins_with(sk, 'a') is false when sk begins with 'a', so the DELETE is
        // refused with ConditionalCheckFailedException and the item is left in place.
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a1")), ("flag", S("active"))));
        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"DELETE FROM \"{t}\" WHERE pk='p1' AND sk='a1' AND NOT begins_with(\"sk\", 'a')",
            }),
            Ccf);
        Ensure(
            Py.Equal(await GetItem(ddb, t, "p1", "a1"), Item(("pk", S("p1")), ("sk", S("a1")), ("flag", S("active")))),
            "a failed conditional DELETE must leave the item untouched");

        // UPDATE of a key the table never held, guarded by attribute_not_exists(pk): the write is
        // refused with ConditionalCheckFailedException and creates nothing.
        await ExpectError(
            async () => await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{t}\" SET v = ? WHERE pk='absent' AND sk='absent' AND attribute_not_exists(pk)",
                Parameters = [N("1")],
            }),
            Ccf);
        Ensure(
            await GetItem(ddb, t, "absent", "absent") is null,
            "a refused UPDATE guarded by attribute_not_exists(pk) on an absent key must create nothing");

        // ExecuteTransaction UPDATE conditioned on attribute_exists: true branch commits, proven by a
        // read; false branch cancels the whole transaction with the exact reason, item unchanged.
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("a2")), ("m", M(("sub", S("x"))))));
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement
                {
                    Statement = $"UPDATE \"{t}\" SET v = ? WHERE pk='p1' AND sk='a2' AND attribute_exists(m)",
                    Parameters = [N("2")],
                },
            ],
        });
        Ensure(
            Py.Equal(await GetItem(ddb, t, "p1", "a2"), Item(("pk", S("p1")), ("sk", S("a2")), ("m", M(("sub", S("x")))), ("v", N("2")))),
            "the transaction UPDATE must have applied v=2 once attribute_exists(m) held");

        await ExpectError(
            async () => await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
            {
                TransactStatements =
                [
                    new ParameterizedStatement
                    {
                        Statement = $"UPDATE \"{t}\" SET v = ? WHERE pk='p1' AND sk='a1' AND attribute_exists(m)",
                        Parameters = [N("3")],
                    },
                ],
            }),
            Canceled);
        Ensure(
            Py.Equal(await GetItem(ddb, t, "p1", "a1"), Item(("pk", S("p1")), ("sk", S("a1")), ("flag", S("active")))),
            "a cancelled transaction UPDATE must leave the item untouched");
    }
}
