// Translated from behavioral/dynamodb/check_create_table_throughput_validation_precedes_keyschema_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-throughput-validation-precedes-keyschema-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableThroughputValidationPrecedesKeyschemaValidation
{
    private const string CheckId = "create-table-throughput-validation-precedes-keyschema-validation";
    private const string ErrValidation = "ValidationException";
    private const string KeySchemaMessage = "Invalid KeySchema: Some index key attribute have no definition";

    private static List<KeySchemaElement> DupHashKeySchema() => [new("pk", KeyType.HASH), new("pk", KeyType.HASH)];

    private static Func<Task> Teardown(IAmazonDynamoDB ddb, string table) => async () =>
    {
        try
        {
            await ddb.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    };

    /// <summary>The (code, message) CreateTable is refused with; a table it creates anyway is torn down.</summary>
    private static async Task<(string? Code, string? Message)> CreateRefused(IAmazonDynamoDB ddb, FidelityContext ctx, CreateTableRequest request)
    {
        var (created, error) = await FidelityContext.Capture(() => ddb.CreateTableAsync(request));
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        ctx.OnCleanup(Teardown(ddb, request.TableName));
        throw Fail($"CreateTable was accepted: {created!.TableDescription}");
    }

    private static async Task AssertNoTable(IAmazonDynamoDB ddb, string table)
    {
        var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(table));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it SUCCEEDED: {response}");
        }

        Ensure(error.Code == "ResourceNotFoundException", $"a rejected CreateTable left a table behind: {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
        var expected = $"Requested resource not found: Table: {table} not found";
        Ensure(error.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(error.Message)}");
    }

    /// <summary>
    /// CreateTable answers a missing or stray ProvisionedThroughput before an invalid KeySchema in
    /// the same request; resolving the throughput mismatch exposes the KeySchema error underneath.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-throughput-validation-precedes-keyschema-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var name1 = FidelityContext.UniqueName("sim2real-behav-ddb-prec-nobm-");
        var (code1, msg1) = await CreateRefused(ddb, ctx, new CreateTableRequest
        {
            TableName = name1,
            AttributeDefinitions = [],
            KeySchema = DupHashKeySchema(),
        });
        Ensure(code1 == ErrValidation, $"expected {ErrValidation}, got {code1}");
        const string expected1 =
            "One or more parameter values were invalid: ReadCapacityUnits and WriteCapacityUnits "
            + "must both be specified when BillingMode is PROVISIONED";
        Ensure(msg1 == expected1, $"expected message {Py.Repr(expected1)}, got {Py.Repr(msg1)}");
        await AssertNoTable(ddb, name1);

        var name2 = FidelityContext.UniqueName("sim2real-behav-ddb-prec-ppr-");
        var (code2, msg2) = await CreateRefused(ddb, ctx, new CreateTableRequest
        {
            TableName = name2,
            AttributeDefinitions = [],
            KeySchema = DupHashKeySchema(),
            BillingMode = BillingMode.PAY_PER_REQUEST,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        Ensure(code2 == ErrValidation, $"expected {ErrValidation}, got {code2}");
        const string expected2 =
            "One or more parameter values were invalid: Neither ReadCapacityUnits nor "
            + "WriteCapacityUnits can be specified when BillingMode is PAY_PER_REQUEST";
        Ensure(msg2 == expected2, $"expected message {Py.Repr(expected2)}, got {Py.Repr(msg2)}");
        await AssertNoTable(ddb, name2);

        var name3 = FidelityContext.UniqueName("sim2real-behav-ddb-prec-ks-");
        var (code3, msg3) = await CreateRefused(ddb, ctx, new CreateTableRequest
        {
            TableName = name3,
            AttributeDefinitions = [],
            KeySchema = DupHashKeySchema(),
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        });
        Ensure(code3 == ErrValidation, $"expected {ErrValidation}, got {code3}");
        Ensure(msg3 == KeySchemaMessage, $"expected message {Py.Repr(KeySchemaMessage)}, got {Py.Repr(msg3)}");
        await AssertNoTable(ddb, name3);

        var name4 = FidelityContext.UniqueName("sim2real-behav-ddb-prec-ok-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name4,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(ddb, name4));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < 300.0)
        {
            desc = (await ddb.DescribeTableAsync(name4)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(desc is not null && desc.TableStatus == TableStatus.ACTIVE, $"positive control not ACTIVE: {desc}");
        var throughput = desc.ProvisionedThroughput;
        Ensure(
            (throughput?.ReadCapacityUnits, throughput?.WriteCapacityUnits) == (5, 5),
            $"expected explicit throughput 5/5 to be honored, got {Py.Repr((throughput?.ReadCapacityUnits, throughput?.WriteCapacityUnits))}");
    }
}
