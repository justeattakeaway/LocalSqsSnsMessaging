// Translated from behavioral/dynamodb/check_error_query_without_key_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.error-query-without-key-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorQueryWithoutKeyCondition
{
    private const string CheckId = "error-query-without-key-condition";
    private const string ErrValidation = "ValidationException";

    /// <summary>Query without KeyConditionExpression raises ValidationException; the same table accepts a properly-keyed Query.</summary>
    [Test]
    [DisplayName("dynamodb.error-query-without-key-condition")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("x"))),
        });
        Ensure(control.Items.Count == 0, $"positive control: expected no items on a fresh table, got {Py.Repr(control.Items)}");

        var error = await FidelityContext.CaptureError(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            FilterExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("x"))),
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code ?? "None"}");
        Ensure(
            message == "Either the KeyConditions or KeyConditionExpression parameter must be specified in the request.",
            $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
    }
}
