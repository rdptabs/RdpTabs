using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RdpTabs
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && (args[0].StartsWith("-", StringComparison.Ordinal) ||
                                    args[0].StartsWith("/", StringComparison.Ordinal)))
            {
                string option = args[0].TrimStart('-', '/').ToLowerInvariant();
                if (option == "selftest")
                    return SelfTest.Run(args.Length > 1 ? args[1] : null);
                if (option == "help" || option == "h" || option == "?")
                {
                    ShowUsage();
                    return 0;
                }
                ShowUsage();
                return 2;
            }

            // One tab per address on the command line (connect to several machines at once)
            List<ConnectionProfile> startupProfiles = new List<ConnectionProfile>();
            foreach (string arg in args)
            {
                string error;
                ConnectionProfile profile = ConnectionProfile.FromQuickConnect(arg, out error);
                if (profile == null)
                {
                    MessageBox.Show(error, "RdpTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 2;
                }
                startupProfiles.Add(profile);
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            ProfileStore store = ProfileStore.Load();
            if (store.LoadError != null)
            {
                MessageBox.Show(
                    "Could not read the settings file, so this session starts empty. The existing file is left " +
                    "alone unless you save a new connection.\n\n" +
                    ProfileStore.FilePath + "\n\n" + store.LoadError,
                    "RdpTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Theme.SetMode(store.Theme);   // must happen before any control is created
            Native.SetAppDarkMode(Theme.IsDark);   // dark scrollbars and message boxes

            // If an address matches a saved connection, use that one (with its settings and stored password)
            for (int i = 0; i < startupProfiles.Count; i++)
            {
                ConnectionProfile saved = FindSavedMatch(store, startupProfiles[i]);
                if (saved != null) startupProfiles[i] = saved;
            }

            Application.Run(new MainForm(store, startupProfiles));
            return 0;
        }

        private static ConnectionProfile FindSavedMatch(ProfileStore store, ConnectionProfile target)
        {
            foreach (ConnectionProfile saved in store.Profiles)
            {
                if (!string.Equals(saved.Host, target.Host, StringComparison.OrdinalIgnoreCase)) continue;
                if (saved.Port != target.Port) continue;
                // When the command line names a user, the user has to match too
                if (!string.IsNullOrEmpty(target.UserName) &&
                    !string.Equals(saved.UserName, target.UserName, StringComparison.OrdinalIgnoreCase))
                    continue;
                return saved;
            }
            return null;
        }

        private static void ShowUsage()
        {
            string text =
                "RdpTabs - a tabbed Remote Desktop client\n\n" +
                "Usage:\n" +
                "  RdpTabs.exe                        start the UI\n" +
                "  RdpTabs.exe ADDRESS [ADDRESS...]   one tab per address; user@host:port is supported\n" +
                "                                     an address matching a saved connection reuses its\n" +
                "                                     settings and stored password\n" +
                "  RdpTabs.exe --selftest             self test: ActiveX host, late binding, DPAPI, JSON\n" +
                "  RdpTabs.exe --selftest HOST:PORT   self test plus a real connection, logging state changes\n";
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.WriteLine(text);
            MessageBox.Show(text, "RdpTabs", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ReportFatal(e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ReportFatal(e.ExceptionObject as Exception);
        }

        private static void ReportFatal(Exception ex)
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine("RdpTabs hit an unhandled error.");
            message.AppendLine();
            message.AppendLine(ex != null ? Com.Unwrap(ex).ToString() : "(no exception details)");
            MessageBox.Show(message.ToString(), "RdpTabs", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
