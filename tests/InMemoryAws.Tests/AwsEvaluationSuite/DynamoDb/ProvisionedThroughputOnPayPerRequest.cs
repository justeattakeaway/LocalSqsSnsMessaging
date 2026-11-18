// Translated from behavioral/dynamodb/check_provisioned_throughput_on_pay_per_request.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.provisioned-throughput-on-pay-per-request</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProvisionedThroughputOnPayPerRequest
{
    private const string ErrValidation = "ValidationException";

    /// <summary>The Error of a call that MUST be refused; RAISES if the call succeeds.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// CreateTable with ProvisionedThroughput and BillingMode=PAY_PER_REQUEST fails with
    /// ValidationException and creates no table, while the same request without throughput succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.provisioned-throughput-on-pay-per-request")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var controlName = FidelityContext.UniqueName("sim2real-behav-ppr-control-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = controlName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(controlName));
        await ddb.WaitForTableExists(controlName, delaySeconds: 2, maxAttempts: 250);
        var controlStatus = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = controlName })).Table.TableStatus;
        Ensure(
            controlStatus == TableStatus.ACTIVE,
            $"positive control: PAY_PER_REQUEST CreateTable with no throughput block must succeed, got status {Py.Repr(controlStatus)}");

        var name = FidelityContext.UniqueName("sim2real-behav-ppr-with-throughput-");
        var err = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        }));
        Ensure(err.Code == ErrValidation, $"expected {ErrValidation}, got {err.Code}");
        const string expected = "One or more parameter values were invalid: Neither ReadCapacityUnits nor " +
            "WriteCapacityUnits can be specified when BillingMode is PAY_PER_REQUEST";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        var existsErr = await Error(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name }));
        Ensure(existsErr.Code == "ResourceNotFoundException", "a rejected CreateTable must not leave a stray table behind");
        var expectedMissing = $"Requested resource not found: Table: {name} not found";
        Ensure(existsErr.Message == expectedMissing, $"expected message {Py.Repr(expectedMissing)}, got {Py.Repr(existsErr.Message)}");
    }
}
