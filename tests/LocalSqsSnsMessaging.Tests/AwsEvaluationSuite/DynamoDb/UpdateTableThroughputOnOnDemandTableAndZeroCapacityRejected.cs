// Translated from behavioral/dynamodb/check_update_table_throughput_on_on_demand_table_and_zero_capacity_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-throughput-on-on-demand-table-and-zero-capacity-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableThroughputOnOnDemandTableAndZeroCapacityRejected
{
    private const string WithBillingModeMessage =
        "One or more parameter values were invalid: ProvisionedThroughput cannot be specified "
        + "when BillingMode is PAY_PER_REQUEST";

    private const string WithoutBillingModeMessage =
        "One or more parameter values were invalid: Neither ReadCapacityUnits nor WriteCapacityUnits "
        + "can be specified when BillingMode is PAY_PER_REQUEST";

    private const string ZeroReadMessage =
        "1 validation error detected: Value '0' at 'provisionedThroughput.readCapacityUnits' "
        + "failed to satisfy constraint: Member must have value greater than or equal to 1";

    // A field, not a literal: the SDK's analyzer refuses a constant below the model's minimum.
    private static readonly long ZeroCapacity = 0;

    /// <summary>
    /// UpdateTable refuses throughput on an on-demand table, with or without BillingMode, and a zero
    /// ReadCapacityUnits on a provisioned one; neither table changes, and a valid capacity change applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-throughput-on-on-demand-table-and-zero-capacity-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var lax = ctx.Dynamo;
        var @base = FidelityContext.UniqueName("sim2real-behav-ddb-cap-");
        var onDemand = $"{@base}-od";
        var provisioned = $"{@base}-pv";
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = onDemand,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(onDemand), "ResourceNotFoundException"));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = provisioned,
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 },
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(provisioned), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, onDemand);
        await WaitActive(ddb, ctx, provisioned);

        static ProvisionedThroughput Five() => new() { ReadCapacityUnits = 5, WriteCapacityUnits = 5 };
        var (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = onDemand,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            ProvisionedThroughput = Five(),
        }));
        Ensure(
            (code, msg) == ("ValidationException", WithBillingModeMessage),
            $"BillingMode PAY_PER_REQUEST plus throughput on an on-demand table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        (code, msg) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = onDemand, ProvisionedThroughput = Five() }));
        Ensure(
            (code, msg) == ("ValidationException", WithoutBillingModeMessage),
            $"throughput alone on an on-demand table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var desc = await WaitActive(ddb, ctx, onDemand);
        var mode = desc.BillingModeSummary?.BillingMode?.Value;
        Ensure(mode == "PAY_PER_REQUEST", $"after the refusals the on-demand table bills as {Py.Repr(mode)}");
        Ensure(Capacity(desc) == (0, 0), $"after the refusals the on-demand table reports {CapacityRepr(desc)}");

        (code, msg) = await Error(() => lax.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = provisioned,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = ZeroCapacity, WriteCapacityUnits = 1 },
        }));
        Ensure(
            (code, msg) == ("ValidationException", ZeroReadMessage),
            $"ReadCapacityUnits=0 on a provisioned table gave {Py.Repr(code)}: {Py.Repr(msg)}");
        desc = await WaitActive(ddb, ctx, provisioned);
        Ensure(Capacity(desc) == (1, 1), $"after the refused zero the provisioned table reports {CapacityRepr(desc)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = provisioned,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 2, WriteCapacityUnits = 1 },
        });
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (ctx.TimeLeftSeconds is var left && left != 0 ? left : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        while (true)
        {
            desc = await WaitActive(ddb, ctx, provisioned);
            if (Capacity(desc) == (2, 1) || clock.Elapsed >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(Capacity(desc) == (2, 1), $"a valid capacity change left the table at {CapacityRepr(desc)}");
    }

    private static (long? Read, long? Write) Capacity(TableDescription desc) =>
        (desc.ProvisionedThroughput?.ReadCapacityUnits, desc.ProvisionedThroughput?.WriteCapacityUnits);

    private static string CapacityRepr(TableDescription desc) =>
        $"({Capacity(desc).Read?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"}, {Capacity(desc).Write?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"})";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (left != 0 ? left : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus?.Value}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
