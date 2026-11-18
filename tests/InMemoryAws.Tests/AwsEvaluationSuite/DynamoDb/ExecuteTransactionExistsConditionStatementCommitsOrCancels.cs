// Translated from behavioral/dynamodb/check_execute_transaction_exists_condition_statement_commits_or_cancels.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-exists-condition-statement-commits-or-cancels</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionExistsConditionStatementCommitsOrCancels
{
    private const string CheckId = "execute-transaction-exists-condition-statement-commits-or-cancels";
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// ExecuteTransaction treats a standalone EXISTS(SELECT ...) statement as a condition check,
    /// like TransactWriteItems' ConditionCheck: true lets a sibling write statement commit, false
    /// cancels the whole transaction with a ConditionalCheckFailed cancellation reason.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.multiplestatements.transactions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-exists-condition-statement-commits-or-cancels")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("sentinel")), ("sk", S("s1")), ("flag", S("yes"))));
        var targetKey = Item(("pk", S("target")), ("sk", S("s1")));
        await ddb.PutItemAsync(t, Item(("pk", S("target")), ("sk", S("s1")), ("v", N("0"))));

        // EXISTS must read a single item by its full key plus an additional attribute condition.
        var existsStmt = $"EXISTS(SELECT * FROM \"{t}\" WHERE pk = ? AND sk = ? AND flag = ?)";
        var updateStmt = $"UPDATE \"{t}\" SET v = ? WHERE pk = ? AND sk = ?";

        // Positive control: the sentinel's flag matches, so the EXISTS condition is true and the
        // sibling UPDATE commits.
        await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = existsStmt, Parameters = [S("sentinel"), S("s1"), S("yes")] },
                new ParameterizedStatement { Statement = updateStmt, Parameters = [N("1"), S("target"), S("s1")] },
            ],
        });
        var afterCommit = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = targetKey, ConsistentRead = true })).Item;
        Ensure(Py.Equal(afterCommit["v"], N("1")), $"a true EXISTS condition statement must let the sibling UPDATE commit, got {Py.Repr(afterCommit)}");

        // Break exactly one thing: the sentinel item exists, but its flag no longer matches.
        var err = await FidelityContext.CaptureError(() => ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = existsStmt, Parameters = [S("sentinel"), S("s1"), S("no")] },
                new ParameterizedStatement { Statement = updateStmt, Parameters = [N("2"), S("target"), S("s1")] },
            ],
        }));

        Ensure(err is not null, "a false EXISTS condition statement must cancel the transaction");
        Ensure(err.Code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {Py.Repr(err.Code)}");
        const string expectedMessage =
            "Transaction cancelled, please refer cancellation reasons for specific reasons "
            + "[ConditionalCheckFailed, None]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");

        var reasons = err.CancellationReasons();
        Ensure(
            reasons is not null && reasons.Count == 2,
            $"CancellationReasons must be positionally aligned with the 2 statements, got {Py.Repr(reasons?.Select(r => (r.Code, r.Message)).ToList())}");
        var existsReason = reasons![0];
        var updateReason = reasons[1];
        Ensure(existsReason.Code == "ConditionalCheckFailed", $"the EXISTS statement must report ConditionalCheckFailed, got {Py.Repr((existsReason.Code, existsReason.Message))}");
        Ensure(
            updateReason.Code == "None",
            $"the sibling UPDATE must report Code 'None' when the transaction is canceled by its sibling, got {Py.Repr((updateReason.Code, updateReason.Message))}");

        // Effect absent: an independent read shows the canceled transaction's UPDATE never applied.
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = targetKey, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["v"], N("1")), $"a canceled transaction must not have applied its sibling UPDATE, got {Py.Repr(unchanged)}");
    }
}
