using Luna.Core.Tests.Support;
using Luna.Core.ViewModels;
using Luna.Protocol.Auth;

namespace Luna.Core.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static (ShellViewModel Shell, LoginViewModel Login) Create(FakeAuthService fake, IUiDispatcher? ui = null)
    {
        var login = new LoginViewModel(fake, ui ?? new ImmediateDispatcher());
        return (new ShellViewModel(login), login);
    }

    [Fact]
    public void Starts_on_the_login_page_and_signed_out()
    {
        var (shell, login) = Create(new FakeAuthService());
        using (login)
        {
            Assert.Same(login, shell.Login);
            Assert.Equal(ShellPage.Login, shell.Page);
            Assert.False(shell.IsSignedIn);
            Assert.Null(shell.Session);
        }
    }

    [Fact]
    public async Task Moves_to_home_and_exposes_the_session_when_the_login_signs_in()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = Create(fake);
        using (login)
        {
            var pages = new List<ShellPage>();
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.Page))
                {
                    pages.Add(shell.Page);
                }
            };

            await login.StartCommand.ExecuteAsync(null);

            Assert.Equal(ShellPage.Home, shell.Page);
            Assert.True(shell.IsSignedIn);
            Assert.Same(fake.RestoreResult, shell.Session);
            Assert.Equal([ShellPage.Home], pages);
        }
    }

    [Fact]
    public async Task Stays_on_the_login_page_while_waiting_for_a_scan()
    {
        var fake = new FakeAuthService { BlockQr = true };
        fake.QrEvents.Add(new QrGenerated("fake-qr-code", [1, 2, 3]));
        var (shell, login) = Create(fake);
        using (login)
        {
            Task run = login.StartCommand.ExecuteAsync(null);
            await fake.QrEntered.WaitAsync(Patience);

            Assert.Equal(LoginState.WaitingForScan, login.State);
            Assert.Equal(ShellPage.Login, shell.Page);
            Assert.False(shell.IsSignedIn);

            fake.Release();
            await run.WaitAsync(Patience);
            Assert.Equal(ShellPage.Home, shell.Page);
        }
    }

    [Fact]
    public async Task Stays_on_the_login_page_after_a_failure()
    {
        var fake = new FakeAuthService { RestoreException = new HttpRequestException("fake") };
        var (shell, login) = Create(fake);
        using (login)
        {
            await login.StartCommand.ExecuteAsync(null);

            Assert.Equal(LoginState.Failed, login.State);
            Assert.Equal(ShellPage.Login, shell.Page);
            Assert.False(shell.IsSignedIn);
            Assert.Null(shell.Session);
        }
    }

    [Fact]
    public async Task Follows_the_posted_state_not_the_awaited_call()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var queue = new QueueingDispatcher();
        var (shell, login) = Create(fake, queue);
        using (login)
        {
            await login.StartCommand.ExecuteAsync(null);
            Assert.Equal(ShellPage.Login, shell.Page);

            queue.RunAll();

            Assert.Equal(ShellPage.Home, shell.Page);
            Assert.Same(fake.RestoreResult, shell.Session);
        }
    }

    [Fact]
    public async Task Returns_to_the_login_page_when_a_new_login_starts()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = Create(fake);
        using (login)
        {
            await login.StartCommand.ExecuteAsync(null);
            Assert.Equal(ShellPage.Home, shell.Page);

            fake.RestoreResult = null;
            fake.BlockQr = true;
            Task run = login.StartCommand.ExecuteAsync(null);
            await fake.QrEntered.WaitAsync(Patience);

            Assert.Equal(ShellPage.Login, shell.Page);
            Assert.False(shell.IsSignedIn);
            Assert.Null(shell.Session);

            fake.Release();
            await run.WaitAsync(Patience);
            Assert.Equal(ShellPage.Home, shell.Page);
        }
    }

    [Fact]
    public async Task ToString_is_the_type_name_while_a_session_is_present()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        var (shell, login) = Create(fake);
        using (login)
        {
            await login.StartCommand.ExecuteAsync(null);
            Assert.NotNull(shell.Session);
            Assert.Equal(FakeSessions.FakeUid, shell.Session!.Info.Uid);

            Assert.Equal(nameof(ShellViewModel), shell.ToString());
        }
    }
}
