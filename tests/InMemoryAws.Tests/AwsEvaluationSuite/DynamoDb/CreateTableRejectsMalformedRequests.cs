// Translated from behavioral/dynamodb/check_create_table_rejects_malformed_requests.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-rejects-malformed-requests</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableRejectsMalformedRequests
{
    private const string ErrValidation = "ValidationException";
    private const string ErrNotFound = "ResourceNotFoundException";

    /// <summary>
    /// CreateTable rejects five malformed requests with ValidationException and its exact
    /// message, each graded together with a later, independent DescribeTable call proving no
    /// table was created.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-rejects-malformed-requests")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var laxDdb = ctx.Dynamo;

        var controlName = FidelityContext.UniqueName("sim2real-behav-ddb-createreject-control-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, controlName));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = controlName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        await ddb.WaitForTableExists(controlName, delaySeconds: 2, maxAttempts: 60);
        var control = (await ddb.DescribeTableAsync(controlName)).Table;
        Ensure(
            control.TableStatus == "ACTIVE",
            $"positive control: expected {controlName} ACTIVE, got {Py.Repr(control.TableStatus?.Value)}");

        var gsiName = FidelityContext.UniqueName("sim2real-behav-ddb-createreject-gsiinc-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, gsiName));
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = gsiName,
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
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"GSI INCLUDE with no NonKeyAttributes: expected {ErrValidation}, got {Py.Repr(code)}");
        const string expected = "One or more parameter values were invalid: ProjectionType is INCLUDE, but NonKeyAttributes is not specified";
        Ensure(message == expected, $"GSI INCLUDE with no NonKeyAttributes: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        await AssertNotCreated(ddb, gsiName);

        var lsiName = FidelityContext.UniqueName("sim2real-behav-ddb-createreject-lsiinc-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, lsiName));
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = lsiName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsisk", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsisk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"LSI INCLUDE with no NonKeyAttributes: expected {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == expected, $"LSI INCLUDE with no NonKeyAttributes: expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        await AssertNotCreated(ddb, lsiName);

        (code, message) = await Error(() => laxDdb.CreateTableAsync(new CreateTableRequest
        {
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"no TableName: expected {ErrValidation}, got {Py.Repr(code)}");
        const string expectedNoName = "The parameter 'TableName' is required but was not present in the request";
        Ensure(message == expectedNoName, $"no TableName: expected message {Py.Repr(expectedNoName)}, got {Py.Repr(message)}");

        var samekeyName = FidelityContext.UniqueName("sim2real-behav-ddb-createreject-samekey-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, samekeyName));
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = samekeyName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("pk", KeyType.RANGE)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == ErrValidation, $"same attribute as HASH and RANGE: expected {ErrValidation}, got {Py.Repr(code)}");
        const string expectedSamekey = "Invalid KeySchema: Some index key attribute have no definition";
        Ensure(
            message == expectedSamekey,
            $"same attribute as HASH and RANGE: expected message {Py.Repr(expectedSamekey)}, got {Py.Repr(message)}");
        await AssertNotCreated(ddb, samekeyName);

        var streamfvName = FidelityContext.UniqueName("sim2real-behav-ddb-createreject-streamfv-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, streamfvName));
        (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = streamfvName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = false, StreamViewType = StreamViewType.NEW_IMAGE },
        }));
        Ensure(code == ErrValidation, $"StreamEnabled false with a StreamViewType: expected {ErrValidation}, got {Py.Repr(code)}");
        const string expectedStream =
            "One or more parameter values were invalid: Table is being created with a stream "
            + "disabled, UpdateViewType should not be specified";
        Ensure(
            message == expectedStream,
            $"StreamEnabled false with a StreamViewType: expected message {Py.Repr(expectedStream)}, got {Py.Repr(message)}");
        await AssertNotCreated(ddb, streamfvName);
    }

    /// <summary>
    /// Call fn(); return the botocore (Error.Code, Error.Message). An unexpected SUCCESS raises,
    /// naming what came back, rather than returning a sentinel a mismatch could be confused with.
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
        if (error is not null && error.Code != ErrNotFound)
        {
            throw error.Exception;
        }
    }

    private static async Task AssertNotCreated(IAmazonDynamoDB ddb, string tableName)
    {
        var (code, message) = await Error(() => ddb.DescribeTableAsync(tableName));
        Ensure(code == ErrNotFound, $"expected {Py.Repr(tableName)} to not exist, DescribeTable gave {Py.Repr(code)}");
        var expected = $"Requested resource not found: Table: {tableName} not found";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }
}
