// Translated from behavioral/dynamodb/check_projection_overlapping_document_paths_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-overlapping-document-paths-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionOverlappingDocumentPathsRejected
{
    private const string ErrValidation = "ValidationException";
    private const string OverlapPrefix =
        "Invalid ProjectionExpression: Two document paths overlap with each other; " +
        "must remove or rewrite one of these paths; ";

    /// <summary>Return (code, message) for a rejected call; fail loudly if it SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// Overlapping ProjectionExpression paths (same path twice, or parent plus descendant, also via
    /// aliases) are rejected with ValidationException naming both resolved paths; siblings are served.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-overlapping-document-paths-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        await ddb.PutItemAsync(
            table,
            Item(("pk", S("overlap")), ("doc", M(("keep", S("k")), ("zeta", S("z"))))));

        Task<(string? Code, string? Message)> Reject(string expr, Dictionary<string, string>? names = null)
        {
            var request = new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("overlap"))),
                ConsistentRead = true,
                ProjectionExpression = expr,
            };
            if (names is { Count: > 0 })
            {
                request.ExpressionAttributeNames = names;
            }

            return Error(() => ddb.GetItemAsync(request));
        }

        // --- the same raw path twice ---
        var (code, msg) = await Reject("doc, doc");
        Ensure(code == ErrValidation, $"'doc, doc' must be rejected with {ErrValidation}; got {code}");
        Ensure(
            msg == OverlapPrefix + "path one: [doc], path two: [doc]",
            $"the overlap message must name both paths in resolved bracketed form; got {Py.Repr(msg)}");

        // --- a parent alongside its own child ---
        (code, msg) = await Reject("doc, doc.keep");
        Ensure(code == ErrValidation, $"'doc, doc.keep' must be rejected with {ErrValidation}; got {code}");
        Ensure(
            msg == OverlapPrefix + "path one: [doc], path two: [doc, keep]",
            $"a parent/descendant overlap must render the child path with every segment; got {Py.Repr(msg)}");

        // --- the same attribute reached via an alias on one side only ---
        (code, msg) = await Reject("doc, #d", Names(("#d", "doc")));
        Ensure(code == ErrValidation, $"a raw path and an alias for the same attribute must overlap; got {code}");
        Ensure(
            msg == OverlapPrefix + "path one: [doc], path two: [doc]",
            $"overlap is decided on the RESOLVED name and the message must render the resolved name, not the alias; got {Py.Repr(msg)}");

        // --- the same alias twice ---
        (code, msg) = await Reject("#d, #d", Names(("#d", "doc")));
        Ensure(code == ErrValidation, $"the same alias twice must overlap; got {code}");
        Ensure(msg == OverlapPrefix + "path one: [doc], path two: [doc]", $"got {Py.Repr(msg)}");

        // --- the rejection precedes the read: it fires on a key that does not exist ---
        var (absentCode, absentMsg) = await Error(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("no-such-item"))),
            ConsistentRead = true,
            ProjectionExpression = "doc, doc.keep",
        }));
        Ensure(
            absentCode == ErrValidation,
            $"projection overlap must be validated BEFORE the item lookup, so it rejects even for a key that does not exist; got {absentCode}");
        Ensure(absentMsg == OverlapPrefix + "path one: [doc], path two: [doc, keep]", $"got {Py.Repr(absentMsg)}");

        // --- control: two siblings under one parent do not overlap and are served ---
        // A refusal here is a divergence, so report it as an assertion failure.
        var (okResp, okErr) = await FidelityContext.Capture(() => ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("overlap"))),
            ConsistentRead = true,
            ProjectionExpression = "doc.keep, doc.zeta",
        }));
        if (okErr is not null)
        {
            throw Fail(
                $"'doc.keep, doc.zeta' are non-overlapping siblings and must be SERVED, but the target refused them with {okErr.Code}: {okErr.Message}");
        }

        var ok = okResp!.Item;
        Ensure(
            Py.Equal(ok, Item(("doc", M(("keep", S("k")), ("zeta", S("z")))))),
            $"two non-overlapping sibling paths under one parent must be ACCEPTED and both returned -- the rule is prefix overlap, not a shared first segment; got {Py.Repr(ok)}");

        // --- control: an ordinary read still works after the rejections ---
        var full = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("overlap"))), ConsistentRead = true })).Item;
        Ensure(
            full.Keys.ToHashSet().SetEquals(["pk", "doc"]),
            $"an unprojected read must still carry every attribute; got {Py.Repr(full.Keys.Order(StringComparer.Ordinal).ToList())}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projovl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {name} never became ACTIVE");
    }
}
