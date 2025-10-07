using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests;

[ApplyDefaultCategory]
public abstract class WaitingTestBase
{
    protected const string TimeBased = ApplyDefaultCategoryAttribute.TimeBasedCategoryName;
    protected TimeProvider TimeProvider = TimeProvider.System;

    protected static TimeSpan DefaultShortWaitTime =>
        TimeSpan.FromMilliseconds(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 2_500 : 100
        );

    protected Task WaitAsync(TimeSpan timeSpan)
    {
        var categories = TestContext.Current?.TestDetails.Categories;
        if (categories is null || !categories.Contains(TimeBased, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This method should only be called for tests marked with the 'TimeBased' category.");
        }

        if (TimeProvider is FakeTimeProvider fakeTimeProvider)
        {
            fakeTimeProvider.Advance(timeSpan);
            return Task.CompletedTask;
        }

        return Task.Delay(timeSpan, TimeProvider);
    }
}
