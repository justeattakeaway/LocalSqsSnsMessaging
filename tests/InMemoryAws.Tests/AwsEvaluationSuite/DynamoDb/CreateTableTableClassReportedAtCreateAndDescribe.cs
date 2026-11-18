// Translated from behavioral/dynamodb/check_create_table_table_class_reported_at_create_and_describe.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-table-class-reported-at-create-and-describe</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableTableClassReportedAtCreateAndDescribe
{
    /// <summary>
    /// TableClass given at CreateTable is reported in CreateTable's own response and in
    /// DescribeTable; a table created without TableClass omits the block from CreateTable's response.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-table-class-reported-at-create-and-describe")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var classed = FidelityContext.UniqueName("sim2real-behav-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = classed,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            TableClass = TableClass.STANDARD_INFREQUENT_ACCESS,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(classed));
        await WaitActive(ddb, classed);
        var createdSummary = Boto(created.TableDescription.TableClassSummary);
        Ensure(
            Py.Equal(createdSummary, new Dictionary<string, object?> { ["TableClass"] = "STANDARD_INFREQUENT_ACCESS" }),
            $"CreateTable's own response must carry TableClassSummary for the class it was given, got {Py.Repr(createdSummary)}");

        var described = (await ddb.DescribeTableAsync(classed)).Table;
        var describedSummary = Boto(described.TableClassSummary);
        Ensure(
            Py.Equal(describedSummary, new Dictionary<string, object?> { ["TableClass"] = "STANDARD_INFREQUENT_ACCESS" }),
            $"DescribeTable must carry the same TableClassSummary, got {Py.Repr(describedSummary)}");

        var @default = FidelityContext.UniqueName("sim2real-behav-");
        var createdDefault = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = @default,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(@default));
        await WaitActive(ddb, @default);
        Ensure(
            createdDefault.TableDescription.TableClassSummary is null,
            $"a table created with no TableClass must omit TableClassSummary from CreateTable's response, got {Py.Repr(Boto(createdDefault.TableDescription.TableClassSummary))}");
    }

    private static Task WaitActive(IAmazonDynamoDB ddb, string table) =>
        ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

    private static Dictionary<string, object?>? Boto(TableClassSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (summary.TableClass is not null)
        {
            result["TableClass"] = summary.TableClass.Value;
        }

        if (summary.LastUpdateDateTime is not null)
        {
            result["LastUpdateDateTime"] = summary.LastUpdateDateTime;
        }

        return result;
    }
}
