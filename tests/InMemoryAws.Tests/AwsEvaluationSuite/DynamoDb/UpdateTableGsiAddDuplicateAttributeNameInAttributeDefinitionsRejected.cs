// Translated from behavioral/dynamodb/check_update_table_gsi_add_duplicate_attribute_name_in_attribute_definitions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-table-gsi-add-duplicate-attribute-name-in-attribute-definitions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateTableGsiAddDuplicateAttributeNameInAttributeDefinitionsRejected
{
    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string name)
    {
        try
        {
            await ddb.DeleteTableAsync(name);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    /// <summary>
    /// Adding a GSI with the same AttributeDefinitions name declared twice at different types is
    /// rejected and leaves the table with no new index and its one attribute unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTable.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-table-gsi-add-duplicate-attribute-name-in-attribute-definitions-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-gsidup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var (code, message) = await Error(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("g1", ScalarAttributeType.S),
                new AttributeDefinition("g1", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexUpdates =
            [
                new GlobalSecondaryIndexUpdate
                {
                    Create = new CreateGlobalSecondaryIndexAction
                    {
                        IndexName = "gsi-main",
                        KeySchema = [new KeySchemaElement("g1", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        const string expectedMsg = "Attribute Name is duplicated: g1";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        var afterReject = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            afterReject.GlobalSecondaryIndexes is not { Count: > 0 },
            $"a rejected GSI add must create no index, got {Py.Repr(afterReject.GlobalSecondaryIndexes)}");
        var afterRejectAttrs = afterReject.AttributeDefinitions.ToDictionary(a => a.AttributeName, a => a.AttributeType.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(afterRejectAttrs, new Dictionary<string, string> { ["pk"] = "S" }),
            $"a rejected GSI add must leave AttributeDefinitions alone, got {Py.Repr(afterRejectAttrs)}");
    }
}
