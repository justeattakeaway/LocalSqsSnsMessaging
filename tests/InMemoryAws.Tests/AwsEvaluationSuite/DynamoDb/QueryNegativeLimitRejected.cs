// Translated from behavioral/dynamodb/check_query_negative_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-negative-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryNegativeLimitRejected
{
    private const string Check = "query-negative-limit-rejected";
    private const string ErrValidation = "ValidationException";

    // A field, not a literal: the SDK's analyzers reject a literal Limit below 1, as boto3's
    // parameter validation would (the check turns that off).
    private static readonly int NegativeLimit = -1;

    /// <summary>
    /// Query with a negative Limit, sent without client-side validation, raises
    /// ValidationException with the exact message, while Limit=1 still finds the item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-negative-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var ddbRaw = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-qnl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("k"))));

        var (code, message) = await Error(() => ddbRaw.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("k"))),
            Limit = NegativeLimit,
        }));
        Ensure(code == ErrValidation, $"negative Limit: expected {Py.Repr(ErrValidation)}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "1 validation error detected: Value at 'Limit' failed to satisfy constraint: " +
                "Member must have value greater than or equal to 1",
            $"negative Limit: unexpected message {Py.Repr(message)}");

        // Positive control: the same item is still found with a valid Limit.
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("k"))),
            Limit = 1,
            ConsistentRead = true,
        });
        Ensure(
            resp.Count == 1 && Py.Equal(resp.Items[0]["pk"], S("k")),
            $"Limit=1 must still find the item, got Count={Py.Repr(resp.Count)} Items={Py.Repr(resp.Items)}");
    }

    /// <summary>Return (code, message) of the error <paramref name="fn"/> raises; an accepted call fails the check.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
