using System.Security.Cryptography;
using System.Text;
using Luna.Core.Auth;
using Luna.Core.Tests.Support;
using Luna.Core.ViewModels;
using Luna.Protocol.Auth;

namespace Luna.Core.Tests.ViewModels;

public sealed class LoginViewModelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02];
    private static readonly byte[] FakePng2 = [0x89, 0x50, 0x4E, 0x47, 0x09, 0x08];

    private const string FakeCode = "fake-qr-code-content";
    private const string FakeDisplayName = "Fake Display Name";
    private const string FakeAvatar = "https://fake.invalid/avatar.png";
    private const string FakeSecret = "fake-secret-in-server-message";

    private static LoginViewModel Create(FakeAuthService fake, IUiDispatcher? ui = null) =>
        new(fake, ui ?? new ImmediateDispatcher());

    private static Task Run(LoginViewModel vm) => vm.StartCommand.ExecuteAsync(null);

    private static async Task Finish(Task task) => await task.WaitAsync(Patience);

    /// <summary>Everything a view could render as text from the public surface of the view model.</summary>
    private static string Dump(LoginViewModel vm)
    {
        var sb = new StringBuilder();
        sb.Append(vm.State).Append('|').Append(vm.Failure).Append('|').Append(vm.ServerErrorCode).Append('|');
        sb.Append(vm.ToString()).Append('|');
        foreach (var property in typeof(LoginViewModel).GetProperties()
                     .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0))
        {
            sb.Append(property.GetValue(vm)).Append('|');
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Restore_succeeds_signs_in_without_calling_the_qr_login()
    {
        var fake = new FakeAuthService { RestoreResult = FakeSessions.Create() };
        using var vm = Create(fake);

        Assert.Equal(LoginState.Idle, vm.State);
        await Run(vm);

        Assert.Equal(LoginState.SignedIn, vm.State);
        Assert.Same(fake.RestoreResult, vm.Session);
        Assert.Null(vm.QrPng);
        Assert.Equal(LoginFailure.None, vm.Failure);
        Assert.Equal(1, fake.RestoreCalls);
        Assert.Equal(0, fake.QrCalls);
    }

    [Fact]
    public async Task Restore_null_then_qr_flow_walks_the_states_and_clears_the_image()
    {
        var fake = new FakeAuthService { RestoreResult = null };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        fake.QrEvents.Add(new QrScanned(FakeDisplayName, FakeAvatar));
        using var vm = Create(fake);
        var seen = new List<LoginState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.State))
            {
                seen.Add(vm.State);
            }
        };

        await Run(vm);

        Assert.Equal(
            [LoginState.Restoring, LoginState.WaitingForScan, LoginState.WaitingForConfirm, LoginState.SignedIn],
            seen);
        Assert.Same(fake.QrResult, vm.Session);
        Assert.Null(vm.QrPng);
        Assert.Equal(1, fake.QrCalls);

        // The scanned user's name and avatar must not be kept anywhere.
        var scanned = new QrScanned(FakeDisplayName, FakeAvatar);
        Assert.Equal(FakeDisplayName, scanned.DisplayName);
        string dump = Dump(vm);
        Assert.DoesNotContain(FakeDisplayName, dump);
        Assert.DoesNotContain(FakeAvatar, dump);
        Assert.DoesNotContain(FakeCode, dump);
    }

    [Fact]
    public async Task Generated_event_exposes_the_png_while_waiting()
    {
        var fake = new FakeAuthService { BlockQr = true };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        using var vm = Create(fake);

        Task run = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);

        Assert.Equal(LoginState.WaitingForScan, vm.State);
        Assert.Equal(FakePng, vm.QrPng);

        fake.Release();
        await Finish(run);
        Assert.Equal(LoginState.SignedIn, vm.State);
        Assert.Null(vm.QrPng);
    }

    [Fact]
    public async Task Expired_then_a_new_code_refreshes_the_image()
    {
        var fake = new FakeAuthService { BlockQr = true };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        fake.QrEvents.Add(new QrExpired());
        using var vm = Create(fake);
        var seen = new List<LoginState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.State))
            {
                seen.Add(vm.State);
            }
        };

        Task run = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);
        Assert.Equal(LoginState.RefreshingCode, vm.State);

        // A new code arriving on the same flow, as the real client does after an expiry.
        fake.Emit(new QrGenerated(FakeCode, FakePng2));

        Assert.Equal(LoginState.WaitingForScan, vm.State);
        Assert.Equal(FakePng2, vm.QrPng);
        Assert.Equal(
            [LoginState.Restoring, LoginState.WaitingForScan, LoginState.RefreshingCode, LoginState.WaitingForScan],
            seen);

        fake.Release();
        await Finish(run);
    }

    [Fact]
    public async Task Declined_keeps_the_declined_state_when_the_minus_13_exception_follows()
    {
        var fake = new FakeAuthService
        {
            QrException = FakeSessions.ApiError(-13, FakeSecret),
        };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        fake.QrEvents.Add(new QrScanned(FakeDisplayName, null));
        fake.QrEvents.Add(new QrDeclined());
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Declined, vm.State);
        Assert.Equal(LoginFailure.None, vm.Failure);
        Assert.Null(vm.ServerErrorCode);
        Assert.Null(vm.Session);
    }

    [Fact]
    public async Task Other_api_errors_fail_with_the_code_only_and_never_the_message()
    {
        var error = FakeSessions.ApiError(18022, FakeSecret);
        Assert.Contains(FakeSecret, error.Message);
        Assert.Contains(FakeSecret, error.ServerMessage);
        var fake = new FakeAuthService { QrException = error };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Server, vm.Failure);
        Assert.Equal(18022, vm.ServerErrorCode);
        Assert.DoesNotContain(FakeSecret, Dump(vm));
    }

    [Fact]
    public async Task Timeout_maps_to_qr_timed_out()
    {
        var fake = new FakeAuthService { QrException = new TimeoutException(FakeSecret) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.QrTimedOut, vm.Failure);
        Assert.DoesNotContain(FakeSecret, Dump(vm));
    }

    [Fact]
    public async Task Invalid_data_maps_to_unexpected_response()
    {
        var fake = new FakeAuthService { QrException = new InvalidDataException(FakeSecret) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.UnexpectedResponse, vm.Failure);
        Assert.DoesNotContain(FakeSecret, Dump(vm));
    }

    [Fact]
    public async Task Network_error_during_restore_fails_and_never_falls_through_to_a_qr_login()
    {
        var fake = new FakeAuthService { RestoreException = new HttpRequestException(FakeSecret) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Network, vm.Failure);
        Assert.Equal(0, fake.QrCalls);
        Assert.DoesNotContain(FakeSecret, Dump(vm));
    }

    [Fact]
    public async Task Network_error_during_the_qr_login_maps_to_network()
    {
        var fake = new FakeAuthService { QrException = new HttpRequestException(FakeSecret) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Network, vm.Failure);
    }

    [Fact]
    public async Task Http_client_timeout_is_a_network_failure_not_a_cancellation()
    {
        // HttpClient reports its own timeout as TaskCanceledException while the caller's token is NOT cancelled.
        var fake = new FakeAuthService { QrException = new TaskCanceledException(FakeSecret, new TimeoutException()) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.False(fake.LastQrToken.IsCancellationRequested);
        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Network, vm.Failure);
    }

    public static TheoryData<string> StorageExceptions => new() { "io", "access", "crypto" };

    [Theory]
    [MemberData(nameof(StorageExceptions))]
    public async Task Session_storage_errors_map_to_storage_without_message_text(string kind)
    {
        Exception error = kind switch
        {
            "io" => new IOException(FakeSecret),
            "access" => new UnauthorizedAccessException(FakeSecret),
            _ => new CryptographicException(FakeSecret),
        };
        Assert.Contains(FakeSecret, error.Message);
        var fake = new FakeAuthService { QrException = error };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Storage, vm.Failure);
        Assert.DoesNotContain(FakeSecret, Dump(vm));
    }

    [Fact]
    public async Task Storage_error_during_restore_also_maps_to_storage()
    {
        var fake = new FakeAuthService { RestoreException = new CryptographicException(FakeSecret) };
        using var vm = Create(fake);

        await Run(vm);

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Storage, vm.Failure);
        Assert.Equal(0, fake.QrCalls);
    }

    [Fact]
    public async Task Unexpected_exception_posts_failed_unexpected_and_still_propagates()
    {
        var fake = new FakeAuthService { QrException = new InvalidOperationException(FakeSecret) };
        using var vm = Create(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(vm));

        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(LoginFailure.Unexpected, vm.Failure);
        Assert.DoesNotContain(FakeSecret, Dump(vm));

        // The guard was released: the command works again.
        fake.QrException = null;
        await Run(vm);
        Assert.Equal(LoginState.SignedIn, vm.State);
    }

    [Fact]
    public async Task Cancel_during_the_qr_flow_ends_cancelled_and_cancels_the_token()
    {
        var fake = new FakeAuthService { BlockQr = true };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        using var vm = Create(fake);

        Task run = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);
        Assert.False(fake.LastQrToken.IsCancellationRequested);

        vm.CancelCommand.Execute(null);
        await Finish(run);

        Assert.True(fake.LastQrToken.IsCancellationRequested);
        Assert.Equal(LoginState.Cancelled, vm.State);
        Assert.Equal(LoginFailure.None, vm.Failure);
        Assert.Null(vm.QrPng);
        Assert.Null(vm.Session);
    }

    [Fact]
    public void Cancel_while_idle_does_nothing()
    {
        using var vm = Create(new FakeAuthService());

        vm.CancelCommand.Execute(null);

        Assert.Equal(LoginState.Idle, vm.State);
    }

    [Fact]
    public async Task Start_while_running_is_ignored()
    {
        var fake = new FakeAuthService { BlockQr = true };
        using var vm = Create(fake);

        Task first = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);
        await Finish(Run(vm));

        Assert.Equal(1, fake.QrCalls);
        Assert.Equal(1, fake.RestoreCalls);

        fake.Release();
        await Finish(first);
        Assert.Equal(LoginState.SignedIn, vm.State);
    }

    [Fact]
    public async Task Two_back_to_back_starts_enter_the_service_once()
    {
        var fake = new FakeAuthService { BlockQr = true };
        using var vm = Create(fake);

        Task first = Run(vm);
        Task second = Run(vm);
        await Finish(second);

        Assert.Equal(1, fake.RestoreCalls);
        Assert.Equal(1, fake.QrCalls);

        fake.Release();
        await Finish(first);
    }

    [Fact]
    public async Task Many_parallel_starts_enter_the_service_once()
    {
        var fake = new FakeAuthService { BlockQr = true };
        using var vm = Create(fake);

        // Dedicated threads: the callers block on the barrier waiting for each other, so they must not
        // depend on the thread-pool size (a 2-core CI runner grows the pool too slowly to fill it).
        using var barrier = new Barrier(16);
        Task[] starts = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    return Run(vm);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap())
            .ToArray();

        await fake.QrEntered.WaitAsync(Patience);

        // Wait until the 15 ignored starts have returned, so the counts below are not read too early.
        using var cts = new CancellationTokenSource(Patience);
        while (starts.Count(t => t.IsCompleted) < 15)
        {
            await Task.Delay(5, cts.Token);
        }

        Assert.Equal(1, fake.RestoreCalls);
        Assert.Equal(1, fake.QrCalls);

        fake.Release();
        await Finish(Task.WhenAll(starts));
        Assert.Equal(1, fake.QrCalls);
    }

    [Fact]
    public async Task Start_after_failed_works_and_clears_the_previous_error()
    {
        var fake = new FakeAuthService { QrException = FakeSessions.ApiError(18022, FakeSecret) };
        using var vm = Create(fake);
        await Run(vm);
        Assert.Equal(LoginState.Failed, vm.State);
        Assert.Equal(18022, vm.ServerErrorCode);

        fake.QrException = null;
        await Run(vm);

        Assert.Equal(LoginState.SignedIn, vm.State);
        Assert.Equal(LoginFailure.None, vm.Failure);
        Assert.Null(vm.ServerErrorCode);
        Assert.Equal(2, fake.QrCalls);
    }

    [Fact]
    public async Task Start_after_declined_or_cancelled_works()
    {
        var fake = new FakeAuthService { QrException = FakeSessions.ApiError(-13, FakeSecret) };
        fake.QrEvents.Add(new QrDeclined());
        using var vm = Create(fake);
        await Run(vm);
        Assert.Equal(LoginState.Declined, vm.State);

        fake.QrException = null;
        fake.QrEvents.Clear();
        await Run(vm);

        Assert.Equal(LoginState.SignedIn, vm.State);
    }

    [Fact]
    public async Task With_a_queueing_dispatcher_nothing_changes_until_posts_run_and_order_is_kept()
    {
        var fake = new FakeAuthService();
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        fake.QrEvents.Add(new QrScanned(FakeDisplayName, null));
        var queue = new QueueingDispatcher();
        using var vm = Create(fake, queue);
        var seen = new List<LoginState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.State))
            {
                seen.Add(vm.State);
            }
        };

        await Run(vm);

        // The awaited call has finished, but no state change has been applied yet.
        Assert.Equal(1, fake.QrCalls);
        Assert.True(queue.Pending > 0);
        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Null(vm.Session);
        Assert.Empty(seen);

        queue.RunAll();

        Assert.Equal(
            [LoginState.Restoring, LoginState.WaitingForScan, LoginState.WaitingForConfirm, LoginState.SignedIn],
            seen);
        Assert.Same(fake.QrResult, vm.Session);
        Assert.Null(vm.QrPng);
    }

    [Fact]
    public async Task Dispose_cancels_a_running_flow()
    {
        var fake = new FakeAuthService { BlockQr = true };
        var vm = Create(fake);

        Task run = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);
        vm.Dispose();
        await Finish(run);

        Assert.True(fake.LastQrToken.IsCancellationRequested);
        Assert.Equal(LoginState.Cancelled, vm.State);
    }

    /// <summary>
    /// A restore that blocks until <see cref="Release"/> and deliberately ignores its token, like a slow disk or
    /// DPAPI call that cannot be interrupted; it then reports "no saved session". Counts QR login attempts.
    /// </summary>
    private sealed class BlockingRestoreAuth : IAuthService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _qrCalls;

        public Task Entered => _entered.Task;

        public CancellationToken RestoreToken { get; private set; }

        public int QrCalls => Volatile.Read(ref _qrCalls);

        public void Release() => _gate.TrySetResult();

        public async Task<AuthSession?> RestoreAsync(CancellationToken cancellationToken = default)
        {
            RestoreToken = cancellationToken;
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            return null;
        }

        public Task<AuthSession> LoginWithQrAsync(Action<QrLoginEvent> onEvent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _qrCalls);
            return Task.FromResult(FakeSessions.Create());
        }
    }

    [Fact]
    public async Task Cancel_during_restore_ends_cancelled_without_a_qr_login_and_start_works_again()
    {
        var auth = new BlockingRestoreAuth();
        using var vm = new LoginViewModel(auth, new ImmediateDispatcher());

        Task run = Run(vm);
        await auth.Entered.WaitAsync(Patience);
        Assert.Equal(LoginState.Restoring, vm.State);
        Assert.False(auth.RestoreToken.IsCancellationRequested);

        vm.CancelCommand.Execute(null);
        auth.Release();
        await Finish(run);

        Assert.True(auth.RestoreToken.IsCancellationRequested);
        Assert.Equal(0, auth.QrCalls);
        Assert.Equal(LoginState.Cancelled, vm.State);
        Assert.Equal(LoginFailure.None, vm.Failure);
        Assert.Null(vm.QrPng);
        Assert.Null(vm.Session);

        // The guard is free again: a new run enters the service (restore is already released, so it goes on to QR).
        await Finish(Run(vm));
        Assert.Equal(1, auth.QrCalls);
        Assert.Equal(LoginState.SignedIn, vm.State);
    }

    [Fact]
    public async Task Dispose_during_restore_does_not_throw_and_releases_the_guard()
    {
        var auth = new BlockingRestoreAuth();
        var vm = new LoginViewModel(auth, new ImmediateDispatcher());

        Task run = Run(vm);
        await auth.Entered.WaitAsync(Patience);

        vm.Dispose();
        auth.Release();
        await Finish(run);

        Assert.True(auth.RestoreToken.IsCancellationRequested);
        Assert.Equal(0, auth.QrCalls);
        Assert.Equal(LoginState.Cancelled, vm.State);
        Assert.False(vm.StartCommand.IsRunning);

        // The run guard (0 = idle) must be back to idle once the run has unwound.
        var guard = typeof(LoginViewModel).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(guard);
        Assert.Equal(0, (int)guard!.GetValue(vm)!);

        // A start after Dispose is ignored and must not reach the service again.
        await Finish(Run(vm));
        Assert.Equal(0, auth.QrCalls);
        Assert.Equal(0, (int)guard.GetValue(vm)!);
    }

    [Fact]
    public void Dispose_is_safe_when_idle_and_when_called_twice()
    {
        var vm = Create(new FakeAuthService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task ToString_is_the_type_name_while_a_qr_image_and_a_session_are_present()
    {
        var fake = new FakeAuthService { BlockQr = true };
        fake.QrEvents.Add(new QrGenerated(FakeCode, FakePng));
        using var vm = Create(fake);

        Task run = Run(vm);
        await fake.QrEntered.WaitAsync(Patience);
        Assert.NotNull(vm.QrPng);
        Assert.Equal(nameof(LoginViewModel), vm.ToString());

        fake.Release();
        await Finish(run);
        Assert.NotNull(vm.Session);
        Assert.Contains(FakeSessions.FakeUid, vm.Session!.Info.Uid);
        Assert.Equal(nameof(LoginViewModel), vm.ToString());
    }
}
