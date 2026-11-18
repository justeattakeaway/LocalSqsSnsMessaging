// Translated from behavioral/dynamodb/check_create_table_provisioned_without_throughput.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-provisioned-without-throughput</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableProvisionedWithoutThroughput
{
    private const string CheckId = "create-table-provisioned-without-throughput";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// CreateTable in PROVISIONED mode without ProvisionedThroughput, and in PAY_PER_REQUEST mode with it,
    /// each raise ValidationException with the exact message and create no table; the same PROVISIONED call
    /// with 5/5 throughput succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-provisioned-without-throughput")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var okName = FidelityContext.UniqueName("sim2real-behav-goodprov-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = okName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(okName));
        await ddb.WaitForTableExists(okName, delaySeconds: 2, maxAttempts: 250);
        var okDesc = (await ddb.DescribeTableAsync(okName)).Table;
        Ensure(okDesc.TableStatus == "ACTIVE", $"positive control table not ACTIVE: {Py.Repr(okDesc.TableStatus?.Value)}");
        var okThroughput = okDesc.ProvisionedThroughput;
        Ensure(
            (okThroughput?.ReadCapacityUnits, okThroughput?.WriteCapacityUnits) == (5, 5),
            $"expected explicit throughput 5/5 to be honored, got {Py.Repr((okThroughput?.ReadCapacityUnits, okThroughput?.WriteCapacityUnits))}");

        var name1 = FidelityContext.UniqueName("sim2real-behav-badprov-");
        var (code1, msg1) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name1,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PROVISIONED,
        }));
        Ensure(code1 == ErrValidation, $"expected {ErrValidation}, got {code1}");
        const string expected1 =
            "One or more parameter values were invalid: ReadCapacityUnits and WriteCapacityUnits "
            + "must both be specified when BillingMode is PROVISIONED";
        Ensure(msg1 == expected1, $"expected message {Py.Repr(expected1)}, got {Py.Repr(msg1)}");
        var (stray1, stray1Msg) = await Error(() => ddb.DescribeTableAsync(name1));
        Ensure(stray1 == "ResourceNotFoundException", "a rejected CreateTable must not create a table");
        var expectedNf1 = $"Requested resource not found: Table: {name1} not found";
        Ensure(stray1Msg == expectedNf1, $"expected message {Py.Repr(expectedNf1)}, got {Py.Repr(stray1Msg)}");

        var name2 = FidelityContext.UniqueName("sim2real-behav-badondemand-");
        var (code2, msg2) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name2,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            ProvisionedThroughput = new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 },
        }));
        Ensure(code2 == ErrValidation, $"expected {ErrValidation}, got {code2}");
        const string expected2 =
            "One or more parameter values were invalid: Neither ReadCapacityUnits nor "
            + "WriteCapacityUnits can be specified when BillingMode is PAY_PER_REQUEST";
        Ensure(msg2 == expected2, $"expected message {Py.Repr(expected2)}, got {Py.Repr(msg2)}");
        var (stray2, stray2Msg) = await Error(() => ddb.DescribeTableAsync(name2));
        Ensure(stray2 == "ResourceNotFoundException", "a rejected CreateTable must not create a table");
        var expectedNf2 = $"Requested resource not found: Table: {name2} not found";
        Ensure(stray2Msg == expectedNf2, $"expected message {Py.Repr(expectedNf2)}, got {Py.Repr(stray2Msg)}");
    }

    /// <summary>
    /// Call fn(); return its botocore (error code, error message). An unexpected SUCCESS raises, because
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

        throw Fail($"expected this CreateTable to be rejected, but it SUCCEEDED: {result}");
    }
}
