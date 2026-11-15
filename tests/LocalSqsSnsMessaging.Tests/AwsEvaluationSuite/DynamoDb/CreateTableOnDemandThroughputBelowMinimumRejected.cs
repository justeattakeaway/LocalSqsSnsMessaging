// Translated from behavioral/dynamodb/check_create_table_on_demand_throughput_below_minimum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-on-demand-throughput-below-minimum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableOnDemandThroughputBelowMinimumRejected
{
    private const string CheckId = "create-table-on-demand-throughput-below-minimum-rejected";

    private static readonly (string Code, string Message) BelowMinimum = (
        "ValidationException",
        "One or more parameter values were invalid: Requested MaxReadRequestUnits for "
        + "OnDemandThroughput for table is outside of valid range");

    /// <summary>
    /// CreateTable's OnDemandThroughput.MaxReadRequestUnits must be at least 1; a value of 0
    /// is refused and no table is left behind.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-on-demand-throughput-below-minimum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");

        await ExpectError(
            () => ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                Tags = [new Tag { Key = "sim2real", Value = CheckId }],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 0, MaxWriteRequestUnits = 5 },
            }),
            BelowMinimum.Code,
            BelowMinimum.Message);

        // Positive control: the same shape with a valid ceiling succeeds, proving the refusal above
        // was about the value and not the table's other properties.
        var ctrlTable = FidelityContext.UniqueName("sim2real-behav-ctrl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = ctrlTable,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            OnDemandThroughput = new OnDemandThroughput { MaxReadRequestUnits = 1, MaxWriteRequestUnits = 5 },
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(ctrlTable));
        await ddb.WaitForTableExists(ctrlTable, delaySeconds: 2, maxAttempts: 250);

        var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
        var code = error?.Code;
        Ensure(
            code == "ResourceNotFoundException",
            $"the refused CreateTable must not leave a table behind, DescribeTable gave {Py.Repr(code)}");
    }

    /// <summary>Calls fn() and asserts it raises exactly (code, message). Raises when fn() succeeds.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == code, $"expected code {Py.Repr(code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected ({code}, {Py.Repr(message)}); the call succeeded: {result}");
    }
}
