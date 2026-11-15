// Translated from behavioral/dynamodb/check_partiql_update_from_first_clause_order_runs_as_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-from-first-clause-order-runs-as-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateFromFirstClauseOrderRunsAsWrite
{
    private const string CheckId = "partiql-update-from-first-clause-order-runs-as-write";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) =>
        async () =>
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is not null && !notFound.Contains(error.Code))
            {
                throw error.Exception;
            }
        };

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;

    /// <summary>Assert <c>fn</c> is refused with the exact <c>(code, message)</c> pair.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (code, message) = expected;
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the statement to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL UPDATE statement that opens with FROM instead of UPDATE applies a SET and a
    /// REMOVE; an extra WHERE predicate is a write condition, an absent key raises
    /// ConditionalCheckFailedException creating nothing, and the form also runs in a batch and a transaction.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-from-first-clause-order-runs-as-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlfromfirst-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var item = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("active")), ("extra", S("e1")));
        await ddb.PutItemAsync(table, item);

        // 1. FROM-first form, keywords mixed case, ? parameters, SET then REMOVE, RETURNING ALL NEW *.
        var r1 = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"from \"{table}\" where pk = ? and sk = ? set status = ? remove extra returning all new *",
            Parameters = [S("p1"), S("s1"), S("changed")],
        });
        var want1 = new[] { Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("changed"))) };
        Ensure(Py.Equal(r1.Items, want1), $"RETURNING ALL NEW * after the FROM-first UPDATE returned {Py.Repr(r1.Items)}");
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), want1[0]),
            $"the stored item after the FROM-first UPDATE is {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // 2. An extra WHERE predicate is a write condition: false leaves the item untouched.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' AND status = 'nope' SET status = 'wrong'",
            }),
            Ccf);
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), want1[0]),
            $"a false extra predicate must leave the item untouched, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // Positive control: the same predicate, true, applies.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' AND status = 'changed' SET status = 'confirmed'",
        });
        var confirmed = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("confirmed")));
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), confirmed),
            $"a true extra predicate must apply, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // 3. An absent key raises ConditionalCheckFailedException and creates nothing.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"FROM \"{table}\" WHERE pk = 'absent' AND sk = 'absent' SET status = 'x'",
            }),
            Ccf);
        Ensure(
            await Stored(ddb, table, "absent", "absent") is null,
            "the absent key must not have been created by the refused FROM-first UPDATE");

        // 4. The FROM-first form runs as a BatchExecuteStatement member with no Error on the slot.
        var batch = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements = [new BatchStatementRequest { Statement = $"FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' SET status = 'batched'" }],
        });
        Ensure(batch.Responses[0].Error is null, $"the batch slot carried an error: {Py.Repr(batch.Responses[0].Error?.Message)}");
        var batched = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("batched")));
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), batched),
            $"the BatchExecuteStatement member must have applied, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");

        // 5. The FROM-first form runs inside ExecuteTransaction, and the transaction commits.
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [new ParameterizedStatement { Statement = $"FROM \"{table}\" WHERE pk = 'p1' AND sk = 's1' SET status = 'transacted'" }],
        });
        var transacted = Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("transacted")));
        Ensure(
            Py.Equal(await Stored(ddb, table, "p1", "s1"), transacted),
            $"the ExecuteTransaction member must have committed, got {Py.Repr(await Stored(ddb, table, "p1", "s1"))}");
    }
}
