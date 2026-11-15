// Translated from behavioral/dynamodb/check_attribute_name_length_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-name-length-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeNameLengthLimit
{
    private const string Check = "attribute-name-length-limit";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// The modeled AttributeName shape allows up to 65535 bytes for an item attribute name (distinct
    /// from the 255-byte KeySchemaAttributeName cap that applies only to key/index attribute names) —
    /// a name at that max round-trips, and a name one byte over it is rejected.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-name-length-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var maxName = new string('a', 65535);
        await ddb.PutItemAsync(table, Item(("pk", S("long-attr")), (maxName, S("value"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("long-attr"))), ConsistentRead = true })).Item;
        Ensure(
            got is not null && Py.Equal(got.GetValueOrDefault(maxName), S("value")),
            $"expected a {maxName.Length}-byte attribute name to round-trip, got item {Py.Repr(got)}");

        var overName = new string('a', 65536);
        var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, Item(("pk", S("over-attr")), (overName, S("value")))));
        if (error is null)
        {
            throw Fail($"expected PutItem with a {overName.Length}-byte attribute name to be rejected, it succeeded");
        }

        Ensure(error.Code == ErrValidation, $"expected {ErrValidation} for a {overName.Length}-byte attribute name, got {Py.Repr(error.Code)}");
        Ensure(
            error.Message == "1 validation error detected: Attribute name is too large, must be less than 65536 bytes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(error.Message)}");

        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over-attr"))), ConsistentRead = true })).Item;
        Ensure(absent is null, $"expected no item written for the rejected over-length attribute name, got {Py.Repr(absent)}");
    }
}
