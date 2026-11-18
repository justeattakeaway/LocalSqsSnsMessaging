// Translated from behavioral/dynamodb/check_ddb_numeric_sort_key_magnitude_ceiling_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-numeric-sort-key-magnitude-ceiling-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbNumericSortKeyMagnitudeCeilingRejected
{
    private const string Check = "ddb-numeric-sort-key-magnitude-ceiling-rejected";
    private const string ErrValidation = "ValidationException";

    // Documented N range ceiling and one magnitude step past it, mirrored to the negative side.
    private const string MaxMagnitude = "9.9999999999999999999999999999999999999E+125";
    private const string OverMagnitude = "1E+126";

    // The canonical decimal string the ceiling is stored as: exponents fold into plain digits.
    private static readonly string MaxStored = new string('9', 38) + new string('0', 88);

    private const string OverflowMessage =
        "1 validation error detected: Number overflow. Attempting to store a number with " +
        "magnitude larger than supported range";

    /// <summary>
    /// PutItem on an N sort key at the documented magnitude ceiling is accepted and visible to
    /// Query; one magnitude step past it, on either sign, is ValidationException and creates nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-numeric-sort-key-magnitude-ceiling-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        // --- positive control: the documented ceiling itself is accepted as a sort key value ---
        await ddb.PutItemAsync(table, Item(("pk", S("k")), ("sk", N(MaxMagnitude))));
        var atCeiling = await QueryK(ddb, table);
        Ensure(
            Py.Equal(atCeiling, new[] { Item(("pk", S("k")), ("sk", N(MaxStored))) }),
            $"the at-ceiling sort key must be visible to Query exactly once as N {Py.Repr(MaxStored)}, " +
            $"got {Py.Repr(atCeiling)}");

        // --- one magnitude step past the positive ceiling is rejected ---
        var (code, message) = await ErrorCode(() => ddb.PutItemAsync(table, Item(("pk", S("k")), ("sk", N(OverMagnitude)))));
        Ensure(
            code == ErrValidation,
            $"a sort key magnitude of {OverMagnitude} is past the documented ceiling and must be " +
            $"{ErrValidation}, got {code}");
        Ensure(message == OverflowMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // --- the negative mirror is rejected the same way ---
        var (negCode, negMessage) = await ErrorCode(() => ddb.PutItemAsync(table, Item(("pk", S("k")), ("sk", N("-" + OverMagnitude)))));
        Ensure(
            negCode == ErrValidation,
            $"a sort key magnitude of -{OverMagnitude} must also be {ErrValidation}, got {negCode}");
        Ensure(negMessage == OverflowMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(negMessage)}");

        // --- neither rejected write created anything: Query under this partition still sees only
        // the one item the positive control created ---
        var after = await QueryK(ddb, table);
        Ensure(
            Py.Equal(after, new[] { Item(("pk", S("k")), ("sk", N(MaxStored))) }),
            $"a rejected PutItem must not create a sort key entry, Query now sees {Py.Repr(after)}");
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> QueryK(IAmazonDynamoDB ddb, string table) =>
        (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("k"))),
        })).Items;

    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
