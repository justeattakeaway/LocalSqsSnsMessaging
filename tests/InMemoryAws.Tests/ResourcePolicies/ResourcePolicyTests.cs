using System.Net;
using System.Text.Json.Nodes;
using Shouldly;

namespace InMemoryAws.Tests.ResourcePolicies;

/// <summary>
/// <see cref="ResourcePolicy"/> on JSON fixtures: reading both statement shapes, editing by
/// <c>Sid</c>, each service's empty document, storage and comparison rules, and evaluation.
/// Error codes and messages are each service's, and are pinned by the SDK tests.
/// </summary>
public sealed class ResourcePolicyTests
{
    private const string QueueArn = "arn:aws:sqs:us-east-1:123456789012:orders";
    private const string TopicArn = "arn:aws:sns:us-east-1:123456789012:orders";
    private const string BusArn = "arn:aws:events:us-east-1:123456789012:event-bus/default";

    private const string SingleStatement =
        """{"Version":"2012-10-17","Statement":{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:SendMessage","Resource":"arn:aws:sqs:us-east-1:123456789012:orders"}}""";

    private const string TwoStatements =
        """{"Version":"2012-10-17","Statement":[{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:SendMessage"},{"Sid":"Two","Effect":"Allow","Principal":"*","Action":"sqs:ReceiveMessage"}]}""";

    private static ResourcePolicy Queue(string json) => ResourcePolicy.Parse(ResourcePolicyKind.Queue, json).ShouldNotBeNull();

    private static JsonObject Statement(string sid, string action = "sqs:DeleteMessage") =>
        new() { ["Sid"] = sid, ["Effect"] = "Allow", ["Principal"] = "*", ["Action"] = action };

    private static ResourcePolicy.RequestContext Caller(string action, string resource = QueueArn, IPAddress? sourceIp = null) =>
        new(action, resource, "123456789012", "arn:aws:iam::123456789012:root", sourceIp);

