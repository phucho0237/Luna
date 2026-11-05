using Luna.Core.Auth;
using Luna.Core.Chat;
using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Core.Tests.Support;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;

namespace Luna.Core.Tests.ViewModels;

/// <summary>The shell owns one <see cref="HomeViewModel"/> per signed-in session, built by an injected factory.</summary>
public sealed class ShellHomeTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeMessageStore _store = new();
    private readonly FakeContactStore _contacts = new();
    private readonly FakeFriendListSource _friendSource = new();
    private readonly List<FakeRealtimeSource> _feeds = [];
    private readonly List<AuthSession> _factorySessions = [];
    private readonly List<HomeViewModel> _homes = [];

    public void Dispose()
    {
        foreach (HomeViewModel home in _homes)
        {
            home.Dispose();
        }

        _folder.Dispose();
    }

    private HomeViewModel BuildHome(AuthSession session)
    {
        _factorySessions.Add(session);
        var feed = new FakeRealtimeSource();
        _feeds.Add(feed);
        var ui = new ImmediateDispatcher();
        var guard = new ApiThrottleGuard(Path.Combine(_folder.Path, "api-throttle.json"));
        var home = new HomeViewModel(
            new ConversationListViewModel(_store, _contacts, ui),
            new FriendDirectory(_friendSource, guard, _contacts, ui),
            new LiveSyncService(feed, _store, ui),
            new ChatViewModel(_store, _contacts, ui));
        _homes.Add(home);
        return home;
    }

    private static (ShellViewModel Shell, LoginViewModel Login) CreateShell(
        FakeAuthService fake, Func<AuthSession, HomeViewModel>? factory)
    {
        var login = new LoginViewModel(fake, new ImmediateDispatcher());
        return (new ShellViewModel(login, factory), login);
    }

    [Fact]
    public void No_home_is_built_while_the_login_page_is_shown()
    {
        var (shell, login) = CreateShell(new FakeAuthService(), BuildHome);
        using (login)
        using (shell)
        {
            Assert.Null(shell.Home);
            Assert.Empty(_factorySessions);
        }
    }

    [Fact]
    public async Task Signing_in_builds_the_home_from_the_session_before_the_page_changes()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = CreateShell(fake, BuildHome);
        using (login)
        using (shell)
        {
            HomeViewModel? homeWhenPageChanged = null;
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.Page))
                {
                    homeWhenPageChanged = shell.Home;
                }
            };

            await login.StartCommand.ExecuteAsync(null);

            Assert.Same(fake.RestoreResult, Assert.Single(_factorySessions));
            Assert.NotNull(shell.Home);
            Assert.Same(_homes[0], shell.Home);
            Assert.Same(shell.Home, homeWhenPageChanged);
            Assert.Equal(ShellPage.Home, shell.Page);
        }
    }

    [Fact]
    public async Task Without_a_factory_the_shell_still_reaches_home_with_no_home_view_model()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var login = new LoginViewModel(fake, new ImmediateDispatcher());
        using (login)
        {
            var shell = new ShellViewModel(login);

            await login.StartCommand.ExecuteAsync(null);

            Assert.Equal(ShellPage.Home, shell.Page);
            Assert.Null(shell.Home);
            shell.Dispose();
        }
    }

    [Fact]
    public async Task A_factory_that_throws_leaves_no_home_and_does_not_break_the_shell()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = CreateShell(fake, _ => throw new InvalidOperationException("fake factory failure"));
        using (login)
        using (shell)
        {
            await login.StartCommand.ExecuteAsync(null);

            Assert.Equal(ShellPage.Home, shell.Page);
            Assert.True(shell.IsSignedIn);
            Assert.Null(shell.Home);
        }
    }

    [Fact]
    public async Task Signing_out_disposes_the_home_and_a_new_sign_in_builds_a_fresh_one()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = CreateShell(fake, BuildHome);
        using (login)
        using (shell)
        {
            await login.StartCommand.ExecuteAsync(null);
            HomeViewModel first = shell.Home!;
            await first.StartAsync().WaitAsync(Patience);
            await _feeds[0].WhenRunCalls(1).WaitAsync(Patience);
            fake.RestoreResult = null;
            fake.BlockQr = true;

            // A new login run first leaves the signed-in state (session cleared), which must dispose the old home.
            Task second = login.StartCommand.ExecuteAsync(null);

            Assert.Null(shell.Home);
            Assert.Equal(ShellPage.Login, shell.Page);
            Assert.True(_feeds[0].Tokens[0].IsCancellationRequested);

            login.CancelCommand.Execute(null);
            await second.WaitAsync(Patience);
            fake.BlockQr = false;
            fake.RestoreResult = FakeSessions.Create();
            await login.StartCommand.ExecuteAsync(null);

            Assert.NotNull(shell.Home);
            Assert.NotSame(first, shell.Home);
            Assert.Equal(2, _factorySessions.Count);
        }
    }

    [Fact]
    public async Task Dispose_disposes_the_home_stops_following_the_login_and_is_safe_twice()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = CreateShell(fake, BuildHome);
        using (login)
        {
            await login.StartCommand.ExecuteAsync(null);
            HomeViewModel home = shell.Home!;
            await home.StartAsync().WaitAsync(Patience);
            await _feeds[0].WhenRunCalls(1).WaitAsync(Patience);

            shell.Dispose();
            shell.Dispose();

            Assert.True(_feeds[0].Tokens[0].IsCancellationRequested);
            Assert.Null(shell.Home);

            await login.StartCommand.ExecuteAsync(null);

            Assert.Single(_factorySessions);
        }
    }

    [Fact]
    public void ToString_prints_the_type_name_only()
    {
        var (shell, login) = CreateShell(new FakeAuthService(), BuildHome);
        using (login)
        using (shell)
        {
            Assert.Equal(nameof(ShellViewModel), shell.ToString());
        }
    }
}
