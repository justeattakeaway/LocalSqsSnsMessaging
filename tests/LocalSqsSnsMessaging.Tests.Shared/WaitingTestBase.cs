using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests;

public abstract class WaitingTestBase
{
    protected const string TimeBasedTests = "TimeBasedTests";
    protected TimeProvider TimeProvider = TimeProvider.System;

    protected static TimeSpan DefaultShortWaitTime =>
        TimeSpan.FromMilliseconds(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 2_500 : 100
        );

    protected Task WaitAsync(TimeSpan timeSpan)
    {
        var categories = TestContext.Current?.TestDetails.Categories;
        if (categories is null || !categories.Contains(TimeBasedTests, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This method should only be called for tests marked with the 'TimeBasedTests' category.");
        }

        if (TimeProvider is FakeTimeProvider fakeTimeProvider)
        {
            fakeTimeProvider.Advance(timeSpan);
            return Task.CompletedTask;
        }

        return Task.Delay(timeSpan, TimeProvider);
    }
}
