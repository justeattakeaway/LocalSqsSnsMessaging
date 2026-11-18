// Translated from behavioral/dynamodb/check_create_table_lsi_hash_key_differs_from_table_hash_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-lsi-hash-key-differs-from-table-hash-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableLsiHashKeyDiffersFromTableHashKeyRejected
{
    private const string CheckId = "create-table-lsi-hash-key-differs-from-table-hash-key-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable rejects a LocalSecondaryIndex whose leading hash key differs from the base
    /// table's hash key, naming the index and both hash keys. An LSI must share the table's hash key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-lsi-hash-key-differs-from-table-hash-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var ctrlName = FidelityContext.UniqueName("sim2real-behav-lsi-hash-ctrl-");
        var name = FidelityContext.UniqueName("sim2real-behav-lsi-hash-mismatch-");
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, ctrlName));
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, name));

        // Positive control: the identical LSI, sharing the table's hash key, must succeed -- proves
        // CreateTable+LSI works on this account/credentials before the negative below is trusted to
        // mean "rejected for this reason" rather than "rejects everything".
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
        Ensure(ctrlLsiNames.Contains("lsi1"), $"positive control did not create the LSI: {Py.Repr(ctrlDesc.TableName)}");

        // Negative: the LSI's leading key (other) is not the table's hash key (pk).
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("other", ScalarAttributeType.S),
                new AttributeDefinition("lsi_sk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("other", KeyType.HASH), new KeySchemaElement("lsi_sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {Py.Repr(code)}");
        const string expectedMsg =
            "One or more parameter values were invalid: Index KeySchema does not have the same "
            + "leading hash key as table KeySchema for index: lsi1. index hash key: other, table hash "
            + "key: pk";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The rejected CreateTable must not have left a stray table behind.
        var (existsCode, existsMsg) = await Error(() => ddb.DescribeTableAsync(name));
        Ensure(existsCode == "ResourceNotFoundException", $"the refused CreateTable left {Py.Repr(name)} behind: DescribeTable gave {Py.Repr(existsCode)}");
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
