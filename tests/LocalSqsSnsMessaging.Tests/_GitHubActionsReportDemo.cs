// TEMPORARY — demo tests so the Microsoft.Testing.Extensions.GitHubActionsReport
// annotations (a red source-linked error + a skip warning) are visible on the PR.
// Delete this file before merging.
namespace LocalSqsSnsMessaging.Tests;

public class GitHubActionsReportDemo
{
    [Test]
    public void Demo_Failing_Test_Produces_Error_Annotation()
    {
        Assert.Fail("Demo failure so the GitHub Actions error annotation is visible on the PR diff.");
    }

    [Test]
    [Skip("Demo skip so the GitHub Actions warning annotation is visible on the PR.")]
    public void Demo_Skipped_Test_Produces_Warning_Annotation()
    {
    }
}
