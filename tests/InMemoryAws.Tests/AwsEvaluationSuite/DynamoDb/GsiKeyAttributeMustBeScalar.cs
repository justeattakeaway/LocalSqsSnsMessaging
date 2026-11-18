// Translated from behavioral/dynamodb/check_gsi_key_attribute_must_be_scalar.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-key-attribute-must-be-scalar</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiKeyAttributeMustBeScalar
{
    /// <summary>
    /// A secondary index key must be String, Number or Binary. A list or map typed key is refused at
    /// CreateTable, because an index cannot order a document type.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-key-attribute-must-be-scalar")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        var err = await Error(
            () => ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("doc", new ScalarAttributeType("L")),
                ],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "idx1",
                        KeySchema = [new KeySchemaElement("doc", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }),
            "a GSI keyed on a list-typed attribute");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code ?? "None"}");
        const string expectedMessage =
            "1 validation error detected: Value 'L' at 'attributeDefinitions.2.member.attributeType' "
            + "failed to satisfy constraint: Member must satisfy enum value set: [B, N, S]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        Ensure(!(await ddb.ListTablesAsync(new ListTablesRequest())).TableNames.Contains(table), "the refused table must not exist");
    }

    /// <summary>The Error dict from a call that MUST be refused.</summary>
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
