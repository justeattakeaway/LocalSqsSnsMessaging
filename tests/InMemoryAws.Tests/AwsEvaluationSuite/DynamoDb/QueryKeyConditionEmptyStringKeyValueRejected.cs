// Translated from behavioral/dynamodb/check_query_key_condition_empty_string_key_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-empty-string-key-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionEmptyStringKeyValueRejected
{
    private const string Check = "query-key-condition-empty-string-key-value-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Query refuses a KeyConditionExpression equality test naming an empty string as the
    /// partition or sort key value, each naming only that key, while a non-empty value still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-empty-string-key-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("s1"))));

        const string expectedPkMessage =
            "One or more parameter values are not valid. The AttributeValue for a key " +
            "attribute cannot contain an empty string value. Key: pk";
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S(""))),
        }));
        Ensure(code == ErrValidation, $"an empty-string partition key value must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expectedPkMessage, $"expected the message {Py.Repr(expectedPkMessage)}, got {Py.Repr(message)}");

        const string expectedSkMessage =
            "One or more parameter values are not valid. The AttributeValue for a key " +
            "attribute cannot contain an empty string value. Key: sk";
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk = :v",
            ExpressionAttributeValues = Item((":p", S("p1")), (":v", S(""))),
        }));
        Ensure(code == ErrValidation, $"an empty-string sort key value must be refused {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expectedSkMessage, $"expected the message {Py.Repr(expectedSkMessage)}, got {Py.Repr(message)}");

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p1"))),
        });
        Ensure(
            resp.Count == 1 && resp.Items[0]["sk"].S == "s1",
            $"a non-empty partition key value must still match the seeded item, got {Py.Repr(resp.Items)}");
    }

    /// <summary>(Error.Code, Error.Message) of the error <paramref name="fn"/> raises; an unexpected success fails.</summary>
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
