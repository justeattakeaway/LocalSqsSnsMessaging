// Translated from behavioral/dynamodb/check_create_table_attribute_name_over_255_chars_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-attribute-name-over-255-chars-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableAttributeNameOver255CharsRejected
{
    private static readonly string AtLimit = new('k', 255);
    private static readonly string OverLimit = new('k', 256);

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

    /// <summary>
    /// CreateTable accepts a 255-character attribute name and rejects a 256-character one
    /// with ValidationException naming both the KeySchema and AttributeDefinitions members.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.create-table-attribute-name-over-255-chars-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var atLimitTable = FidelityContext.UniqueName("sim2real-behav-attrlen-ok-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = atLimitTable,
            KeySchema = [new KeySchemaElement(AtLimit, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(AtLimit, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(atLimitTable));
        await ddb.WaitForTableExists(atLimitTable, delaySeconds: 2, maxAttempts: 250);
        var desc = (await ddb.DescribeTableAsync(atLimitTable)).Table;
        var gotName = desc.KeySchema[0].AttributeName;
        Ensure(gotName == AtLimit, $"a 255-character attribute name was not preserved, got a name of length {gotName.Length}");

        var overLimitTable = FidelityContext.UniqueName("sim2real-behav-attrlen-bad-");
        var (code, message) = await Error(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = overLimitTable,
            KeySchema = [new KeySchemaElement(OverLimit, KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition(OverLimit, ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {code}: {Py.Repr(message)}");
        var expected =
            $"2 validation errors detected: Value '{OverLimit}' at "
            + "'keySchema.1.member.attributeName' failed to satisfy constraint: Member must have "
            + $"length less than or equal to 255; Value '{OverLimit}' at "
            + "'attributeDefinitions.1.member.attributeName' failed to satisfy constraint: Member "
            + "must have length less than or equal to 255";
        Ensure(message == expected, $"unexpected message {Py.Repr(message)}");

        (code, _) = await Error(() => ddb.DescribeTableAsync(overLimitTable));
        Ensure(code == "ResourceNotFoundException", $"the rejected CreateTable must not have created {Py.Repr(overLimitTable)}, got {code}");
    }
}
