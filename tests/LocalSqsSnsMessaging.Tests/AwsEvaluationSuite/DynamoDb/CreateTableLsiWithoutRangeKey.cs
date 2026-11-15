// Translated from behavioral/dynamodb/check_create_table_lsi_without_range_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-lsi-without-range-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableLsiWithoutRangeKey
{
    private const string CheckId = "create-table-lsi-without-range-key";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable MUST reject a request that specifies LocalSecondaryIndexes on a base table
    /// whose KeySchema has no range key -- LSIs require a composite (HASH+RANGE) primary key by
    /// definition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-lsi-without-range-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var ctrlName = FidelityContext.UniqueName("sim2real-behav-lsi-ctrl-");
        var name = FidelityContext.UniqueName("sim2real-behav-lsi-no-range-");
        // Registered up front so an unexpectedly-accepted CreateTable cannot leak a table.
        ctx.OnCleanup(() => ddb.DeleteTableAsync(ctrlName));
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, name));

        // Positive control: the identical LSI, on a base table that DOES have a range key, must
        // succeed -- proves CreateTable+LSI works on this account/credentials before the negative
        // below is trusted to mean "rejected for this reason" rather than "rejects everything".
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = ctrlName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsi_sk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsi_sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(ctrlName, delaySeconds: 2, maxAttempts: 250);
        var ctrlDesc = (await ddb.DescribeTableAsync(ctrlName)).Table;
        var ctrlLsiNames = (ctrlDesc.LocalSecondaryIndexes ?? []).Select(idx => idx.IndexName).ToList();
        Ensure(ctrlLsiNames.Contains("lsi1"), $"positive control did not create the LSI: {ctrlDesc.TableName}");

        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)], // no range key
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("lsi_sk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsi_sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}");
        const string expectedMsg =
            "One or more parameter values were invalid: Table KeySchema does not have a range key, "
            + "which is required when specifying a LocalSecondaryIndex";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The rejected CreateTable must not have left a stray table behind.
        var (existsCode, existsMsg) = await Error(() => ddb.DescribeTableAsync(name));
        Ensure(existsCode == "ResourceNotFoundException", existsCode ?? "None");
        var expectedNf = $"Requested resource not found: Table: {name} not found";
        Ensure(existsMsg == expectedNf, $"expected message {Py.Repr(expectedNf)}, got {Py.Repr(existsMsg)}");
    }

    /// <summary>
    /// Call fn(); return (code, message). An unexpected SUCCESS raises rather than returning a
    /// falsy sentinel.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
