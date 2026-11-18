// Translated from behavioral/dynamodb/check_partiql_hash_and_unterminated_block_comment_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-hash-and-unterminated-block-comment-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlHashAndUnterminatedBlockCommentRejected
{
    private const string CheckId = "partiql-hash-and-unterminated-block-comment-rejected";
    private static readonly (string Code, string Message) NotWellFormed = ("ValidationException", "Statement wasn't well formed, can't be processed: ");

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected ExecuteStatement to be rejected, but it SUCCEEDED with {Py.Repr(result)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await ErrorCodeAndMessage(fn);
        Ensure(gotCode == expected.Code, $"expected error code {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)}");
        Ensure(gotMessage == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(gotMessage)}");
    }

    /// <summary>Poll DescribeTable until ACTIVE; CreateTable is eventually consistent.</summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>
    /// ExecuteStatement refuses a '#' line comment and an unterminated '/*' block comment with
    /// the bare not-well-formed envelope, never treating either as an ignorable comment marker.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-hash-and-unterminated-block-comment-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlcmtrej-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitForTableActive(ddb, table);

        await ddb.PutItemAsync(table, Item(("pk", S("A")), ("sk", N("2"))));
        var keyedItem = Item(("pk", S("A")), ("sk", N("2")));

        // Positive control: the same clause with no comment marker succeeds, so the refusals below
        // are attributable to the comment form rather than the clause itself.
        var control = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='A' AND sk=2", ConsistentRead = true })).Items;
        Ensure(Py.Equal(control, new[] { keyedItem }), $"positive control must return the keyed item, got {Py.Repr(control)}");

        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='A' AND sk=2 # a hash comment" }),
            NotWellFormed);
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='A' AND sk=2 /* unterminated" }),
            NotWellFormed);
    }
}
