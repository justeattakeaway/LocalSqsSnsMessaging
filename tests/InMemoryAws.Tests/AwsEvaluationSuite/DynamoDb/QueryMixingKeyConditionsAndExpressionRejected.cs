// Translated from behavioral/dynamodb/check_query_mixing_key_conditions_and_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-mixing-key-conditions-and-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryMixingKeyConditionsAndExpressionRejected
{
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }

    /// <summary>
    /// The legacy KeyConditions parameter and KeyConditionExpression cannot both be sent. AWS names
    /// which parameters collided, so a target answering a generic error loses that.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-mixing-key-conditions-and-expression-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await ErrorCode(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :v",
                ExpressionAttributeValues = Item((":v", S("a"))),
                KeyConditions = new() { ["pk"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("a")] } },
            }),
            "a Query sending both KeyConditions and KeyConditionExpression");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected =
            "Can not use both expression and non-expression parameters in the same request: " +
            "Non-expression parameters: {KeyConditions} Expression parameters: {KeyConditionExpression}";
        Ensure(err.Message == expected, $"the message must name the colliding parameters, got {Py.Repr(err.Message)}");
    }
}
