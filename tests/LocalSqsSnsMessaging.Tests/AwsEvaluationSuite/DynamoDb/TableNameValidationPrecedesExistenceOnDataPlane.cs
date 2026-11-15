// Translated from behavioral/dynamodb/check_table_name_validation_precedes_existence_on_data_plane.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-name-validation-precedes-existence-on-data-plane</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableNameValidationPrecedesExistenceOnDataPlane
{
    private const string CheckId = "table-name-validation-precedes-existence-on-data-plane";
    private const string ErrValidation = "ValidationException";
    private const string ErrNotFound = "ResourceNotFoundException";

    /// <summary>The botocore error code and message <paramref name="fn"/> raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string? Message)> Code(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail("real AWS rejects this call, but it SUCCEEDED");
    }

    /// <summary>
    /// A malformed TableName (too short, too long, or outside [a-zA-Z0-9_.-]) is ValidationException on
    /// every data-plane operation before any existence lookup; well-formed absent names, including a
    /// 3-character one, are ResourceNotFoundException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-name-validation-precedes-existence-on-data-plane")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // A real table proves the caller's wiring is good, so a rejection below is
        // attributable to the name rather than to credentials or endpoint.
        var good = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = good,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(good));
        await ddb.WaitForTableExists(good, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(good, Item(("pk", S("k"))));
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = good, Key = Item(("pk", S("k"))), ConsistentRead = true })).Item;
        Ensure(control is { Count: > 0 }, "positive control failed: a legal name on an existing table must read back");

        // Well-formed but absent: this is the error the malformed names must NOT produce.
        var absent = FidelityContext.UniqueName("sim2real-behav-absent-");
        var (code, message) = await Code(() => ddb.GetItemAsync(new GetItemRequest { TableName = absent, Key = Item(("pk", S("k"))) }));
        Ensure(code == ErrNotFound, $"a well-formed name for an absent table must be {ErrNotFound}, got {Py.Repr(code)}");
        Ensure(message == "Requested resource not found", $"a well-formed name for an absent table: got the message {Py.Repr(message)}");

        var illegal = new List<(string Label, string Name)>
        {
            ("too-short", "ab"),
            ("slash", "bad/name"),
            ("space", "bad name"),
            ("colon", "bad:name"),
            ("too-long", new string('a', 256)),
        };
        const string pattern = "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+";
        var constraint = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["too-short"] = "Member must have length greater than or equal to 3",
            ["slash"] = pattern,
            ["space"] = pattern,
            ["colon"] = pattern,
            ["too-long"] = "Member must have length less than or equal to 255",
        };
        foreach (var (label, name) in illegal)
        {
            var expectedMessage = $"1 validation error detected: Value '{name}' at 'tableName' failed to satisfy constraint: {constraint[label]}";
            var ops = new List<(string Op, Func<Task> Call)>
            {
                ("GetItem", () => ddb.GetItemAsync(new GetItemRequest { TableName = name, Key = Item(("pk", S("k"))) })),
                ("PutItem", () => ddb.PutItemAsync(new PutItemRequest { TableName = name, Item = Item(("pk", S("k"))) })),
                ("Query", () => ddb.QueryAsync(new QueryRequest
                {
                    TableName = name,
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = Item((":p", S("k"))),
                })),
                ("DeleteItem", () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = name, Key = Item(("pk", S("k"))) })),
            };
            foreach (var (op, call) in ops)
            {
                var (got, msg) = await Code(call);
                Ensure(
                    got == ErrValidation,
                    $"{op} with a {label} table name must be {ErrValidation} (name validation precedes existence), got {Py.Repr(got)}");
                Ensure(
                    msg == expectedMessage,
                    $"{op} with a {label} table name: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(msg)}");
            }
        }

        // A 3-character name is legal, so the boundary is inclusive and the rule above
        // is a length floor rather than a blanket rejection of short names.
        (code, message) = await Code(() => ddb.GetItemAsync(new GetItemRequest { TableName = "abc", Key = Item(("pk", S("k"))) }));
        Ensure(code == ErrNotFound, $"a 3-character name is legal and must reach existence checking, not {Py.Repr(code)}");
        Ensure(
            message == "Requested resource not found",
            $"a 3-character name reaches existence checking: got the message {Py.Repr(message)}");
    }
}
