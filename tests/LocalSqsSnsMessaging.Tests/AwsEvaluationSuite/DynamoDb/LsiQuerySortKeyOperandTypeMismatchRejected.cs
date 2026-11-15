// Translated from behavioral/dynamodb/check_lsi_query_sort_key_operand_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-sort-key-operand-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQuerySortKeyOperandTypeMismatchRejected
{
    private const string CheckId = "lsi-query-sort-key-operand-type-mismatch-rejected";
    private static readonly string[] Rev0 = ["rev-0"];

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly succeeded where an error was required: {Py.Repr(resp)}");
    }

    /// <summary>
    /// A Query against an LSI rejects begins_with given an N operand with a ValidationException
    /// naming the function and the operand type, while the correctly-typed S operand still selects
    /// on the index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-sort-key-operand-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsitype-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsiSk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-type",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsiSk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("item-1")), ("sk", S("rev-0")), ("lsiSk", S("alpha"))));
        await ddb.PutItemAsync(table, Item(("pk", S("item-1")), ("sk", S("rev-1")), ("lsiSk", S("beta"))));

        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi-type",
            KeyConditionExpression = "pk = :pk AND begins_with(lsiSk, :v)",
            ExpressionAttributeValues = Item((":pk", S("item-1")), (":v", N("1"))),
        }));
        Ensure(code == "ValidationException", $"begins_with(lsiSk, N-operand) on the LSI answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expectedMessage = "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: begins_with, operand type: N";
        Ensure(message == expectedMessage, $"the refusal message is {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi-type",
            KeyConditionExpression = "pk = :pk AND begins_with(lsiSk, :v)",
            ExpressionAttributeValues = Item((":pk", S("item-1")), (":v", S("al"))),
            ConsistentRead = true,
        });
        var got = resp.Items.Select(item => item["sk"].S).ToList();
        Ensure(got.SequenceEqual(Rev0), $"begins_with(lsiSk, 'al') with the correctly-typed S operand returned {Py.Repr(got)}, expected ['rev-0']");
    }
}
