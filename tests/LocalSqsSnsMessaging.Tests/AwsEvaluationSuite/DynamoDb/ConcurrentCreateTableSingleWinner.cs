// Translated from behavioral/dynamodb/check_concurrent_create_table_single_winner.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-create-table-single-winner</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentCreateTableSingleWinner
{
    private const string Check = "concurrent-create-table-single-winner";
    private const string ErrResourceInUse = "ResourceInUseException";

    /// <summary>
    /// N concurrent CreateTable calls racing to create a table with the SAME name must yield
    /// EXACTLY ONE winner; every other racer gets ResourceInUseException, and the table ends up
    /// existing exactly once. Tagged axis:rejection: the graded half is the losers' error code.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbTransientStates, "the winning table is ACTIVE at once, so losers get 'Table already exists: <name>' instead of 'Attempt to change a resource which is still in use: Table is being created: <name>'")]
    [DisplayName("dynamodb.concurrent-create-table-single-winner")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-race-tbl-");
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        const int n = 8;

        Task<CreateTableResponse> TryCreate() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

        object[] results;
        using (ctx.Concurrently())
        {
            results = await RunConcurrent([.. Enumerable.Range(0, n).Select(_ => (Func<Task<CreateTableResponse>>)TryCreate)]);
        }

        var successes = ConcurrentSuccesses(results);
        Ensure(successes == 1, $"expected exactly one winning CreateTable, got {successes}: {Py.Repr(results.Select(Describe))}");

        var winner = results.OfType<CreateTableResponse>().First();
        Ensure(winner.TableDescription.TableName == name, Py.Repr(winner.TableDescription.TableName));

        var codes = ConcurrentErrorCodes(results);
        Ensure(codes.Count == n - 1, Py.Repr(codes));
        Ensure(codes.All(c => c == ErrResourceInUse), Py.Repr(codes));
        var expected = $"Attempt to change a resource which is still in use: Table is being created: {name}";
        var messages = results.OfType<Amazon.Runtime.AmazonServiceException>().Select(r => AwsError.From(r).Message).ToList();
        Ensure(
            messages.Count == n - 1 && messages.All(m => m == expected),
            $"expected every loser to get {Py.Repr(expected)}, got {Py.Repr(messages)}");

        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var described = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table;
        Ensure(described.TableName == name, Py.Repr(described.TableName));
    }

    private static string Describe(object r) => r is Exception ex ? $"{ex.GetType().Name}({ex.Message})" : "ok";

    /// <summary>Runs every fn concurrently; each result is its response or the exception it raised.</summary>
    private static async Task<object[]> RunConcurrent<T>(IReadOnlyList<Func<Task<T>>> fns)
        where T : class =>
        await Task.WhenAll(fns.Select(fn => Task.Run<object>(async () =>
        {
            try
            {
                return await fn();
            }
#pragma warning disable CA1031 // as the Python check, every failure is recorded as a result
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        })));

    private static int ConcurrentSuccesses(object[] results) => results.Count(r => r is not Exception);

    private static List<string?> ConcurrentErrorCodes(object[] results) =>
    [
        .. results.OfType<Exception>().Select(r => r is Amazon.Runtime.AmazonServiceException ex ? AwsError.From(ex).Code : r.GetType().Name),
    ];
}
