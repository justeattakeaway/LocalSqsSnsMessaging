// Translated from behavioral/dynamodb/check_create_table_stream_enabled_without_a_view_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-stream-enabled-without-a-view-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableStreamEnabledWithoutAViewTypeRejected
{
    private const string CheckId = "create-table-stream-enabled-without-a-view-type-rejected";

    /// <summary>cap_s, shortened when the runner stops the check sooner, keeping 5 s for the failure.</summary>
    private static double WaitCap(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>A cleanup deleting the table, backing off while a create or tag update holds it; a failure raises.</summary>
    private static Func<Task> DropTable(IAmazonDynamoDB ddb, string name)
    {
        async Task<string> Status()
        {
            try
            {
                return (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                return "GONE";
            }
        }

        return async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                // A table just created can still read not-found, so GONE still sends the delete.
                if (await Status() == "DELETING")
                {
                    return;
                }

                try
                {
                    await ddb.DeleteTableAsync(name);
                    return;
                }
                catch (ResourceNotFoundException)
                {
                    return;
                }
                catch (ResourceInUseException) when (attempt != 7)
                {
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(3 * Math.Pow(2, attempt), 60)));
            }
        };
    }

    /// <summary>CreateTable, its delete registered at once, then DescribeTable polled to ACTIVE.</summary>
    private static async Task<TableDescription> NewTable(FidelityContext ctx, IAmazonDynamoDB ddb, CreateTableRequest request)
    {
        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(DropTable(ddb, request.TableName));
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 240.0);
        TableDescription table;
        while (true)
        {
            try
            {
                table = (await ddb.DescribeTableAsync(request.TableName)).Table;
            }
            catch (ResourceNotFoundException) when (clock.Elapsed.TotalSeconds < deadline)
            {
                // DescribeTable can read not-found for a moment after CreateTable.
                table = new TableDescription { TableStatus = TableStatus.CREATING };
            }

            if (table.TableStatus == TableStatus.ACTIVE || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(table.TableStatus == TableStatus.ACTIVE, $"table {request.TableName} settled at {Py.Repr(table.TableStatus?.Value)}");
        return table;
    }

    /// <summary>
    /// Call fn, which AWS must refuse with code; return the error. A success fails the check, and a
    /// different code fails it raised from AWS's own error.
    /// </summary>
    private static async Task<AwsError> Refusal<T>(Func<Task<T>> fn, string code)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            if (error.Code == code)
            {
                return error;
            }

            throw new FidelityAssertionException($"expected {code}, got {error.Code}", error.Exception);
        }

        throw Fail($"expected {code}, but the call succeeded: {Py.Repr(result)}");
    }

    private CreateTableRequest Request(string name, StreamSpecification stream) => new()
    {
        TableName = name,
        StreamSpecification = stream,
        BillingMode = BillingMode.PAY_PER_REQUEST,
        Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
    };

    /// <summary>
    /// CreateTable enabling a stream with no StreamViewType is refused and creates no table; the same
    /// create naming KEYS_ONLY succeeds and reads the stream back.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-stream-enabled-without-a-view-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-dtsv-");
        ctx.OnCleanup(DropTable(ddb, name));

        var err = await Refusal(
            () => ddb.CreateTableAsync(Request(name, new StreamSpecification { StreamEnabled = true })),
            "ValidationException");
        const string noView =
            "One or more parameter values were invalid: Table is being created with a stream enabled, "
            + "UpdateViewType is required";
        var message = err.Message;
        Ensure(message == noView, $"the refusal carried the message {Py.Repr(message)}");

        err = await Refusal(() => ddb.DescribeTableAsync(name), "ResourceNotFoundException");
        var missing = $"Requested resource not found: Table: {name} not found";
        message = err.Message;
        Ensure(message == missing, $"the refused name reads {Py.Repr(message)}, expected {Py.Repr(missing)}");

        var table = await NewTable(ctx, ddb, Request(name, new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY }));
        var read = table.StreamSpecification;
        Ensure(
            read is { StreamEnabled: true } && read.StreamViewType == StreamViewType.KEYS_ONLY,
            $"the accepted create reads StreamSpecification={(read is null ? "None" : $"{{'StreamEnabled': {Py.Repr(read.StreamEnabled)}, 'StreamViewType': {Py.Repr(read.StreamViewType?.Value)}}}")}");
    }
}
