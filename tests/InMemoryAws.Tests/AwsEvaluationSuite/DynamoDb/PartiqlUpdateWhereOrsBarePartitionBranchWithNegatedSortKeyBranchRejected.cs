// Translated from behavioral/dynamodb/check_partiql_update_where_ors_bare_partition_branch_with_negated_sort_key_branch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-where-ors-bare-partition-branch-with-negated-sort-key-branch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateWhereOrsBarePartitionBranchWithNegatedSortKeyBranchRejected
{
    private const string CheckId = "partiql-update-where-ors-bare-partition-branch-with-negated-sort-key-branch-rejected";

    private static readonly (string Code, string Message) MissingKey = (
        "ValidationException",
        "Where clause does not contain a mandatory equality on all key attributes");

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected ExecuteStatement to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk, string sk)
    {
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S(sk))), ConsistentRead = true })).Item;
        return item is { Count: > 0 } ? item : null;
    }

    /// <summary>
    /// A PartiQL UPDATE whose WHERE ORs a bare partition-key branch with a negated sort-key
    /// branch is rejected with the mandatory key-equality ValidationException; nothing is written.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.update.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-where-ors-bare-partition-branch-with-negated-sort-key-branch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlupdor-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var seed = Item(("pk", S("x")), ("sk", S("y")), ("a", N("0")));
        await ddb.PutItemAsync(table, seed);

        // Neither OR branch carries an equality on both key attributes (one names only pk, the other
        // negates sk), so the WHERE has no mandatory key equality and the statement is rejected.
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" SET a=1 WHERE pk='x' OR pk='x' AND NOT sk='z'" }),
            MissingKey);
        var unchanged = await Stored(ddb, table, "x", "y");
        Ensure(Py.Equal(unchanged, seed), $"a rejected UPDATE changed the existing item: {Py.Repr(unchanged)}");

        // Positive control: the identical SET succeeds once the WHERE is a plain key equality with
        // no OR at all.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\" SET a=1 WHERE pk='x' AND sk='y'" });
        var updated = await Stored(ddb, table, "x", "y");
        Ensure(
            Py.Equal(updated, Item(("pk", S("x")), ("sk", S("y")), ("a", N("1")))),
            $"UPDATE with a plain key-equality WHERE did not land: {Py.Repr(updated)}");
    }
}
