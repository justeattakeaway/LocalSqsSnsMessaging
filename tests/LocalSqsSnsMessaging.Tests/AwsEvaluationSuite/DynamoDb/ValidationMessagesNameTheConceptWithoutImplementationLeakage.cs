// Translated from behavioral/dynamodb/check_validation_messages_name_the_concept_without_implementation_leakage.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.validation-messages-name-the-concept-without-implementation-leakage</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class ValidationMessagesNameTheConceptWithoutImplementationLeakage
{
    private const string Check = "validation-messages-name-the-concept-without-implementation-leakage";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Substrings that betray the implementation behind the endpoint rather than describing the
    /// customer's mistake: decoder diagnostics, byte offsets, language type and namespace markers, and
    /// crash residue.
    /// </summary>
    private static readonly string[] Leakage =
    [
        "::", "panic", "unwrap", "serde", "traceback", "stack backtrace", "goroutine",
        "nullpointerexception", "java.lang", "sun.reflect", "site-packages", ".rs:", ".py:", ".java:",
        "byte offset", "at offset", "expected value at line", "option<", "result<", "struct ",
        "no such field", "cannot deserialize", "deserialization", "unmarshal", "typeerror",
    ];

    /// <summary>A file-and-line or stack-frame shape, which no customer-facing message should ever carry.</summary>
    [GeneratedRegex(@"\b\w+\.(rs|py|java|go|kt|cpp)\b|\bline \d+\b|\bcolumn \d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex Frame();

    /// <summary>
    /// Each of eight ValidationException messages names the violated concept (parameter, type, limit)
    /// and contains no implementation leakage such as decoder diagnostics, offsets or stack-frame shapes;
    /// refused writes store nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.validation-messages-name-the-concept-without-implementation-leakage")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-msg-");
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

        // Positive control: the same operations on the same table succeed, so the messages below come
        // from the validation surface and not from a target that refuses everything.
        await ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("ctl")), ("n", N("1"))) });
        var ctl = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("ctl"))),
            ConsistentRead = true,
            ProjectionExpression = "n",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(
            Py.Equal(ctl.Item, Item(("n", N("1")))),
            $"positive control: a well-formed PutItem/GetItem with a projection must work, got {Py.Repr(ctl.Item)}");

        // Each case pairs a refusal with the concept its message must name. The keys are the pk values
        // the refused writes addressed, so the effect-absent sweep below can look for them.
        var nestedOverLimit = S("leaf");
        for (var i = 0; i < 32; i++)
        {
            nestedOverLimit = M(("n", nestedOverLimit));
        }

        var cases = new (string Why, Func<Task> Call, string[] Required, string? Key, string Expected)[]
        {
            ("a table name below the length floor",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = "ab", Key = Item(("pk", S("x"))) }),
                ["tablename", "length"], null,
                "1 validation error detected: Value 'ab' at 'tableName' failed to satisfy constraint: "
                + "Member must have length greater than or equal to 3"),
            ("an unknown ReturnConsumedCapacity enum value",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ctl"))), ReturnConsumedCapacity = new ReturnConsumedCapacity("NOPE") }),
                ["returnconsumedcapacity", "enum"], null,
                "1 validation error detected: Value 'NOPE' at 'returnConsumedCapacity' failed to satisfy "
                + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]"),
            ("an AttributeValue carrying no type member",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("msg-union")), ("a", new AttributeValue())) }),
                ["attributevalue", "datatype"], "msg-union",
                "Supplied AttributeValue is empty, must contain exactly one of the supported datatypes"),
            ("a key that does not match the declared schema",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", N("1"))) }),
                ["key", "schema"], null,
                "The provided key element does not match the schema"),
            ("an empty string as a key attribute value",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S(""))) }),
                ["empty string", "pk"], null,
                "One or more parameter values are not valid. The AttributeValue for a key attribute cannot "
                + "contain an empty string value. Key: pk"),
            ("a projection expression that does not parse",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ctl"))), ProjectionExpression = "1+" }),
                ["projectionexpression", "syntax error"], null,
                "Invalid ProjectionExpression: Syntax error; token: \"1\", near: \"1+\""),
            ("a document nested past the depth limit",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("msg-nest")), ("d", nestedOverLimit)) }),
                ["nesting", "limit"], "msg-nest",
                "1 validation error detected: Nesting Levels have exceeded supported limits: Attributes in "
                + "the item have nested levels beyond supported limit"),
            ("a number carrying more significant digits than are supported",
                () => ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = Item(("pk", S("msg-num")), ("n", N(new string('9', 39)))) }),
                ["38", "significant digits"], "msg-num",
                "1 validation error detected: Attempting to store more than 38 significant digits in a "
                + "Number"),
        };

        foreach (var (why, call, required, _, expected) in cases)
        {
            var message = await Message(call, $"a request carrying {why}", expected);
            var low = message.ToLowerInvariant();
            Ensure(message.Trim().Length >= 10, $"the refusal for {why} must describe the mistake, but its Message was {Py.Repr(message)}");
            Ensure(
                message.Length <= 600,
                $"the refusal for {why} returned a {message.Length}-character Message; a customer-facing validation message is a sentence, not a dump: {Py.Repr(message[..Math.Min(200, message.Length)])}");
            foreach (var token in required)
            {
                Ensure(
                    low.Contains(token, StringComparison.Ordinal),
                    $"the refusal for {why} must name the violated concept {Py.Repr(token)} in its Message, got {Py.Repr(message)}");
            }

            foreach (var marker in Leakage)
            {
                Ensure(
                    !low.Contains(marker, StringComparison.Ordinal),
                    $"the Message for {why} leaks the implementation behind the endpoint: it contains {Py.Repr(marker)}. A customer-facing validation message describes the request, not the parser. Got {Py.Repr(message)}");
            }

            var frame = Frame().Match(message);
            Ensure(
                !frame.Success,
                $"the Message for {why} carries a source-location shape {Py.Repr(frame.Value)}, which is decoder diagnostics rather than a description of the request: {Py.Repr(message)}");
        }

        // --- effect absent: every refused write left nothing behind ---
        foreach (var (why, _, _, key, _) in cases)
        {
            if (key is null)
            {
                continue;
            }

            var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(key))), ConsistentRead = true });
            Ensure(got.Item is null, $"the write refused for {why} must leave nothing behind at pk={Py.Repr(key)}, got {Py.Repr(got.Item)}");
        }

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, Select = Select.COUNT });
        Ensure(scanned.Count == 1, $"only the control item may exist after eight refused requests, got {scanned.Count}");
    }

    /// <summary>The Message of a refused call, asserted to be <paramref name="expected"/>; an unexpected success raises.</summary>
    private static async Task<string> Message(Func<Task> fn, string what, string expected)
    {
        var err = await FidelityContext.CaptureError(fn) ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
        Ensure(
            err.Code == ErrValidation,
            $"{what}: expected {ErrValidation} so the message below is the validation surface, got {Py.Repr(err.Code)}");
        Ensure(err.Message == expected, $"{what}: expected the message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        return err.Message ?? "";
    }
}
