using Luna.Core.Friends;

namespace Luna.Core.Tests.Friends;

/// <summary>
/// Pins the client options of <see cref="ApiFriendListSource"/>. The throttle guard in <see cref="FriendDirectory"/> is the
/// only judge of the friend list interval; the ApiClient must not add a second, slightly different one.
/// </summary>
public sealed class ApiFriendListSourceTests
{
    [Fact]
    public void The_client_has_no_per_endpoint_interval_so_the_guard_is_the_only_judge()
    {
        var options = ApiFriendListSource.CreateOptions();

        Assert.Empty(options.EndpointMinIntervals);
    }

    [Fact]
    public void The_client_has_no_trace_action_so_nothing_is_logged()
    {
        var options = ApiFriendListSource.CreateOptions();

        Assert.Null(options.Trace);
    }
}
