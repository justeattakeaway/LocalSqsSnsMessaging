// Translated from behavioral/dynamodb/check_create_table_gsi_key_schema_hash_after_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-table-gsi-key-schema-hash-after-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTableGsiKeySchemaHashAfterRangeRejected
{
    private const string HashAfterRangeMessage =
        "One or more parameter values were invalid: All HASH key attributes must precede RANGE key "
        + "attributes in the KeySchema";

    private const string Index = "by-a-b-c";

    private static readonly string[] AttributeNames = ["pk", "a", "b", "c"];

    /// <summary>
    /// CreateTable refuses a GSI KeySchema with a HASH attribute after the RANGE one and creates
    /// nothing; the same attributes keyed HASH, HASH, RANGE create a GSI described in that order.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_CreateTable.html
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbMultiAttributeKeys, "A three-element index KeySchema is refused by the model's length-2 constraint, so HASH, RANGE, HASH never gets the HASH-after-RANGE message and HASH, HASH, RANGE is refused too")]
    [DisplayName("dynamodb.create-table-gsi-key-schema-hash-after-range-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var @base = FidelityContext.UniqueName("sim2real-behav-ddb-ks-");

        var order = $"{@base}-order";
        var (code, msg) = await CreateRefused(ddb, ctx, new CreateTableRequest
        {
            TableName = order,
            AttributeDefinitions = [.. AttributeNames.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema =
                    [
                        new KeySchemaElement("a", KeyType.HASH),
                        new KeySchemaElement("b", KeyType.RANGE),
                        new KeySchemaElement("c", KeyType.HASH),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        Ensure(
            (code, msg) == ("ValidationException", HashAfterRangeMessage),
            $"a GSI KeySchema HASH, RANGE, HASH gave {Py.Repr(code)}: {Py.Repr(msg)}");
        await AssertAbsent(ddb, order);

        var good = $"{@base}-good";
        List<KeySchemaElement> Ordered() =>
        [
            new KeySchemaElement("a", KeyType.HASH),
            new KeySchemaElement("c", KeyType.HASH),
            new KeySchemaElement("b", KeyType.RANGE),
        ];
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = good,
            AttributeDefinitions = [.. AttributeNames.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex { IndexName = Index, KeySchema = Ordered(), Projection = new Projection { ProjectionType = ProjectionType.ALL } },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(good), "ResourceNotFoundException"));
        var desc = await WaitActive(ddb, ctx, good);
        Ensure(
            Py.Equal(Boto(desc.KeySchema), Boto([new KeySchemaElement("pk", KeyType.HASH)])),
            $"base key is {Py.Repr(Boto(desc.KeySchema))}");
        var gsis = desc.GlobalSecondaryIndexes.ToDictionary(g => g.IndexName, g => (object?)Boto(g.KeySchema), StringComparer.Ordinal);
        Ensure(
            Py.Equal(gsis, new Dictionary<string, object?> { [Index] = Boto(Ordered()) }),
            $"the GSI with the RANGE attribute last describes as {Py.Repr(gsis)}");
    }

    private static List<Dictionary<string, object?>> Boto(IEnumerable<KeySchemaElement> keySchema) =>
        [.. keySchema.Select(k => new Dictionary<string, object?>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType?.Value })];

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null)
            {
                return;
            }

            if (error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var budget = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == "ACTIVE" && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == "ACTIVE"))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>The (code, message) CreateTable is refused with; a table it creates anyway is torn down.</summary>
    private static async Task<(string? Code, string? Message)> CreateRefused(IAmazonDynamoDB ddb, FidelityContext ctx, CreateTableRequest request)
    {
        request.BillingMode = BillingMode.PAY_PER_REQUEST;
        var (created, error) = await FidelityContext.Capture(() => ddb.CreateTableAsync(request));
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(request.TableName), "ResourceNotFoundException"));
        throw Fail($"CreateTable was accepted: {Py.Repr(created!.TableDescription.TableName)}");
    }

    private static async Task AssertAbsent(IAmazonDynamoDB ddb, string table)
    {
        var (code, msg) = await Error(() => ddb.DescribeTableAsync(table));
        var want = ("ResourceNotFoundException", $"Requested resource not found: Table: {table} not found");
        Ensure((code, msg) == want, $"the refused table {table} describes as {Py.Repr(code)}: {Py.Repr(msg)}");
    }
}