    // Reading

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("\"a string\"")]
    public void Parse_TextThatHoldsNoDocument_IsNoPolicy(string? json)
    {
        ResourcePolicy.Parse(ResourcePolicyKind.Queue, json).ShouldBeNull();
    }

    [Test]
    public void Statements_ReadFromASingleObjectAsFromAList()
    {
        var single = Queue(SingleStatement);
        single.StatementCount.ShouldBe(1);
        single.HasStatement("One").ShouldBeTrue();

        var list = Queue(TwoStatements);
        list.StatementCount.ShouldBe(2);
        list.HasStatement("Two").ShouldBeTrue();
        list.HasStatement("Three").ShouldBeFalse();
    }

    [Test]
    public void Statements_OfADocumentWithoutAStatementList_AreNone()
    {
        Queue("""{"Version":"2012-10-17"}""").StatementCount.ShouldBe(0);
        Queue("""{"Version":"2012-10-17","Statement":"nonsense"}""").StatementCount.ShouldBe(0);
    }

    // Editing

    [Test]
    public void WithStatement_AddsAfterTheOthers()
    {
        Queue(TwoStatements).WithStatement(Statement("Three")).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Statement":[{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:SendMessage"},{"Sid":"Two","Effect":"Allow","Principal":"*","Action":"sqs:ReceiveMessage"},{"Sid":"Three","Effect":"Allow","Principal":"*","Action":"sqs:DeleteMessage"}]}""");
    }

    [Test]
    public void WithStatement_OnASingleObject_KeepsItAndWritesAList()
    {
        Queue(SingleStatement).WithStatement(Statement("Two")).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Statement":[{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:SendMessage","Resource":"arn:aws:sqs:us-east-1:123456789012:orders"},{"Sid":"Two","Effect":"Allow","Principal":"*","Action":"sqs:DeleteMessage"}]}""");
    }

    [Test]
    public void WithStatement_ReplacesTheStatementWithTheSameSid_InItsPlace()
    {
        Queue(TwoStatements).WithStatement(Statement("One")).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Statement":[{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:DeleteMessage"},{"Sid":"Two","Effect":"Allow","Principal":"*","Action":"sqs:ReceiveMessage"}]}""");
    }

    [Test]
    public void WithStatement_OnADocumentWithoutStatements_StartsAList()
    {
        Queue("""{"Version":"2012-10-17","Id":"x"}""").WithStatement(Statement("One")).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Id":"x","Statement":[{"Sid":"One","Effect":"Allow","Principal":"*","Action":"sqs:DeleteMessage"}]}""");
    }

    [Test]
    public void Edits_LeaveTheOriginalPolicyUnchanged()
    {
        var policy = Queue(TwoStatements);

        _ = policy.WithStatement(Statement("Three"));
        _ = policy.WithoutStatement("One");

        policy.ToJson().ShouldBe(TwoStatements);
        Queue(TwoStatements).StatementCount.ShouldBe(2);
    }

    [Test]
    public void WithoutStatement_RemovesBySid_FromEitherShape()
    {
        Queue(TwoStatements).WithoutStatement("One").ToJson().ShouldBe(
            """{"Version":"2012-10-17","Statement":[{"Sid":"Two","Effect":"Allow","Principal":"*","Action":"sqs:ReceiveMessage"}]}""");

        var emptied = Queue(SingleStatement).WithoutStatement("One");
        emptied.StatementCount.ShouldBe(0);
        emptied.ToJson().ShouldBe("""{"Version":"2012-10-17","Statement":[]}""");
    }

    [Test]
    public void WithoutStatement_ASidThePolicyLacks_ChangesNothing()
    {
        Queue(SingleStatement).WithoutStatement("Missing").ToJson().ShouldBe(SingleStatement);
    }

    // Each service's documents

    [Test]
    public void Empty_IsEachServicesStartingDocument()
    {
        ResourcePolicy.Empty(ResourcePolicyKind.Queue, QueueArn).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Id":"arn:aws:sqs:us-east-1:123456789012:orders/SQSDefaultPolicy","Statement":[]}""");
        ResourcePolicy.Empty(ResourcePolicyKind.Topic, TopicArn).ToJson().ShouldBe(
            """{"Version":"2008-10-17","Id":"__default_policy_ID","Statement":[]}""");
        ResourcePolicy.Empty(ResourcePolicyKind.EventBus, BusArn).ToJson().ShouldBe(
            """{"Version":"2012-10-17","Statement":[]}""");
    }

    [Test]
    public void ParseOrEmpty_WithNoPolicy_IsTheEmptyDocument()
    {
        ResourcePolicy.ParseOrEmpty(ResourcePolicyKind.EventBus, null, BusArn).ToJson().ShouldBe("""{"Version":"2012-10-17","Statement":[]}""");
        ResourcePolicy.ParseOrEmpty(ResourcePolicyKind.EventBus, SingleStatement, BusArn).HasStatement("One").ShouldBeTrue();
    }

    [Test]
    public void Normalize_ForAQueue_StoresAccountPrincipalsAsRootArns_InEitherShape()
    {
        ResourcePolicy.Normalize(ResourcePolicyKind.Queue,
                """{"Statement":{"Effect":"Allow","Principal":{"AWS":"111122223333"},"Action":"sqs:*"}}""")
            .ShouldBe("""{"Statement":{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::111122223333:root"},"Action":"sqs:*"}}""");
        ResourcePolicy.Normalize(ResourcePolicyKind.Queue,
                """{"Statement":[{"Effect":"Deny","NotPrincipal":{"AWS":["111122223333","arn:aws:iam::444455556666:user/x"]},"Action":"sqs:*"}]}""")
            .ShouldBe("""{"Statement":[{"Effect":"Deny","NotPrincipal":{"AWS":["arn:aws:iam::111122223333:root","arn:aws:iam::444455556666:user/x"]},"Action":"sqs:*"}]}""");
    }

    [Test]
    public void Normalize_KeepsTheTextByteForByte_WhenNothingChanges()
    {
        const string spaced = """{ "Statement": [ { "Effect": "Allow", "Principal": "*", "Action": "sqs:*" } ] }""";
        ResourcePolicy.Normalize(ResourcePolicyKind.Queue, spaced).ShouldBe(spaced);
        ResourcePolicy.Normalize(ResourcePolicyKind.Queue, "not json").ShouldBe("not json");

        const string accountPrincipal = """{"Statement":{"Effect":"Allow","Principal":{"AWS":"111122223333"},"Action":"sns:Publish"}}""";
        ResourcePolicy.Normalize(ResourcePolicyKind.Topic, accountPrincipal).ShouldBe(accountPrincipal);
    }

    [Test]
    public void AreEquivalent_IgnoresLayout_AndForAQueueStatementIds()
    {
        const string a = """{"Version":"2012-10-17","Statement":[{"Sid":"A","Effect":"Allow","Principal":"*","Action":"sqs:*"}]}""";
        const string b = """{ "Statement": [ { "Action": "sqs:*", "Principal": "*", "Effect": "Allow", "Sid": "B" } ], "Version": "2012-10-17" }""";
        const string sameSid = """{ "Statement": [ { "Action": "sqs:*", "Principal": "*", "Effect": "Allow", "Sid": "A" } ], "Version": "2012-10-17" }""";

        ResourcePolicy.AreEquivalent(ResourcePolicyKind.Queue, a, b).ShouldBeTrue();
        ResourcePolicy.AreEquivalent(ResourcePolicyKind.Topic, a, b).ShouldBeFalse();
        ResourcePolicy.AreEquivalent(ResourcePolicyKind.Topic, a, sameSid).ShouldBeTrue();
        ResourcePolicy.AreEquivalent(ResourcePolicyKind.Topic, "not json", "not json").ShouldBeTrue();
        ResourcePolicy.AreEquivalent(ResourcePolicyKind.Topic, a, null).ShouldBeFalse();
    }

    // Evaluation

    [Test]
    public void DeniesExplicitly_ReadsASingleObjectStatement()
    {
        var policy = Queue("""{"Statement":{"Effect":"Deny","Principal":"*","Action":"sqs:SendMessage"}}""");

        policy.DeniesExplicitly(Caller("SendMessage")).ShouldBeTrue();
        policy.DeniesExplicitly(Caller("ReceiveMessage")).ShouldBeFalse();
    }

    [Test]
    [Arguments("sqs:*", "SendMessage", true)]
    [Arguments("SQS:send*", "SendMessage", true)]
    [Arguments("sqs:Receive?essage", "ReceiveMessage", true)]
    [Arguments("sqs:SendMessage", "SendMessageBatch", false)]
    [Arguments("*", "PurgeQueue", true)]
    [Arguments("sns:*", "SendMessage", false)]
    public void DeniesExplicitly_MatchesActionWildcards_IgnoringCase(string pattern, string action, bool denied)
    {
        var policy = Queue(new JsonObject
        {
            ["Statement"] = new JsonObject { ["Effect"] = "Deny", ["Principal"] = "*", ["Action"] = pattern },
        }.ToJsonString());

        policy.DeniesExplicitly(Caller(action)).ShouldBe(denied);
    }

    [Test]
    public void DeniesExplicitly_NamesTheActionWithTheKindsPrefix()
    {
        const string denyPublish = """{"Statement":[{"Effect":"Deny","Principal":"*","Action":"sns:Publish"}]}""";

        ResourcePolicy.Parse(ResourcePolicyKind.Topic, denyPublish)!.DeniesExplicitly(Caller("Publish", TopicArn)).ShouldBeTrue();
        ResourcePolicy.Parse(ResourcePolicyKind.Queue, denyPublish)!.DeniesExplicitly(Caller("Publish", QueueArn)).ShouldBeFalse();
    }

    [Test]
    [Arguments("10.1.2.3/8", "10.200.0.1", true)]
    [Arguments("10.1.2.3/8", "11.0.0.1", false)]
    [Arguments("127.0.0.1/32", "127.0.0.1", true)]
    [Arguments("127.0.0.1", "127.0.0.2", false)]
    [Arguments("192.168.1.0/23", "192.168.0.255", true)]
    [Arguments("2001:db8::/32", "2001:db8:1::1", true)]
    [Arguments("2001:db8::/32", "127.0.0.1", false)]
    public void DeniesExplicitly_OnASourceIpCondition_MatchesTheCidr(string cidr, string address, bool denied)
    {
        var policy = Queue(new JsonObject
        {
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "sqs:*",
                ["Condition"] = new JsonObject { ["IpAddress"] = new JsonObject { ["aws:SourceIp"] = cidr } },
            }),
        }.ToJsonString());

        policy.DeniesExplicitly(Caller("SendMessage", sourceIp: IPAddress.Parse(address))).ShouldBe(denied);
    }

    [Test]
    public void DeniesExplicitly_OnAMalformedCidr_DoesNotDeny()
    {
        var policy = Queue("""{"Statement":[{"Effect":"Deny","Principal":"*","Action":"sqs:*","Condition":{"IpAddress":{"aws:SourceIp":"10.0.0.0/99"}}}]}""");

        policy.DeniesExplicitly(Caller("SendMessage", sourceIp: IPAddress.Parse("10.0.0.1"))).ShouldBeFalse();
    }

    [Test]
    public void EvaluateServiceDelivery_GivesEachVerdict()
    {
        var delivery = ResourcePolicy.ServiceRequest("SendMessage", QueueArn, "sns.amazonaws.com", TopicArn);

        Queue("""{"Statement":{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::123456789012:root"},"Action":"sqs:*"}}""")
            .EvaluateServiceDelivery(delivery).ShouldBe(ResourcePolicy.ServiceDeliveryVerdict.Admitted);
        Queue("""{"Statement":{"Effect":"Allow","Principal":{"Service":"sns.amazonaws.com"},"Action":"sqs:SendMessage","Condition":{"ArnEquals":{"aws:SourceArn":"arn:aws:sns:us-east-1:123456789012:orders"}}}}""")
            .EvaluateServiceDelivery(delivery).ShouldBe(ResourcePolicy.ServiceDeliveryVerdict.Admitted);
        Queue("""{"Statement":{"Effect":"Allow","Principal":{"Service":"sns.amazonaws.com"},"Action":"sqs:SendMessage","Condition":{"ArnEquals":{"aws:SourceArn":"arn:aws:sns:us-east-1:123456789012:other"}}}}""")
            .EvaluateServiceDelivery(delivery).ShouldBe(ResourcePolicy.ServiceDeliveryVerdict.NotAllowed);
        Queue("""{"Statement":[{"Effect":"Deny","Principal":{"Service":"sns.amazonaws.com"},"Action":"sqs:SendMessage"}]}""")
            .EvaluateServiceDelivery(delivery).ShouldBe(ResourcePolicy.ServiceDeliveryVerdict.ExplicitlyDenied);
    }
}
