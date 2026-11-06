using Luna.Core.Auth;
using Luna.Core.Chat;
using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Core.Sessions;
using Luna.Core.Storage;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Luna.App
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Invoked when the application is launched. This is the composition root: the session is stored with
        /// DPAPI exactly as Luna.Playground does it, and the view models are built on the UI thread.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var store = new EncryptedFileSessionStore(EncryptedFileSessionStore.DefaultPath, new DpapiSecretProtector());
            var auth = new AuthService(store);
            var ui = new WinUiDispatcher(DispatcherQueue.GetForCurrentThread());
            var login = new LoginViewModel(auth, ui);

            // One message store for the whole run. Without it there is no home factory, so the shell leaves Home null and
            // the home page shows its "unavailable" line; the login still works.
            SqliteMessageStore? messageStore = OpenMessageStore();
            var shell = new ShellViewModel(login, messageStore is null ? null : CreateHomeFactory(messageStore, ui));
            _window = new MainWindow(shell);

            // Registered AFTER the window's own Closed handler (it runs in the constructor above), because handlers run in
            // registration order: the window first disposes the shell (home and live sync), then the store is closed.
            if (messageStore is not null)
            {
                _window.Closed += (_, _) => messageStore.Dispose();
            }

            _window.Activate();
        }

        // Opens the one message store. Only the failures a missing or locked folder, a damaged file or a database that is
        // too new can cause are handled; the exception is logged by type only (CrashLog never writes the message).
        private static SqliteMessageStore? OpenMessageStore()
        {
            string path = MessageStoreDefaults.DefaultPath;
#if DEBUG
            // Debug builds only: LUNA_HISTORY_DB points the app at another database file, for example a synthetic history made
            // by "Luna.Playground --seed-history". The app still saves real incoming messages and friend names into that file.
            string? overridePath = Environment.GetEnvironmentVariable("LUNA_HISTORY_DB");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                path = overridePath;
            }
#endif
            try
            {
                return new SqliteMessageStore(path);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                CrashLog.Write(ex);
                return null;
            }
        }

        // Builds the home for a signed-in session. The friend source is owned by the home view model, which disposes it;
        // the store is owned by the app. Only the three existing server touch points are created here: the friend list
        // (behind the throttle guard), the live socket (one connection) and nothing else.
        private static Func<AuthSession, HomeViewModel> CreateHomeFactory(SqliteMessageStore messageStore, IUiDispatcher ui)
        {
            return session =>
            {
                var friendSource = new ApiFriendListSource(session);
                try
                {
                    return new HomeViewModel(
                        new ConversationListViewModel(messageStore, messageStore, ui),
                        new FriendDirectory(friendSource, new ApiThrottleGuard(), messageStore, ui),
                        new LiveSyncService(new SocketRealtimeSource(session), messageStore, ui),
                        new ChatViewModel(messageStore, messageStore, ui),
                        [friendSource]);
                }
                catch (Exception)
                {
                    friendSource.Dispose();
                    throw;
                }
            };
        }

        // Once the main window exists the login view model already shows Failed/Unexpected for a bug, so the app stays
        // open. Before that (a failure while starting up) there is nothing to show, so the exception is only logged
        // and the process ends. The log line has no exception message (it can carry data); writing it never throws.
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = _window is not null;
            CrashLog.Write(e.Exception);
        }
    }
}
