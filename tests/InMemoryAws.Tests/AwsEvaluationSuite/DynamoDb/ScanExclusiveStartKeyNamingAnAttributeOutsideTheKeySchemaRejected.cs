// Translated from behavioral/dynamodb/check_scan_exclusive_start_key_naming_an_attribute_outside_the_key_schema_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-exclusive-start-key-naming-an-attribute-outside-the-key-schema-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanExclusiveStartKeyNamingAnAttributeOutsideTheKeySchemaRejected
{
    private const string Check = "scan-exclusive-start-key-naming-an-attribute-outside-the-key-schema-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary><c>(Code, Message)</c> for the <c>ClientError</c> <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Scan rejects an ExclusiveStartKey carrying an attribute outside the table's key schema,
    /// with ValidationException, and the rejected call writes nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-exclusive-start-key-naming-an-attribute-outside-the-key-schema-rejected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);
        var seed = new SortedSet<string>(Enumerable.Range(0, 5).Select(i => $"item-{i:D3}"), StringComparer.Ordinal);
        foreach (var pk in seed)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk))));
        }

        // Positive control: a real, freshly minted ExclusiveStartKey continues the scan normally.
        var first = await ddb.ScanAsync(new ScanRequest { TableName = table, Limit = 1, ConsistentRead = true });
        var realKey = first.LastEvaluatedKey;
        var control = await ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = realKey, ConsistentRead = true });
        var rest = new SortedSet<string>(control.Items.Select(item => item["pk"].S), StringComparer.Ordinal);
        var expectedRest = new SortedSet<string>(seed.Where(s => s != realKey["pk"].S), StringComparer.Ordinal);
        Ensure(
            rest.SetEquals(expectedRest),
            $"positive control: resuming after {Py.Repr(realKey["pk"].S)} returned {Py.Repr(rest)}, expected {Py.Repr(expectedRest)}");

        // Negative: the same key, plus one attribute the table's key schema does not declare.
        var badKey = new Dictionary<string, AttributeValue>(realKey, StringComparer.Ordinal) { ["extra"] = S("unexpected") };
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = badKey }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(message == "The provided starting key is invalid", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // The rejected call wrote nothing: the table still holds exactly the five seeded items.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var finalPks = new SortedSet<string>(final.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(finalPks.SetEquals(seed), $"expected the original seed {Py.Repr(seed)} unchanged, got {Py.Repr(finalPks)}");
    }
}
