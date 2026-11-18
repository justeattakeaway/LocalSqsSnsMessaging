// Translated from behavioral/dynamodb/check_table_class_update_limit_exceeded_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-class-update-limit-exceeded-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableClassUpdateLimitExceededRejected
{
    private const string CheckId = "table-class-update-limit-exceeded-rejected";

    /// <summary>
    /// A table's class moves STANDARD_INFREQUENT_ACCESS then back to STANDARD; a third change
    /// within 30 days is rejected with LimitExceededException and its exact message, and the table's
    /// class stays at the last accepted value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-class-update-limit-exceeded-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = tableName, TableClass = TableClass.STANDARD_INFREQUENT_ACCESS });
        var desc = await WaitTableActive(ddb, tableName);
        Ensure(
            desc.TableClassSummary?.TableClass == TableClass.STANDARD_INFREQUENT_ACCESS,
            $"expected STANDARD_INFREQUENT_ACCESS after the first change, got {Summary(desc.TableClassSummary)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = tableName, TableClass = TableClass.STANDARD });
        desc = await WaitTableActive(ddb, tableName);
        Ensure(
            desc.TableClassSummary?.TableClass == TableClass.STANDARD,
            $"expected STANDARD after the second change, got {Summary(desc.TableClassSummary)}");

        var err = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = tableName, TableClass = TableClass.STANDARD_INFREQUENT_ACCESS }));
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {Py.Repr(ErrorDict(err))}");
        const string expectedMessage = "Subscriber limit exceeded: Updates to TableClass are limited to 2 times in 30 day(s).";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");

        desc = (await ddb.DescribeTableAsync(tableName)).Table;
        Ensure(
            desc.TableClassSummary?.TableClass == TableClass.STANDARD,
            $"the rejected third change must leave the table at STANDARD, got {Summary(desc.TableClassSummary)}");
    }

    private static Dictionary<string, object?> ErrorDict(AwsError error) => new() { ["Message"] = error.Message, ["Code"] = error.Code };

    private static string Summary(TableClassSummary? summary) =>
        summary is null
            ? "None"
            : Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["TableClass"] = summary.TableClass?.Value,
                ["LastUpdateDateTime"] = summary.LastUpdateDateTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an
    /// over-permissive target ACCEPTING a call real AWS rejects is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
