// Translated from behavioral/dynamodb/check_query_key_condition_expression_contains_function_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-expression-contains-function-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionExpressionContainsFunctionRejected
{
    private const string Check = "query-key-condition-expression-contains-function-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Query refuses a KeyConditionExpression naming contains(), the function being valid only
    /// in a FilterExpression; the same attribute admits the matching read via FilterExpression.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-expression-contains-function-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbqkc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 150);

        const string pk = "p";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("s1"))));

        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "contains(pk, :v)",
            ExpressionAttributeValues = Item((":v", S(pk))),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "Invalid KeyConditionExpression: Invalid operator used in KeyConditionExpression",
            $"unexpected message for contains() in KeyConditionExpression: {Py.Repr(message)}");

        // Positive control: the same partition key equality, through the operator KeyConditionExpression
        // actually accepts, still finds the item.
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S(pk))),
        });
        var sks = resp.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(sks.SetEquals(["s1"]), $"'pk = :v' returned {Py.Repr(sks.Order(StringComparer.Ordinal).ToList())}, expected only 's1'");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }
}
