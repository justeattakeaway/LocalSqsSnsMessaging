// Translated from behavioral/dynamodb/check_create_table_unused_attribute_definitions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-unused-attribute-definitions</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableUnusedAttributeDefinitions
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable whose AttributeDefinitions name an attribute no key schema uses raises ValidationException
    /// with the exact message and creates no table, while the same call without that attribute succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-unused-attribute-definitions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-unused-attr-");
        List<KeySchemaElement> KeySchema() => [new KeySchemaElement("pk", KeyType.HASH)];
        ctx.OnCleanup(() => SafeDeleteTable(ddb, name));

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = KeySchema(),
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        await ddb.DeleteTableAsync(name);
        await ddb.WaitForTableNotExists(name, delaySeconds: 2, maxAttempts: 250);

        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = KeySchema(),
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("never_used", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}");
        const string expectedMsg =
            "One or more parameter values were invalid: Number of attributes in KeySchema does not "
            + "exactly match number of attributes defined in AttributeDefinitions";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        var (existsCode, existsMsg) = await Error(() => ddb.DescribeTableAsync(name));
        Ensure(existsCode == "ResourceNotFoundException", "a rejected CreateTable must not leave a stray table behind");
        var expectedNf = $"Requested resource not found: Table: {name} not found";
        Ensure(existsMsg == expectedNf, $"expected message {Py.Repr(expectedNf)}, got {Py.Repr(existsMsg)}");
    }

    /// <summary>
    /// Call fn(); return the botocore (Error.Code, Error.Message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }
}
