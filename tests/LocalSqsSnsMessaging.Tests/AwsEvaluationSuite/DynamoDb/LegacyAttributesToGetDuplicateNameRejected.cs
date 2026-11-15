// Translated from behavioral/dynamodb/check_legacy_attributes_to_get_duplicate_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attributes-to-get-duplicate-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributesToGetDuplicateNameRejected
{
    private const string CheckId = "legacy-attributes-to-get-duplicate-name-rejected";
    private const string Validation = "ValidationException";
    private const string DuplicateMsg = "One or more parameter values were invalid: Duplicate value in attribute name: name";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A duplicated attribute name in a base-table legacy AttributesToGet is ValidationException
    /// for GetItem, BatchGetItem, Query and Scan alike; the same name used once succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attributes-to-get-duplicate-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbattgdup-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("name", S("alpha"))));
        var expectedItems = new[] { Item(("name", S("alpha"))) };

        var (code, message) = await Error(() => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), AttributesToGet = ["name", "name"], ConsistentRead = true }));
        Ensure((code, message) == (Validation, DuplicateMsg), $"GetItem with a duplicate name gave {Py.Repr(code)}: {Py.Repr(message)}");
        var control = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), AttributesToGet = ["name"], ConsistentRead = true });
        Ensure(Py.Equal(control.Item, Item(("name", S("alpha")))), $"control GetItem returned {Py.Repr(control.Item)}");

        BatchGetItemRequest Batch(List<string> attributes) => new()
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [table] = new KeysAndAttributes { Keys = [Key()], AttributesToGet = attributes, ConsistentRead = true },
            },
        };
        (code, message) = await Error(() => ddb.BatchGetItemAsync(Batch(["name", "name"])));
        Ensure((code, message) == (Validation, DuplicateMsg), $"BatchGetItem with a duplicate name gave {Py.Repr(code)}: {Py.Repr(message)}");
        var controlBatch = await ddb.BatchGetItemAsync(Batch(["name"]));
        Ensure(Py.Equal(controlBatch.Responses[table], expectedItems), $"control BatchGetItem returned {Py.Repr(controlBatch.Responses[table])}");

        Dictionary<string, Condition> KeyConditions() => new(StringComparer.Ordinal)
        {
            ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ },
        };
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditions = KeyConditions(), AttributesToGet = ["name", "name"], ConsistentRead = true }));
        Ensure((code, message) == (Validation, DuplicateMsg), $"Query with a duplicate name gave {Py.Repr(code)}: {Py.Repr(message)}");
        var controlQuery = await ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditions = KeyConditions(), AttributesToGet = ["name"], ConsistentRead = true });
        Ensure(Py.Equal(controlQuery.Items, expectedItems), $"control Query returned {Py.Repr(controlQuery.Items)}");

        (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, AttributesToGet = ["name", "name"], ConsistentRead = true }));
        Ensure((code, message) == (Validation, DuplicateMsg), $"Scan with a duplicate name gave {Py.Repr(code)}: {Py.Repr(message)}");
        var controlScan = await ddb.ScanAsync(new ScanRequest { TableName = table, AttributesToGet = ["name"], ConsistentRead = true });
        Ensure(Py.Equal(controlScan.Items, expectedItems), $"control Scan returned {Py.Repr(controlScan.Items)}");

        // None of the four refused calls wrote anything: the table still holds only the one item.
        var final = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(final.Count == 1, $"expected the one item put above and nothing else, got {Py.Repr(final.Items)}");
    }
}
