// Translated from behavioral/dynamodb/check_create_table_gsi_key_not_in_attribute_definitions_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-gsi-key-not-in-attribute-definitions-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableGsiKeyNotInAttributeDefinitionsRejected
{
    private const string CheckId = "create-table-gsi-key-not-in-attribute-definitions-rejected";

    /// <summary>CreateTable with a GSI keyed on an attribute absent from AttributeDefinitions must be rejected.</summary>
    [Test]
    [DisplayName("dynamodb.create-table-gsi-key-not-in-attribute-definitions-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Positive control: the identical call shape, with the GSI key attribute correctly declared
        // in AttributeDefinitions, must succeed. Without this, "rejects this GSI" is indistinguishable
        // from "rejects every CreateTable with a GSI".
        var goodTable = FidelityContext.UniqueName("sim2real-behav-ddb-goodgsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = goodTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("gsi_pk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(goodTable));
        await WaitTableActive(ddb, goodTable);
        var goodGsiNames = ((await ddb.DescribeTableAsync(goodTable)).Table.GlobalSecondaryIndexes ?? []).Select(g => g.IndexName).ToList();
        Ensure(
            goodGsiNames.Contains("gsi1"),
            $"positive control: CreateTable with a correctly-declared GSI key attribute did not produce the index, got {Py.Repr(goodGsiNames)}");

        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-badgsi-");
        var (keySchema, attrDefs) = HashKeySchema("pk");
        // Registered up front so an unexpectedly-accepted CreateTable cannot leak a table.
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, tableName));

        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = keySchema,
            // 'gsi_pk' is deliberately NOT declared in AttributeDefinitions.
            AttributeDefinitions = attrDefs,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}");
        const string expectedMsg =
            "One or more parameter values were invalid: Some index key attributes are not defined "
            + "in AttributeDefinitions. Keys: [gsi_pk], AttributeDefinitions: [pk]";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The rejected CreateTable must not have created a table at all.
        var (notCreated, notCreatedMsg) = await Error(() => ddb.DescribeTableAsync(tableName));
        Ensure(notCreated == "ResourceNotFoundException", $"rejected CreateTable must not create the table, got {notCreated}");
        var expectedNf = $"Requested resource not found: Table: {tableName} not found";
        Ensure(notCreatedMsg == expectedNf, $"expected message {Py.Repr(expectedNf)}, got {Py.Repr(notCreatedMsg)}");
    }

    /// <summary>
    /// Call fn(); return the botocore (Error.Code, Error.Message) if a ClientError is raised. Raises
    /// if fn() unexpectedly succeeds.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected a ClientError, but the call succeeded: {result}");
    }

    /// <summary>Cleanup for a table the check expects never to exist: delete it if a target created it.</summary>
    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Poll DescribeTable until TableStatus is ACTIVE (a transient 404 counts as not-yet).</summary>
    private static async Task<string> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 60.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            if (error is not null)
            {
                if (error.Code != "ResourceNotFoundException")
                {
                    throw error.Exception;
                }

                status = "PENDING";
            }
            else
            {
                status = response!.Table.TableStatus?.Value;
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status})");
    }

    /// <summary>Return (KeySchema, AttributeDefinitions) for a hash-only table.</summary>
    private static (List<KeySchemaElement> KeySchema, List<AttributeDefinition> AttributeDefinitions) HashKeySchema(string keyName = "pk", string keyType = "S") =>
        ([new KeySchemaElement(keyName, KeyType.HASH)], [new AttributeDefinition(keyName, new ScalarAttributeType(keyType))]);
}
