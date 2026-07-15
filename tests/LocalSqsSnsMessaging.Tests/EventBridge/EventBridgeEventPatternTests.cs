using System.Text.Json.Nodes;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.EventBridge;

/// <summary>
/// Unit tests for the EventBridge content-based event pattern matcher.
/// </summary>
public sealed class EventBridgeEventPatternTests
{
    private static bool Match(string pattern, string @event) =>
        EventBridgeEventPattern.Matches(pattern, JsonNode.Parse(@event));

    [Test]
    public async Task ExactMatch_TopLevelFields()
    {
        Match("""{"source":["my.app"]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":["my.app"]}""", """{"source":"other"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MultipleFields_AreAnded()
    {
        var pattern = """{"source":["my.app"],"detail-type":["Order"]}""";
        Match(pattern, """{"source":"my.app","detail-type":"Order"}""").ShouldBeTrue();
        Match(pattern, """{"source":"my.app","detail-type":"Other"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MultipleCandidates_AreOred()
    {
        var pattern = """{"source":["a","b"]}""";
        Match(pattern, """{"source":"b"}""").ShouldBeTrue();
        Match(pattern, """{"source":"c"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NestedDetail_Matches()
    {
        var pattern = """{"detail":{"state":["running"]}}""";
        Match(pattern, """{"detail":{"state":"running"}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"state":"stopped"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Prefix_And_Suffix()
    {
        Match("""{"source":[{"prefix":"my."}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":[{"prefix":"my."}]}""", """{"source":"your.app"}""").ShouldBeFalse();
        Match("""{"detail":{"file":[{"suffix":".png"}]}}""", """{"detail":{"file":"a.png"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnythingBut()
    {
        Match("""{"source":[{"anything-but":"blocked"}]}""", """{"source":"allowed"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":"blocked"}]}""", """{"source":"blocked"}""").ShouldBeFalse();
        Match("""{"source":[{"anything-but":["a","b"]}]}""", """{"source":"c"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":["a","b"]}]}""", """{"source":"a"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Numeric()
    {
        Match("""{"detail":{"amount":[{"numeric":[">",10,"<",100]}]}}""", """{"detail":{"amount":50}}""").ShouldBeTrue();
        Match("""{"detail":{"amount":[{"numeric":[">",10]}]}}""", """{"detail":{"amount":5}}""").ShouldBeFalse();
        Match("""{"detail":{"amount":[{"numeric":["=",42]}]}}""", """{"detail":{"amount":42}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exists()
    {
        Match("""{"detail":{"key":[{"exists":true}]}}""", """{"detail":{"key":"v"}}""").ShouldBeTrue();
        Match("""{"detail":{"key":[{"exists":true}]}}""", """{"detail":{"other":"v"}}""").ShouldBeFalse();
        Match("""{"detail":{"key":[{"exists":false}]}}""", """{"detail":{"other":"v"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Wildcard()
    {
        Match("""{"source":[{"wildcard":"my.*.service"}]}""", """{"source":"my.orders.service"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"my.*.service"}]}""", """{"source":"my.orders.api"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Cidr()
    {
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"10.0.0.55"}}""").ShouldBeTrue();
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"10.0.1.55"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task EqualsIgnoreCase()
    {
        Match("""{"source":[{"equals-ignore-case":"MyApp"}]}""", """{"source":"myapp"}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task OrOperator()
    {
        var pattern = """{"$or":[{"source":["a"]},{"detail-type":["X"]}]}""";
        Match(pattern, """{"source":"a","detail-type":"Y"}""").ShouldBeTrue();
        Match(pattern, """{"source":"b","detail-type":"X"}""").ShouldBeTrue();
        Match(pattern, """{"source":"b","detail-type":"Y"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ArrayValuedEventField_MatchesAnyElement()
    {
        Match("""{"resources":["arn:2"]}""", """{"resources":["arn:1","arn:2"]}""").ShouldBeTrue();
        Match("""{"resources":["arn:9"]}""", """{"resources":["arn:1","arn:2"]}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task InvalidPattern_IsDetected()
    {
        EventBridgeEventPattern.IsValid("not json", out _).ShouldBeFalse();
        EventBridgeEventPattern.IsValid("""["array"]""", out _).ShouldBeFalse();
        EventBridgeEventPattern.IsValid("", out _).ShouldBeFalse();
        EventBridgeEventPattern.IsValid("""{"source":["a"]}""", out _).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NumericOperators_AllVariants()
    {
        Match("""{"detail":{"n":[{"numeric":["!=",5]}]}}""", """{"detail":{"n":4}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":["!=",5]}]}}""", """{"detail":{"n":5}}""").ShouldBeFalse();
        Match("""{"detail":{"n":[{"numeric":["<=",10]}]}}""", """{"detail":{"n":10}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":[">=",10]}]}}""", """{"detail":{"n":10}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":[">=",10]}]}}""", """{"detail":{"n":9}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnythingBut_ObjectForms()
    {
        Match("""{"source":[{"anything-but":{"prefix":"aws."}}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":{"prefix":"aws."}}]}""", """{"source":"aws.ec2"}""").ShouldBeFalse();
        Match("""{"source":[{"anything-but":{"suffix":".tmp"}}]}""", """{"source":"file.txt"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":{"wildcard":"a*z"}}]}""", """{"source":"abz"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Prefix_EqualsIgnoreCase_Form()
    {
        Match("""{"source":[{"prefix":{"equals-ignore-case":"MY."}}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"detail":{"f":[{"suffix":{"equals-ignore-case":".PNG"}}]}}""", """{"detail":{"f":"a.png"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exists_False_WhenPresent_DoesNotMatch()
    {
        Match("""{"detail":{"key":[{"exists":false}]}}""", """{"detail":{"key":"v"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Wildcard_LeadingAndTrailing()
    {
        Match("""{"source":[{"wildcard":"*.service"}]}""", """{"source":"my.service"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"my.*"}]}""", """{"source":"my.anything"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"*"}]}""", """{"source":"anything"}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Cidr_InvalidInputs_ReturnFalse()
    {
        Match("""{"detail":{"ip":[{"cidr":"not-a-cidr"}]}}""", """{"detail":{"ip":"10.0.0.1"}}""").ShouldBeFalse();
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"not-an-ip"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NumberAndStringAreDistinct()
    {
        // A string candidate must not match a numeric field (and vice versa).
        Match("""{"detail":{"n":["5"]}}""", """{"detail":{"n":5}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MissingField_WithLiteralCandidate_DoesNotMatch()
    {
        Match("""{"detail":{"absent":["x"]}}""", """{"detail":{"present":"y"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }
}
