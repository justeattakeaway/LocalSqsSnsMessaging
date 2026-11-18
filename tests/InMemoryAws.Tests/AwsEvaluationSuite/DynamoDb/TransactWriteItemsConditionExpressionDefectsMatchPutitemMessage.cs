// Translated from behavioral/dynamodb/check_transact_write_items_condition_expression_defects_match_putitem_message.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-condition-expression-defects-match-putitem-message</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsConditionExpressionDefectsMatchPutitemMessage
{
    private const string Check = "transact-write-items-condition-expression-defects-match-putitem-message";
    private const string ErrValidation = "ValidationException";
    private const string Prefix = "1 validation error detected: ";

    private static readonly Dictionary<string, string> ExpectedPutMessage = new(StringComparer.Ordinal)
    {
        ["empty"] = Prefix + "Invalid ConditionExpression: The expression can not be empty;",
        ["malformed"] = Prefix + "Invalid ConditionExpression: Syntax error; token: \"<EOF>\", near: \"(\"",
        ["over-4kb"] = Prefix + "Invalid ConditionExpression: Expression size has exceeded the maximum allowed size;",
    };

    /// <summary>
    /// A malformed ConditionExpression on a TransactWriteItems Put is a top-level ValidationException
    /// whose message is the single-item PutItem message for the same defect, minus the single-item
    /// "1 validation error detected: " prefix; neither call writes its item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-condition-expression-defects-match-putitem-message")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // Positive control: a transaction whose ConditionExpression is well-formed commits.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("control"))), ConditionExpression = "attribute_not_exists(pk)" },
                },
            ],
        });
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true });
        Ensure(got.Item is not null, "positive control: a well-formed ConditionExpression must commit");

        var over4kb = "attribute_exists(pk)" + new string(' ', 4097 - "attribute_exists(pk)".Length);
        Ensure(over4kb.Length == 4097, "assert len(over_4kb) == 4097");

        var defects = new (string Label, string Expr)[]
        {
            ("empty", ""),
            ("malformed", "attribute_exists(("),
            ("over-4kb", over4kb),
        };
        foreach (var (label, expr) in defects)
        {
            var putKey = $"single-{label}";
            var (putCode, putMessage) = await Error(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(putKey))),
                ConditionExpression = expr,
            }));
            Ensure(
                putCode == ErrValidation,
                $"single-item PutItem with a {label} ConditionExpression answered {Py.Repr(putCode)} ({Py.Repr(putMessage)}), expected {ErrValidation}");
            Ensure(
                putMessage!.StartsWith(Prefix, StringComparison.Ordinal),
                $"single-item PutItem message for a {label} ConditionExpression does not start with {Py.Repr(Prefix)}: {Py.Repr(putMessage)}");
            Ensure(
                putMessage == ExpectedPutMessage[label],
                $"single-item PutItem message for a {label} ConditionExpression must be the exact AWS message, got {Py.Repr(putMessage)}");

            var txKey = $"transact-{label}";
            var (txCode, txMessage) = await Error(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(txKey))), ConditionExpression = expr } },
                ],
            }));
            Ensure(
                txCode == ErrValidation,
                $"TransactWriteItems with a {label} ConditionExpression answered {Py.Repr(txCode)} ({Py.Repr(txMessage)}), expected {ErrValidation}");

            Ensure(
                Prefix + txMessage == putMessage,
                $"for the {label} defect, TransactWriteItems message {Py.Repr(txMessage)} prefixed with {Py.Repr(Prefix)} does not equal the PutItem message {Py.Repr(putMessage)}");

            foreach (var key in new[] { putKey, txKey })
            {
                var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
                Ensure(item is null, $"a rejected {label} ConditionExpression write must not write {Py.Repr(key)}, got {Py.Repr(item)}");
            }
        }
    }

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {result}");
        }

        return (error.Code, error.Message);
    }
}
