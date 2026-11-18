// Translated from behavioral/dynamodb/check_ddb_partiql_is_type_name_rejects_update_leaves_item_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-partiql-is-type-name-rejects-update-leaves-item-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbPartiqlIsTypeNameRejectsUpdateLeavesItemUnchanged
{
    private const string Check = "ddb-partiql-is-type-name-rejects-update-leaves-item-unchanged";
    private const string Pk = "item1";
    private const string UnsupportedType = "INT4";
    private const string UnsupportedTypeLabel = "Integer4Type";

    /// <summary>
    /// A PartiQL UPDATE whose WHERE clause holds a matching `attr IS &lt;type name&gt;` predicate applies
    /// the write; an IS predicate naming an unsupported type is ValidationException naming that type
    /// and leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-partiql-is-type-name-rejects-update-leaves-item-unchanged")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await ddb.PutItemAsync(name, Item(("pk", S(Pk)), ("flag", Bool(true)), ("counter", N("1"))));

        // Positive control: a supported type name in an UPDATE's WHERE clause runs as a filter and the
        // write that matches it is applied.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{name}\" SET counter = 2 WHERE pk = '{Pk}' AND flag IS BOOL",
        });
        var afterControl = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S(Pk))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(afterControl["counter"], N("2")),
            $"UPDATE ... WHERE flag IS BOOL left counter={Py.Repr(afterControl["counter"])}, expected " +
            "{'N': '2'} -- the matching write was not applied");

        // The exact message names the unsupported type and its position in the statement; the
        // position is this statement's, computed here rather than pinned, so it holds for any
        // generated table name length.
        var statement = $"UPDATE \"{name}\" SET counter = 3 WHERE pk = '{Pk}' AND x IS {UnsupportedType}";
        var predicate = $"x IS {UnsupportedType}";
        var column = statement.IndexOf(predicate, StringComparison.Ordinal) + 1;
        var isTypeRejected = (
            Code: "ValidationException",
            Message: $"Unsupported data type for IS operator: {UnsupportedTypeLabel}. Supported data types " +
                $"are [MISSING, NULL, BOOLEAN, BLOB, STRING, NUMERIC, STRUCT, TUPLE, LIST] at 1:{column}:1");

        Task<ExecuteStatementResponse> Attempt() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement });

        await ExpectError(Attempt, isTypeRejected.Code, isTypeRejected.Message);

        var afterRejection = (await ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S(Pk))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(afterRejection["counter"], N("2")),
            $"counter is {Py.Repr(afterRejection["counter"])} after the rejected UPDATE, expected the " +
            "unchanged {'N': '2'} from the control write -- the rejected statement must not write");
    }

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbistype-upd-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    /// <summary>Calls fn() and asserts it raises exactly (code, message). Raises when fn() succeeds.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ({code}, {Py.Repr(message)}); the call succeeded: {result}");
        }

        Ensure(error.Code == code, $"expected code {Py.Repr(code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
    }
}
