// Translated from behavioral/dynamodb/check_validation_messages_are_customer_intelligible.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.validation-messages-are-customer-intelligible</summary>
[Category("AwsEvaluationSuite")]
public sealed class ValidationMessagesAreCustomerIntelligible
{
    private const string Check = "validation-messages-are-customer-intelligible";
    private const string ErrValidation = "ValidationException";

    // Markers that only appear when an implementation detail has escaped into the
    // response: a decoder's own diagnostic, a language-level type or panic, or a
    // byte offset into the request. None of these is actionable by a caller, and a
    // caller cannot tell from them WHICH input was wrong.
    private static readonly string[] LeakMarkers =
    [
        "union ",
        "Offset(",
        "offset:",
        "panicked",
        "unwrap",
        "Traceback",
        "serde",
        "Deserialize",
        "deserializ",
        "NoneType",
        "nullptr",
        "::",
        "ValueNull",
        "ValueNumber",
        "ValueString",
    ];

    /// <summary>
    /// Seven malformed requests each fail with ValidationException and AWS's exact Message, which
    /// names the violated concept and has no LEAK_MARKERS: it is what tells a caller what to fix.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.validation-messages-are-customer-intelligible")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // (label, call, domain terms any one of which must appear in the message, the exact message)
        var cases = new (string Label, Func<Task> Call, string[] Terms, string ExpectedMessage)[]
        {
            ("malformed table name",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = "ab", Key = Item(("pk", S("k"))) }),
                ["table", "index", "name"],
                "1 validation error detected: Value 'ab' at 'tableName' failed to satisfy constraint: "
                + "Member must have length greater than or equal to 3"),
            ("number precision overflow",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("n")), ("n", N(new string('9', 39)))) }),
                ["number", "precision", "digit", "numeric"],
                "1 validation error detected: Attempting to store more than 38 significant digits in a "
                + "Number"),
            ("empty string set",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("s")), ("s", SS())) }),
                ["set", "empty", "parameter"],
                "1 validation error detected: One or more parameter values were invalid: An string set  "
                + "may not be empty"),
            ("duplicate set member",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("d")), ("s", SS("a", "a"))) }),
                ["duplicate", "set", "collection"],
                "1 validation error detected: One or more parameter values were invalid: Input collection "
                + "[\"a\", \"a\"] contains duplicates."),
            ("empty attribute value",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("e")), ("bad", new AttributeValue())) }),
                ["attributevalue", "attribute", "datatype", "empty"],
                "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes"),
            ("invalid ReturnValues",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("r"))), ReturnValues = new ReturnValue("BOGUS") }),
                ["return", "value"],
                // AWS's verbatim text: no "Value ... at ..." prefix, and its enum list spells UPDATE_NEW.
                "1 validation error detected: Failed to satisfy constraint: Member must satisfy enum value "
                + "set: [ALL_OLD, UPDATED_OLD, ALL_NEW, UPDATE_NEW, NONE]"),
            ("unparseable condition expression",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("c"))), ConditionExpression = "attribute_exists(" }),
                ["expression", "syntax", "token"],
                "1 validation error detected: Invalid ConditionExpression: Syntax error; token: \"<EOF>\", "
                + "near: \"(\""),
            ("invalid BatchGetItem ReturnConsumedCapacity enum",
                () => ddb.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new() { [t] = new KeysAndAttributes { Keys = [Item(("pk", S("g")))] } },
                    ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
                }),
                ["returnconsumedcapacity", "enum", "value"],
                "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to "
                + "satisfy constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]"),
            ("empty map-attribute key in BatchWriteItem",
                () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new()
                    {
                        [t] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("m")), ("nested", M(("", S("v"))))) } }],
                    },
                }),
                ["attribute", "name", "empty"],
                "Empty attribute name"),
        };

        foreach (var (label, call, terms, expectedMessage) in cases)
        {
            var (code, message) = await Error(call);
            Ensure(code == ErrValidation, $"{label}: expected {ErrValidation}, got {Py.Repr(code)}");
            Ensure(message == expectedMessage, $"{label}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
            Ensure(message.Trim().Length > 0, $"{label}: {ErrValidation} must carry a non-empty Message");
            var low = message.ToLowerInvariant();
            Ensure(
                terms.Any(term => low.Contains(term, StringComparison.Ordinal)),
                $"{label}: Message must name the subject (one of {Py.Repr(Tuple(terms))}), got {Py.Repr(message)}");
            var leaked = LeakMarkers.Where(m => low.Contains(m.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
            Ensure(
                leaked.Count == 0,
                $"{label}: Message leaks implementation detail {Py.Repr(leaked)} and is not actionable by a caller: {Py.Repr(message)}");
        }
    }

    private static string Tuple(string[] terms) => "(" + string.Join(", ", terms.Select(Py.Repr)) + ")";

    /// <summary>The (code, message) pair <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("real AWS rejects this call, but it SUCCEEDED");
        return (error.Code, error.Message ?? "");
    }
}
