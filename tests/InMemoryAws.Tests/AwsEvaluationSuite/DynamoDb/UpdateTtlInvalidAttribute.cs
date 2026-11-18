// Translated from behavioral/dynamodb/check_update_ttl_invalid_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-ttl-invalid-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTtlInvalidAttribute
{
    private const string Check = "update-ttl-invalid-attribute";

    // A field, not a literal: the SDK's analyzer refuses a constant shorter than the model's minimum.
    private static readonly string EmptyName = "";

    /// <summary>
    /// UpdateTimeToLive with an empty AttributeName (client validation off) is rejected with ValidationException and
    /// its exact message and TTL stays DISABLED; the same call with a valid name then returns ENABLING or ENABLED.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-ttl-invalid-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var unvalidatedDdb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
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

        var rejected = false;
        var error = await FidelityContext.CaptureError(() => unvalidatedDdb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = t,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = EmptyName },
        }));
        if (error is not null)
        {
            var code = error.Code;
            Ensure(code == "ValidationException", $"expected ValidationException, got {code}");
            var message = error.Message;
            const string expected = "1 validation error detected: Value '' at 'timeToLiveSpecification.attributeName' "
                + "failed to satisfy constraint: Member must have length greater than or equal to 1";
            Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
            rejected = true;
        }

        Ensure(rejected, "an empty TTL AttributeName must be rejected, not silently accepted");

        var ttl = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = t })).TimeToLiveDescription;
        Ensure(ttl.TimeToLiveStatus == TimeToLiveStatus.DISABLED, Describe(ttl));

        const string validAttr = "expiresAt";
        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = t,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = validAttr },
        });
        var enabled = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = t })).TimeToLiveDescription;
        Ensure(enabled.TimeToLiveStatus == TimeToLiveStatus.ENABLING || enabled.TimeToLiveStatus == TimeToLiveStatus.ENABLED, Describe(enabled));
        Ensure(enabled.AttributeName == validAttr, Describe(enabled));
    }

    private static string Describe(TimeToLiveDescription d) =>
        $"{{'TimeToLiveStatus': {Py.Repr(d.TimeToLiveStatus?.Value)}, 'AttributeName': {Py.Repr(d.AttributeName)}}}";
}
