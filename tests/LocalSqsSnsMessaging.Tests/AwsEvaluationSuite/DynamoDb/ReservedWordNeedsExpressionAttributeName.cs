// Translated from behavioral/dynamodb/check_reserved_word_needs_expression_attribute_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.reserved-word-needs-expression-attribute-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReservedWordNeedsExpressionAttributeName
{
    private const string Check = "reserved-word-needs-expression-attribute-name";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// A bare reserved word ('status') in a FilterExpression raises ValidationException; the
    /// identical semantic filter succeeds once the attribute is aliased via ExpressionAttributeNames.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.reserved-word-needs-expression-attribute-name")]
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
            Tags = [new Tag { Key = "sim2real-behav", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("rw")), ("status", S("active"))));
        var aliased = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "#s = :s",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":s", S("active"))),
            ConsistentRead = true,
        });
        Ensure(aliased.Count == 1, $"expected aliased filter to match 1 item, got {aliased.Count}");
        var error = await FidelityContext.CaptureError(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "status = :s",
            ExpressionAttributeValues = Item((":s", S("active"))),
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(code == ErrValidation, $"expected {ErrValidation} for bare reserved word, got {code ?? "None"}");
        Ensure(
            message == "Invalid FilterExpression: Attribute name is a reserved keyword; reserved keyword: status",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
    }
}
