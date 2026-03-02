using System.Diagnostics;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Configures the <see cref="InMemoryAwsBus"/> default <see cref="ActivitySource"/>
/// so that in-memory AWS operations emit spans under a TUnit-compatible source name.
/// This makes SQS/SNS operation spans appear as children of TUnit test case spans
/// in the HTML test report.
/// </summary>
public static class TestActivitySetup
{
    private static readonly ActivitySource Source = new("TUnit.LocalSqsSnsMessaging");

    [Before(TestSession)]
    public static void ConfigureActivitySource()
    {
        InMemoryAwsBus.DefaultActivitySource = Source;
    }
}
