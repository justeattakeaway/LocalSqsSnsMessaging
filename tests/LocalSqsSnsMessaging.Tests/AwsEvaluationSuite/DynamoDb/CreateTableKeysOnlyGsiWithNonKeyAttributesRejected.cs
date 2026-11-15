// Translated from behavioral/dynamodb/check_create_table_keys_only_gsi_with_non_key_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-keys-only-gsi-with-non-key-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableKeysOnlyGsiWithNonKeyAttributesRejected
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable refuses a GSI whose Projection is KEYS_ONLY but names NonKeyAttributes, with
    /// the exact ValidationException message, and creates no table; a KEYS_ONLY GSI with no
    /// NonKeyAttributes is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-keys-only-gsi-with-non-key-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var rejectedName = FidelityContext.UniqueName("sim2real-behav-ddb-gsikonk-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, rejectedName));
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = rejectedName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gsipk", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsipk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY, NonKeyAttributes = ["extra"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)}: {Py.Repr(message)}");
        const string expected =
            "One or more parameter values were invalid: ProjectionType is KEYS_ONLY, but "
            + "NonKeyAttributes is specified";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var (notFoundCode, notFoundMessage) = await Error(() => ddb.DescribeTableAsync(rejectedName));
        Ensure(
            notFoundCode == "ResourceNotFoundException",
            $"a refused CreateTable must create nothing, but DescribeTable gave {Py.Repr(notFoundCode)}");
        var expectedNotFound = $"Requested resource not found: Table: {rejectedName} not found";
        Ensure(notFoundMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(notFoundMessage)}");

        var controlName = FidelityContext.UniqueName("sim2real-behav-ddb-gsikonk-ok-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, controlName));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = controlName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gsipk", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsipk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await ddb.WaitForTableExists(controlName, delaySeconds: 2, maxAttempts: 60);
        var control = (await ddb.DescribeTableAsync(controlName)).Table;
        Ensure(
            control.TableStatus == "ACTIVE",
            $"positive control: a KEYS_ONLY GSI with no NonKeyAttributes must be accepted, got TableStatus={Py.Repr(control.TableStatus?.Value)}");
        var controlProjection = control.GlobalSecondaryIndexes[0].Projection;
        var projection = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ProjectionType"] = controlProjection.ProjectionType?.Value };
        if (controlProjection.NonKeyAttributes is not null)
        {
            projection["NonKeyAttributes"] = controlProjection.NonKeyAttributes;
        }

        Ensure(
            Py.Equal(projection, new Dictionary<string, object?> { ["ProjectionType"] = "KEYS_ONLY" }),
            $"expected the accepted GSI's Projection to carry no NonKeyAttributes, got {Py.Repr(projection)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError <paramref name="fn"/> raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
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
