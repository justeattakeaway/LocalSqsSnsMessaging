// Translated from behavioral/dynamodb/check_update_ttl_attribute_name_exceeding_max_length_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-ttl-attribute-name-exceeding-max-length-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTtlAttributeNameExceedingMaxLengthRejected
{
    private const string Check = "update-ttl-attribute-name-exceeding-max-length-rejected";
    private const double EnabledDeadlineS = 240.0;

    /// <summary>
    /// UpdateTimeToLive with an AttributeName over 255 characters (client validation off) is
    /// rejected with ValidationException and its exact message, and TTL stays DISABLED; a
    /// 255-character name is then accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-ttl-attribute-name-exceeding-max-length-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var unvalidatedDdb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ttlmax-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var tooLong = new string('a', 256);
        var rejected = false;
        var error = await FidelityContext.CaptureError(() => unvalidatedDdb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = tooLong },
        }));
        if (error is not null)
        {
            var code = error.Code;
            Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
            var message = error.Message;
            var expected =
                $"1 validation error detected: Value '{tooLong}' at "
                + "'timeToLiveSpecification.attributeName' failed to satisfy constraint: Member must "
                + "have length less than or equal to 255";
            Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
            rejected = true;
        }

        Ensure(rejected, "a 256-character TTL AttributeName must be rejected, not silently accepted");

        var disabled = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table })).TimeToLiveDescription;
        Ensure(disabled.TimeToLiveStatus == TimeToLiveStatus.DISABLED, $"the refused UpdateTimeToLive changed TTL: {Describe(disabled)}");

        var validName = new string('a', 255);
        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = validName },
        });
        var clock = FidelityTime.StartNew();
        TimeToLiveDescription enabled;
        while (true)
        {
            enabled = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table })).TimeToLiveDescription;
            if (enabled.TimeToLiveStatus == TimeToLiveStatus.ENABLED)
            {
                break;
            }

            if (clock.Elapsed > TimeSpan.FromSeconds(EnabledDeadlineS))
            {
                throw Fail($"TTL with a 255-character AttributeName did not reach ENABLED within {EnabledDeadlineS}s: {Describe(enabled)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(enabled.AttributeName == validName, $"expected the 255-character AttributeName stored, got {Describe(enabled)}");
    }

    private static string Describe(TimeToLiveDescription d) =>
        $"{{'TimeToLiveStatus': {Py.Repr(d.TimeToLiveStatus?.Value)}, 'AttributeName': {Py.Repr(d.AttributeName)}}}";
}
