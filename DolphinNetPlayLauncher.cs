using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DolphinNetPlayLauncher
{
    internal static class DiagnosticsLog
    {
        private static readonly object Sync = new object();
        private static readonly string LogFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "DolphinNetPlayLauncher-Diagnostics.log");
        private const long MaxLogBytes = 512 * 1024;

        internal static string LogPath { get { return LogFile; } }

        internal static void Initialize()
        {
            try
            {
                lock (Sync)
                {
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxLogBytes)
                    {
                        string old = LogFile + ".old";
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(LogFile, old); } catch { }
                    }
                }
                Write("SESSION", "Launcher started (version " + Program.AppVersion + ").");
            }
            catch { }
        }

        internal static void Write(string category, string message)
        {
            try
            {
                string safe = Redact(message ?? "").Replace("\r", " ").Replace("\n", " | ");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " [" + (category ?? "INFO") + "] " + safe + Environment.NewLine;
                lock (Sync)
                {
                    File.AppendAllText(LogFile, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        internal static void Exception(string context, Exception ex)
        {
            if (ex == null)
            {
                Write("ERROR", context);
                return;
            }
            Write("ERROR", context + ": " + ex.GetType().Name + " - " + ex.Message);
        }

        internal static string ReadRecent(int maxLines)
        {
            try
            {
                if (!File.Exists(LogFile)) return "(no runtime log yet)";
                string[] lines;
                lock (Sync) { lines = File.ReadAllLines(LogFile); }
                int start = Math.Max(0, lines.Length - Math.Max(1, maxLines));
                StringBuilder sb = new StringBuilder();
                for (int i = start; i < lines.Length; i++)
                    sb.AppendLine(Redact(lines[i]));
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "(could not read runtime log: " + ex.Message + ")";
            }
        }

        internal static void Clear()
        {
            try
            {
                lock (Sync)
                {
                    File.WriteAllText(LogFile, "", new UTF8Encoding(false));
                }
                Write("SESSION", "Runtime diagnostic log cleared by user.");
            }
            catch { }
        }

        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile))
                    text = text.Replace(profile, "%USERPROFILE%");
            }
            catch { }
            return text;
        }
    }

    internal static class UiAnimationBudget
    {
        // RC35: RC34 proved that disabling the hidden side-panel backdrop loops alone
        // is not enough.  The still-visible main 30 Hz animated backdrop can starve
        // WinForms timer/message delivery while Games/Sessions are being navigated.
        // Preserve the accepted main animation at idle, but briefly stop REQUESTING
        // new cosmetic backdrop paints during active side-panel input.  Existing
        // pixels remain visible and motion resumes from the shared timeline.  Games
        // and Sessions themselves remain permanently non-animated under RC34's
        // AnimationAllowed=false contract.
        private static long suppressBackdropUntil;

        internal static void PrioritizeInteractiveUi(int milliseconds)
        {
            milliseconds = Math.Max(0, milliseconds);
            long now = Stopwatch.GetTimestamp();
            long until = now + (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
            if (until > suppressBackdropUntil)
                suppressBackdropUntil = until;
        }

        internal static bool BackdropMayAnimate
        {
            get { return Stopwatch.GetTimestamp() >= suppressBackdropUntil; }
        }
    }

    internal static class Program
    {
        // Single source of truth for the launcher's visible version.
        // Change this ONE value for each release/build revision.
        internal const string AppVersion = "0.11.0";
        internal const string AppName = "Dolphin NetPlay Launcher";
        internal static string AppDisplayName { get { return AppName + " " + AppVersion; } }

        internal static SdlControllerManager ControllerManagerForChildForms;
        internal static bool InProcessReturnControllerWatch;
        internal static DateTime InProcessReturnStartedUtc = DateTime.MinValue;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        internal static IntPtr GetForegroundWindowForDiagnostics()
        {
            return GetForegroundWindow();
        }

        internal static uint GetWindowProcessIdForDiagnostics(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return pid;
        }

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const int SW_RESTORE = 9;

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            int TokenInformationClass,
            out TOKEN_ELEVATION TokenInformation,
            int TokenInformationLength,
            out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        // Dolphin NetPlay Launcher is portable: its own config lives beside the executable.
        // The legacy AppData location is read once only for automatic migration.
        private static readonly string ConfigDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.ini");
        private static readonly string LibraryCacheFile = Path.Combine(ConfigDir, "library-cache.txt");
        private static readonly string LibraryMetadataCacheFile = Path.Combine(ConfigDir, "library-metadata-cache.txt");
        private static readonly string LegacyConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DolphinNetPlayLauncher",
            "config.ini");

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            DiagnosticsLog.Initialize();
            DiagnosticsLog.Write("STARTUP", "Command line mode: " +
                ((args == null || args.Length == 0) ? "Standalone" : "Steam/SRM or file argument"));

            DnlSettings settings = LoadDnlSettings();

            // Internal relaunch arguments. These are stripped before normal ROM handling.
            int cleanupDolphinPid = 0;
            bool resumeUpdateCheck = false;
            List<string> userArgs = new List<string>();
            if (args != null)
            {
                foreach (string arg in args)
                {
                    const string cleanupPrefix = "--cleanup-dolphin-pid=";
                    if (!string.IsNullOrEmpty(arg) &&
                        arg.StartsWith(cleanupPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedPid;
                        if (int.TryParse(arg.Substring(cleanupPrefix.Length), out parsedPid) && parsedPid > 0)
                            cleanupDolphinPid = parsedPid;
                    }
                    else if (string.Equals(arg, "--resume-update-check", StringComparison.OrdinalIgnoreCase))
                    {
                        resumeUpdateCheck = true;
                    }
                    else
                    {
                        userArgs.Add(arg);
                    }
                }
            }
            args = userArgs.ToArray();

            if (cleanupDolphinPid > 0)
                GracefullyClosePriorDolphin(cleanupDolphinPid);

            bool standaloneLaunch = args.Length < 1 || string.IsNullOrWhiteSpace(args[0]);
            DiagnosticsLog.Write("STARTUP", standaloneLaunch ? "Standalone launch detected." : "Game-path launch detected.");
            string romPath = null;

            if (!standaloneLaunch)
            {
                try { romPath = Path.GetFullPath(args[0]); }
                catch { romPath = args[0]; }

                if (!File.Exists(romPath))
                {
                    Error("Game file not found:\n" + romPath);
                    return;
                }
            }



            string dolphinExe;
            if (!settings.FirstRunComplete)
            {
                if (!RunFirstTimeSetup(settings, out dolphinExe))
                    return;
            }
            else
            {
                dolphinExe = ResolveDolphinExe(settings);
                if (string.IsNullOrEmpty(dolphinExe))
                    return;
            }

            DiagnosticsLog.Write("STARTUP", "Dolphin executable resolved: " + dolphinExe);
            DolphinPaths paths = BuildPaths(dolphinExe);
            DiagnosticsLog.Write("STARTUP", "Dolphin user folder: " + paths.UserDir);
            if (!ValidateDolphin(paths))
                return;

            if (!EnsureFirstRunLibrarySetup(settings, paths, dolphinExe))
                return;

            // Fresh launcher state must not inherit stale/personal NetPlay values from
            // an existing Dolphin profile. Remember only values previously entered in
            // Dolphin NetPlay Launcher itself.
            string nickname = string.IsNullOrWhiteSpace(settings.LastNickname)
                ? "Player" : settings.LastNickname.Trim();
            string roomCode = settings.LastTraversalCode ?? "";
            string directIp = settings.LastDirectIp ?? "";
            int directPort = Clamp(settings.LastDirectPort, 1, 65535);
            string lastChoice = string.Equals(settings.LastJoinConnection, "Direct", StringComparison.OrdinalIgnoreCase)
                ? "direct" : "traversal";

            string version = GetDolphinVersion(dolphinExe, settings);
            string track = GetUpdateTrackLabel(paths.DolphinIni, version);
            DiagnosticsLog.Write("STARTUP", "Dolphin version: " + version + "; update track: " + track);

            // Do not recursively scan Dolphin's game folders during launcher startup.
            // The library is loaded the first time Games is actually opened.
            List<string> libraryGames = null;

            GameInfo gameInfo = null;
            if (!string.IsNullOrEmpty(romPath))
            {
                try { gameInfo = ResolveGameInfo(paths, romPath); }
                catch (Exception ex) { DiagnosticsLog.Exception("Initial game metadata lookup failed", ex); }
            }

            using (SdlControllerManager controllerManager = new SdlControllerManager(settings))
            {
            ControllerManagerForChildForms = controllerManager;

            // Keep the Steam-launched process and its SDL controller ownership alive across
            // NetPlay sessions. Steam Input can intermittently fail to expose its virtual
            // controller to a self-spawned replacement process. Rebuilding only the launcher
            // window preserves the original Steam process/controller relationship instead.
            while (true)
            {
            gameInfo = null;
            if (!string.IsNullOrEmpty(romPath))
            {
                try { gameInfo = ResolveGameInfo(paths, romPath); }
                catch (Exception ex) { DiagnosticsLog.Exception("Return game metadata lookup failed", ex); }
            }

            string displayTitle = gameInfo != null && !string.IsNullOrWhiteSpace(gameInfo.Title)
                ? gameInfo.Title
                : (!string.IsNullOrEmpty(romPath) ? Path.GetFileNameWithoutExtension(romPath) : "Choose a game");
            string displayGameId = gameInfo != null ? gameInfo.GameId : "";
            int displayRevision = gameInfo != null ? gameInfo.Revision : 0;

            LauncherForm form = new LauncherForm(
                displayTitle,
                displayGameId,
                displayRevision,
                version,
                track,
                nickname,
                roomCode,
                directIp,
                directPort,
                string.Equals(lastChoice, "direct", StringComparison.OrdinalIgnoreCase),
                settings.RememberLastMode && string.Equals(settings.LastMode, "Join", StringComparison.OrdinalIgnoreCase),
                libraryGames,
                romPath,
                standaloneLaunch ? settings.OpenLibraryOnStandalone : settings.OpenLibraryOnSteam,
                settings.ShowControllerPrompts,
                settings.ControllerPromptStyle,
                settings.ControllerGamesButton,
                paths,
                settings);

            form.CheckUpdateRequested += delegate { RunDolphinUpdateCheck(dolphinExe, form, settings); };

            if (resumeUpdateCheck)
            {
                form.Shown += delegate
                {
                    // This is an internal continuation after UAC. Keep the launcher from
                    // flashing open as though the user clicked Check for Update again.
                    form.Hide();
                    Application.DoEvents();

                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        RunDolphinUpdateCheck(dolphinExe, form, settings);
                    });
                };
            }
            form.ChangeDolphinRequested += delegate
            {
                string changed = BrowseForDolphin();
                if (!string.IsNullOrEmpty(changed))
                {
                    settings.DolphinExe = changed;
                    settings.LibrarySetupAcknowledged = false;
                    SaveDnlSettings(settings);
                    MessageBox.Show(
                        "Dolphin location saved.\n\nReopen the game from Steam to use the new location.",
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            };
            form.OptionsRequested += delegate
            {
                // RC39: Options mutates the shared settings object when OK is clicked.
                // Snapshot appearance/font-affecting values before opening the dialog so
                // controller-only changes (notably polling rate) do not trigger a full
                // theme/font rebuild on the launcher afterward.
                string previousThemeStyle = settings.ThemeStyle;
                string previousAppearance = settings.Appearance;
                string previousInterfaceStyle = settings.InterfaceStyle;
                bool previousAnimatedThemeBackground = settings.AnimatedThemeBackground;
                string previousAccentStyle = settings.AccentStyle;

                using (OptionsForm options = new OptionsForm(settings, paths, romPath, version, track, controllerManager))
                {
                    // The launcher itself is TopMost so it stays visible over Steam.
                    // A normal modal child can briefly appear and then fall behind a
                    // TopMost owner. Match the owner's TopMost state and explicitly
                    // activate the options dialog when it is shown.
                    options.TopMost = form.TopMost;
                    options.Shown += delegate
                    {
                        options.BringToFront();
                        options.Activate();
                    };

                    DialogResult optionsResult = options.ShowDialog(form);

                    if (options.SettingsImportRequested)
                    {
                        DiagnosticsLog.Write("SESSION", "Settings imported; restarting Dolphin NetPlay Launcher.");
                        form.Hide();
                        Application.DoEvents();

                        if (RelaunchSelf(args))
                            form.Close();
                        else
                        {
                            form.Show();
                            MessageBox.Show(
                                "Settings were imported, but Dolphin NetPlay Launcher could not restart automatically.\n\n" +
                                "Close and reopen the launcher to apply the imported settings.",
                                "Import Settings",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                        return;
                    }

                    if (optionsResult == DialogResult.OK)
                    {
                        string previousDolphin = dolphinExe;
                        SaveDnlSettings(settings);
                        form.ApplyControllerPromptSettings(
                            settings.ShowControllerPrompts,
                            settings.ControllerPromptStyle,
                            settings.ControllerGamesButton);

                        bool appearanceVisualsChanged =
                            !string.Equals(previousThemeStyle, settings.ThemeStyle, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(previousAppearance, settings.Appearance, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(previousInterfaceStyle, settings.InterfaceStyle, StringComparison.OrdinalIgnoreCase) ||
                            previousAnimatedThemeBackground != settings.AnimatedThemeBackground ||
                            !string.Equals(previousAccentStyle, settings.AccentStyle, StringComparison.OrdinalIgnoreCase);

                        if (appearanceVisualsChanged)
                        {
                            AppTheme.Apply(form, settings);
                            AppFonts.Apply(form, settings);
                            form.RefreshThemeVisuals();
                            form.Invalidate(true);
                            DiagnosticsLog.Write("THEME",
                                "Options changed appearance/font settings; refreshed launcher theme visuals.");
                        }
                        else
                        {
                            // RC38 exposed a latent GDI lifetime hazard here: changing
                            // only controller polling still rebuilt every AdventureLabel
                            // font and the shared Games title fonts. Besides doing needless
                            // work, an old shared title Font could be disposed before a
                            // queued paint used it. Avoid that entire path when appearance
                            // settings did not change.
                            DiagnosticsLog.Write("THEME",
                                "Options applied without appearance/font changes; skipped full theme/font refresh.");
                        }

                        if (!string.Equals(settings.DolphinExe, previousDolphin, StringComparison.OrdinalIgnoreCase))
                        {
                            settings.LibrarySetupAcknowledged = false;
                            SaveDnlSettings(settings);

                            MessageBox.Show(
                                "Dolphin location saved.\n\nThe new Dolphin location will be used the next time you launch a game from Steam.",
                                "Dolphin NetPlay Launcher",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                }

                // If another application was clicked while Options was open, Windows can
                // leave focus on that application after the modal dialog closes. Controller
                // polling intentionally requires ContainsFocus, so explicitly reactivate
                // the launcher for both OK and Cancel exits.
                if (!form.IsDisposed)
                {
                    form.BringToFront();
                    form.Activate();
                    Control previousFocus = form.ActiveControl;
                    if (previousFocus != null && previousFocus.CanFocus)
                        previousFocus.Focus();
                    else
                        form.Focus();
                }
            };

            if (InProcessReturnControllerWatch)
            {
                form.Shown += delegate
                {
                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        DiagnosticsLog.Write("FOCUS", "Returned launcher shown; attempting to reclaim foreground. Controller status: " + controllerManager.StatusText);
                        RestoreLauncherForeground(form);
                    });
                };
            }

            // RC21 regression correction for RC20's normal main-form message loop.
            // Application.Run(form) disposes the main form when its message loop ends, so
            // Host/Join values must be captured while the live LauncherForm still owns its
            // controls. RC20 incorrectly read those properties after Application.Run
            // returned, which made a visibly populated Nickname appear empty.
            bool launchRequestCaptured = false;
            LaunchMode capturedMode = LaunchMode.Host;
            JoinConnection capturedJoinType = JoinConnection.Traversal;
            string capturedNickname = "";
            string capturedTarget = "";
            int capturedPort = 0;
            string capturedGamePath = null;
            bool capturedShowInServerBrowser = false;
            string capturedPublicSessionName = "";
            string capturedPublicSessionRegion = "";
            string capturedPublicSessionPassword = "";

            // RC18 diagnostic-only probes: trace the close request before the RC17
            // shutdown boundary. These handlers do not alter DialogResult, Cancel, or
            // lifecycle behavior; they only record how far a normal close gets.
            Button diagnosticCancelButton = form.CancelButton as Button;
            if (diagnosticCancelButton != null)
            {
                diagnosticCancelButton.Click += delegate
                {
                    DiagnosticsLog.Write("SHUTDOWN", "Main Cancel button Click event received. DialogResult=" + form.DialogResult + "; IsDisposed=" + form.IsDisposed + ".");
                };
            }
            form.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                DiagnosticsLog.Write("SHUTDOWN", "LauncherForm FormClosing entered. CloseReason=" + e.CloseReason + "; Cancel=" + e.Cancel + "; DialogResult=" + form.DialogResult + ".");

                // Capture the complete Host/Join request before Application.Run disposes
                // the form. Do this only for the accepted primary action; Cancel/X do not
                // need or retain launcher-field state.
                if (!e.Cancel && form.DialogResult == DialogResult.OK && !launchRequestCaptured)
                {
                    capturedMode = form.Mode;
                    capturedJoinType = form.JoinType;
                    capturedNickname = form.Nickname;
                    capturedTarget = form.Target;
                    capturedPort = form.Port;
                    capturedGamePath = form.SelectedGamePath;
                    capturedShowInServerBrowser = form.ShowInServerBrowser;
                    capturedPublicSessionName = form.PublicSessionName;
                    capturedPublicSessionRegion = form.PublicSessionRegion;
                    capturedPublicSessionPassword = form.PublicSessionPassword;
                    launchRequestCaptured = true;
                    DiagnosticsLog.Write("LIFECYCLE", "Captured Host/Join request state before LauncherForm disposal (sensitive values not logged).");
                }
            };
            form.FormClosed += delegate(object sender, FormClosedEventArgs e)
            {
                DiagnosticsLog.Write("SHUTDOWN", "LauncherForm FormClosed entered. CloseReason=" + e.CloseReason + "; DialogResult=" + form.DialogResult + ".");
            };

            ControllerNavigation.Attach(form, controllerManager, settings, null);
            form.SetSavedWindowSize(Math.Max(600, settings.WindowWidth), Math.Max(690, settings.WindowHeight));
            form.ApplyMinimumClientArea();
            form.FormClosed += delegate
            {
                // The main launcher UI is designed around a 600px client width.
                // The Games library is a temporary side panel and must never make the
                // next standalone/main window reopen stretched horizontally.
                settings.WindowWidth = 600;
                settings.WindowHeight = Math.Max(690, Math.Min(form.ClientSize.Height, 1100));
                SaveDnlSettings(settings);
            };


            // RC20 targeted lifecycle fix: the main launcher is the application's primary
            // UI, not a child dialog. RC5 preserved this original Steam-launched process
            // and SDL controller manager across NetPlay returns, but implemented each
            // reconstructed LauncherForm as another ShowDialog() modal lifecycle. RC18/19
            // proved that a returned modal LauncherForm can intermittently accept an
            // uncancelled FormClosing event yet never advance to FormClosed. Keep RC5's
            // process/controller preservation, but give each main LauncherForm a normal
            // WinForms message loop instead of repeatedly entering modal dialog machinery.
            //
            // Button.DialogResult only auto-closes a modal form, so explicitly close the
            // main form after Host/Join or Cancel while preserving the same DialogResult
            // values consumed below. X/Alt+F4 continue through the normal Form close path.
            Button mainActionButton = form.AcceptButton as Button;
            Button mainCancelButton = form.CancelButton as Button;
            if (mainActionButton != null)
            {
                mainActionButton.Click += delegate
                {
                    if (!form.IsDisposed)
                    {
                        form.DialogResult = DialogResult.OK;
                        form.Close();
                    }
                };
            }
            if (mainCancelButton != null)
            {
                mainCancelButton.Click += delegate
                {
                    if (!form.IsDisposed)
                    {
                        form.DialogResult = DialogResult.Cancel;
                        form.Close();
                    }
                };
            }

            DiagnosticsLog.Write("LIFECYCLE", "Starting normal launcher UI message loop.");
            Application.Run(form);
            DialogResult launcherResult = form.DialogResult;
            DiagnosticsLog.Write("LIFECYCLE", "Launcher UI message loop returned. DialogResult=" + launcherResult + ".");

            if (launcherResult != DialogResult.OK)
            {
                DiagnosticsLog.Write("SHUTDOWN", "LauncherForm closed without a Host/Join request; main UI loop is ending.");
                DiagnosticsLog.Write("SHUTDOWN", "Returning from Main UI loop; SDL controller manager disposal begins next.");
                return;
            }

            if (!launchRequestCaptured)
            {
                Error("The launcher closed before its Host/Join request could be captured. Please try again.");
                return;
            }

            bool isJoin = capturedMode == LaunchMode.Join;
            romPath = capturedGamePath;
            DiagnosticsLog.Write("NETPLAY", "User requested " + (isJoin ? "Join" : "Host") + ".");

            // A local game selection is only required when hosting. When joining,
            // Dolphin learns the session/game from the host after connecting.
            if (!isJoin)
            {
                if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
                {
                    Error("Choose a GameCube or Wii game first.");
                    return;
                }

                try
                {
                    settings.LastGameFolder = Path.GetDirectoryName(romPath) ?? "";
                    SaveDnlSettings(settings);
                }
                catch { }

                gameInfo = null;
                try { gameInfo = ResolveGameInfo(paths, romPath); }
                catch { }
            }

            if (settings.RememberLastMode)
            {
                settings.LastMode = capturedMode == LaunchMode.Join ? "Join" : "Host";
                SaveDnlSettings(settings);
            }

            if (Process.GetProcessesByName("Dolphin").Length > 0)
            {
                Error("Dolphin is already running.\n\nClose Dolphin, then launch the NetPlay game again.");
                return;
            }

            string selectedNick = (capturedNickname ?? "").Trim();
            if (string.IsNullOrWhiteSpace(selectedNick))
            {
                Error("Enter a nickname.");
                return;
            }
            if (RejectUnsafeIniValue("Nickname", selectedNick))
                return;

            if (isJoin)
            {
                if (capturedJoinType == JoinConnection.Traversal)
                {
                    string code = (capturedTarget ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        Error("Enter a Dolphin traversal room code.");
                        return;
                    }
                    if (RejectUnsafeIniValue("Traversal room code", code))
                        return;

                    nickname = selectedNick;
                    roomCode = code;
                    lastChoice = "traversal";
                    settings.LastNickname = selectedNick;
                    settings.LastTraversalCode = code;
                    settings.LastJoinConnection = "Traversal";
                    SaveDnlSettings(settings);

                    WriteIniValues(paths.DolphinIni, "NetPlay", new Dictionary<string, string>
                    {
                        { "Nickname", selectedNick },
                        { "HostCode", code },
                        { "TraversalChoice", "traversal" }
                    });
                    DiagnosticsLog.Write("NETPLAY", "Join configuration written: Traversal (target redacted).");
                }
                else
                {
                    string ip = (capturedTarget ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(ip))
                    {
                        Error("Enter the host computer's IP address.");
                        return;
                    }
                    if (RejectUnsafeIniValue("Direct IP address", ip))
                        return;

                    nickname = selectedNick;
                    directIp = ip;
                    directPort = capturedPort;
                    lastChoice = "direct";
                    settings.LastNickname = selectedNick;
                    settings.LastDirectIp = ip;
                    settings.LastDirectPort = capturedPort;
                    settings.LastJoinConnection = "Direct";
                    SaveDnlSettings(settings);

                    WriteIniValues(paths.DolphinIni, "NetPlay", new Dictionary<string, string>
                    {
                        { "Nickname", selectedNick },
                        { "Address", ip },
                        { "ConnectPort", capturedPort.ToString() },
                        { "TraversalChoice", "direct" }
                    });
                    DiagnosticsLog.Write("NETPLAY", "Join configuration written: Direct IP (address redacted; port " + capturedPort.ToString() + ").");
                }
            }
            else
            {
                if (RejectUnsafeIniValue("Public session name", capturedPublicSessionName) ||
                    RejectUnsafeIniValue("Public session password", capturedPublicSessionPassword) ||
                    RejectUnsafeIniValue("Public session region", capturedPublicSessionRegion))
                    return;

                // Hosting is intentionally always traversal. Remember the launcher's
                // nickname, but do not import or persist public session name/password.
                nickname = selectedNick;
                settings.LastNickname = selectedNick;
                SaveDnlSettings(settings);

                WriteIniValues(paths.DolphinIni, "NetPlay", new Dictionary<string, string>
                {
                    { "Nickname", selectedNick },
                    { "TraversalChoice", "traversal" },
                    { "UseIndex", capturedShowInServerBrowser ? "True" : "False" },
                    { "IndexName", capturedPublicSessionName },
                    { "IndexRegion", capturedPublicSessionRegion },
                    { "IndexPassword", capturedPublicSessionPassword }
                });
                DiagnosticsLog.Write("NETPLAY", "Public server-browser hosting " +
                    (capturedShowInServerBrowser ? "enabled" : "disabled") +
                    (capturedShowInServerBrowser ? " (session name/password redacted; region " + capturedPublicSessionRegion + ")." : "."));

                string netplayName;
                try
                {
                    if (gameInfo == null)
                        gameInfo = ResolveGameInfo(paths, romPath);
                    netplayName = gameInfo.NetPlayName;
                }
                catch (Exception ex)
                {
                    Error("Could not identify this game for Dolphin NetPlay.\n\n" + ex.Message);
                    return;
                }
                SetHostGame(paths.QtIni, netplayName);
                DiagnosticsLog.Write("NETPLAY", "Host game prepared: " + netplayName);
            }

            if (!settings.AutomationIntroSeen)
            {
                MessageBox.Show(
                    "Dolphin NetPlay Launcher is about to set up Dolphin NetPlay for you.\n\n" +
                    "For a few moments, the launcher will control Dolphin and may move or click the mouse automatically. " +
                    "This is expected.\n\n" +
                    "While the \"Setting up Dolphin NetPlay...\" message is visible, please do not move the mouse, click, type, " +
                    "or use the controller. Once that message disappears, the automation step is finished.\n\n" +
                    "This explanation will only be shown once.",
                    "Before Dolphin NetPlay starts",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                settings.AutomationIntroSeen = true;
                SaveDnlSettings(settings);
            }

            WarningForm warning = null;
            if (settings.ShowAutomationWarning)
            {
                warning = new WarningForm(
                    "Setting up Dolphin NetPlay...",
                    "Please don't move the mouse for a moment.",
                    settings,
                    true);
                warning.Show();
                warning.Refresh();
                Application.DoEvents();
            }

            NetPlaySession session = null;
            Action automationActionCompleted = delegate
            {
                if (warning != null)
                {
                    try
                    {
                        if (!warning.IsDisposed)
                            warning.Close();
                    }
                    catch { }
                    try { warning.Dispose(); } catch { }
                    warning = null;
                    Application.DoEvents();
                }
            };

            try
            {
                session = RunNetPlayAutomation(dolphinExe, isJoin, automationActionCompleted, settings);
            }
            catch (NetPlayJoinFailedException)
            {
                // Preserve RC10's proven failed-Join automation/error-dialog handling.
                // RunNetPlayAutomation has already performed its normal failed-Join cleanup
                // before this exception reaches us. The only RC12 change is the final
                // launcher return: keep this original Steam-launched process and SDL manager
                // instead of spawning a replacement process. Do NOT run a second Dolphin
                // close routine here; RC11 did that and could produce a redundant manual-close
                // prompt after the failed-Join cleanup had already run.
                DiagnosticsLog.Write("AUTOMATION", "Join did not enter a lobby; returning to the launcher for retry in the existing process after failed-Join cleanup.");

                InProcessReturnControllerWatch = true;
                InProcessReturnStartedUtc = DateTime.UtcNow;
                controllerManager.RefreshDevices(false);
                DiagnosticsLog.Write("CONTROLLER", "Preserving the existing SDL controller manager after failed Join; proactive SDL refresh completed. Controller status: " + controllerManager.StatusText);

                // Rebuild from launcher-owned in-memory/config state. Do not re-import
                // nickname or connection targets from Dolphin.ini on return.
                continue;
            }
            catch (Exception ex)
            {
                // Error() is the single authoritative error record for this failure.
                // The stage-specific automation log immediately before it provides context
                // without duplicating the same exception as two ERROR lines.
                Error(ex.Message);
            }
            finally
            {
                automationActionCompleted();
            }

            // Keep Dolphin NetPlay Launcher alive invisibly for the life of the NetPlay lobby
            // when either automatic Dolphin cleanup or controller lobby input is enabled.
            // Controller input is strictly gated to the exact NetPlay lobby window (or the
            // short-lived quit-confirmation modal immediately after B/Escape).
            if (session != null &&
                (settings.AutoCloseDolphin || settings.ControllerNavigation || settings.ReturnToLauncherAfterDolphinClose))
            {
                MonitorNetPlaySession(session, settings, controllerManager);

                if (settings.ReturnToLauncherAfterDolphinClose &&
                    session.DolphinProcess != null &&
                    HasExitedSafe(session.DolphinProcess))
                {
                    DiagnosticsLog.Write("SESSION", "Dolphin closed; returning to Dolphin NetPlay Launcher in the existing process.");
                    DiagnosticsLog.Write("CONTROLLER", "Preserving the existing SDL controller manager for the in-process launcher return.");

                    // RC1-RC4 relaunched a second copy of Dolphin NetPlay Launcher here.
                    // Under Steam Input that replacement process could intermittently lose
                    // controller state. Stay in this exact Steam-launched process instead.
                    InProcessReturnControllerWatch = true;
                    InProcessReturnStartedUtc = DateTime.UtcNow;

                    // RC6 proved foreground focus can recover quickly while SDL input still
                    // takes roughly 5-6 seconds to become usable again. Proactively refresh
                    // the already-preserved SDL device list at the exact Dolphin -> launcher
                    // handoff instead of waiting for the normal periodic refresh cycle.
                    controllerManager.RefreshDevices(false);
                    DiagnosticsLog.Write("CONTROLLER", "Proactive SDL device refresh completed immediately after Dolphin exit. Controller status: " + controllerManager.StatusText);

                    // Keep launcher-owned nickname/connection history across the in-process
                    // return. Dolphin.ini may contain unrelated prior NetPlay values and is
                    // intentionally not used to repopulate launcher fields.
                    continue;
                }
            }

            // No automatic return was requested (or Dolphin is still running), so this
            // invocation is complete.
            break;
            }
        }
        }

        private static IntPtr OpenNetPlaySetup(Process proc)
        {
            // Dolphin can independently show its automatic "Update available" modal shortly
            // after startup. NetPlay automation must NEVER continue typing through that modal:
            // the same keys used for Tools > Start NetPlay can activate updater controls.
            //
            // We therefore check before and between every menu stage. If the exact Dolphin
            // process presents an update modal, close only that modal, wait for Dolphin's base
            // window to become usable again, then restart the proven NetPlay menu sequence
            // from the beginning.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                DiagnosticsLog.Write("AUTOMATION", "Opening Tools > Start NetPlay (attempt " + (attempt + 1) + " of 4).");
                proc.Refresh();
                if (HasExitedSafe(proc))
                    return IntPtr.Zero;

                IntPtr main = FindLargestVisibleWindowForProcess(proc.Id);
                if (main == IntPtr.Zero)
                    main = proc.MainWindowHandle;
                if (main == IntPtr.Zero)
                    return IntPtr.Zero;

                if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                {
                    Thread.Sleep(150);
                    Application.DoEvents();
                }

                // If some OTHER modal is blocking Dolphin, do not blindly type through it.
                if (!IsWindowEnabled(main))
                    return IntPtr.Zero;

                SetForegroundWindow(main);
                Thread.Sleep(350);

                if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                    continue;

                SendKeys.SendWait("%t");
                Thread.Sleep(200);

                if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                {
                    CloseAnyOpenMenuForNetPlay(main);
                    continue;
                }

                SendKeys.SendWait("{HOME}");
                Thread.Sleep(100);

                if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                {
                    CloseAnyOpenMenuForNetPlay(main);
                    continue;
                }

                SendKeys.SendWait("{DOWN 4}");
                Thread.Sleep(100);

                if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                {
                    CloseAnyOpenMenuForNetPlay(main);
                    continue;
                }

                SendKeys.SendWait("{ENTER}");

                IntPtr setup = IntPtr.Zero;
                DateTime deadline = DateTime.Now.AddSeconds(10);
                while (setup == IntPtr.Zero &&
                       DateTime.Now < deadline &&
                       !HasExitedSafe(proc))
                {
                    Thread.Sleep(200);

                    // One last guard: if auto-update appeared immediately after the final
                    // Enter, dismiss it and retry the whole NetPlay sequence instead of
                    // leaving Dolphin in an ambiguous state.
                    if (DismissStartupUpdatePromptForNetPlay(proc, ref main))
                        break;

                    setup = FindWindowByExactTitleForProcess("NetPlay Setup", proc.Id);
                    Application.DoEvents();
                }

                if (setup != IntPtr.Zero)
                {
                    DiagnosticsLog.Write("AUTOMATION", "NetPlay Setup window detected.");
                    return setup;
                }
            }

            DiagnosticsLog.Write("AUTOMATION", "NetPlay Setup was not detected after all attempts.");
            return IntPtr.Zero;
        }

        private static bool DismissStartupUpdatePromptForNetPlay(
            Process proc, ref IntPtr mainWindow)
        {
            if (proc == null || HasExitedSafe(proc))
                return false;

            IntPtr baseWindow = FindLargestVisibleWindowForProcess(proc.Id);
            if (baseWindow != IntPtr.Zero)
                mainWindow = baseWindow;

            IntPtr updatePrompt = FindVisibleWindowTitleContainsForProcess(
                "update", proc.Id, mainWindow);

            if (updatePrompt == IntPtr.Zero)
                return false;

            DiagnosticsLog.Write("AUTOMATION", "Dolphin startup update prompt detected during NetPlay setup; closing that modal and retrying.");

            // This is intentionally a close request, not Enter/Space/click. During Host/Join
            // the user's requested action is NetPlay, not updating Dolphin. Closing the exact
            // startup update modal avoids accidentally choosing Install Update / Never Update.
            PostMessage(updatePrompt, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            DateTime deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline && !HasExitedSafe(proc))
            {
                Thread.Sleep(50);
                Application.DoEvents();

                IntPtr stillThere = FindVisibleWindowTitleContainsForProcess(
                    "update", proc.Id, mainWindow);

                IntPtr currentBase = FindLargestVisibleWindowForProcess(proc.Id);
                if (currentBase != IntPtr.Zero)
                    mainWindow = currentBase;

                if (stillThere == IntPtr.Zero &&
                    mainWindow != IntPtr.Zero &&
                    IsWindowEnabled(mainWindow))
                    return true;
            }

            return true;
        }

        private static void CloseAnyOpenMenuForNetPlay(IntPtr mainWindow)
        {
            if (mainWindow == IntPtr.Zero || !IsWindowEnabled(mainWindow))
                return;

            SetForegroundWindow(mainWindow);
            Thread.Sleep(80);
            SendKeys.SendWait("{ESC}");
            Thread.Sleep(80);
        }

        private static void HideAutomationWarnings()
        {
            try
            {
                foreach (Form open in Application.OpenForms)
                {
                    WarningForm warning = open as WarningForm;
                    if (warning != null && !warning.IsDisposed)
                        warning.Hide();
                }
                Application.DoEvents();
            }
            catch { }
        }

        private static void GracefullyClosePriorDolphin(int pid)
        {
            try
            {
                Process oldDolphin = Process.GetProcessById(pid);
                if (oldDolphin == null || oldDolphin.HasExited)
                    return;

                oldDolphin.CloseMainWindow();
                DateTime deadline = DateTime.Now.AddSeconds(5);
                while (!oldDolphin.HasExited && DateTime.Now < deadline)
                {
                    Thread.Sleep(100);
                    oldDolphin.Refresh();
                    Application.DoEvents();
                }

                if (!oldDolphin.HasExited)
                {
                    MessageBox.Show(
                        "The previous Dolphin window is still open.\n\n" +
                        "Please close it before continuing. Dolphin NetPlay Launcher will never force-kill Dolphin.",
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch
            {
                // It may already have exited.
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProcessElevated(Process process)
        {
            if (process == null)
                return false;

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process.Handle, TOKEN_QUERY, out token))
                    return false;

                TOKEN_ELEVATION elevation;
                int returned;
                if (!GetTokenInformation(
                    token,
                    TokenElevation,
                    out elevation,
                    Marshal.SizeOf(typeof(TOKEN_ELEVATION)),
                    out returned))
                    return false;

                return elevation.TokenIsElevated != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero)
                    CloseHandle(token);
            }
        }

        private static string QuoteCommandLineArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 &&
                value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '\"' }) < 0)
                return value;

            StringBuilder result = new StringBuilder();
            result.Append('"');

            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                }

                result.Append(c);
            }

            if (backslashes > 0)
                result.Append('\\', backslashes * 2);

            result.Append('"');
            return result.ToString();
        }

        private static bool RelaunchSelf(string[] args)
        {
            try
            {
                StringBuilder commandLine = new StringBuilder();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (i > 0) commandLine.Append(' ');
                        commandLine.Append(QuoteCommandLineArgument(args[i] ?? ""));
                    }
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = commandLine.ToString(),
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory
                };

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Exception("Could not restart Dolphin NetPlay Launcher after importing settings", ex);
                return false;
            }
        }

        private static bool RelaunchSelfElevated(string[] args, int cleanupDolphinPid, string extraInternalArg)
        {
            try
            {
                StringBuilder commandLine = new StringBuilder();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (i > 0) commandLine.Append(' ');
                        commandLine.Append(QuoteCommandLineArgument(args[i] ?? ""));
                    }
                }

                if (cleanupDolphinPid > 0)
                {
                    if (commandLine.Length > 0) commandLine.Append(' ');
                    commandLine.Append("--cleanup-dolphin-pid=");
                    commandLine.Append(cleanupDolphinPid);
                }

                if (!string.IsNullOrWhiteSpace(extraInternalArg))
                {
                    if (commandLine.Length > 0) commandLine.Append(' ');
                    commandLine.Append(extraInternalArg);
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = commandLine.ToString(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory
                };

                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    HideAutomationWarnings();
                    MessageBox.Show(
                        "Administrator access was cancelled.\\n\\n" +
                        "Dolphin is running elevated, so Dolphin NetPlay Launcher cannot automate it from a normal process.",
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    HideAutomationWarnings();
                    MessageBox.Show(
                        "Dolphin NetPlay Launcher could not restart as administrator.\\n\\n" + ex.Message,
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return false;
            }
            catch (Exception ex)
            {
                HideAutomationWarnings();
                MessageBox.Show(
                    "Dolphin NetPlay Launcher could not restart as administrator.\\n\\n" + ex.Message,
                    "Dolphin NetPlay Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private static NetPlaySession RunNetPlayAutomation(string dolphinExe, bool join, Action automationActionCompleted, DnlSettings settings)
        {
            DiagnosticsLog.Write("AUTOMATION", "Starting NetPlay automation for " + (join ? "Join" : "Host") + ".");
            Process proc = Process.Start(new ProcessStartInfo
            {
                FileName = dolphinExe,
                UseShellExecute = true
            });
            if (proc == null) throw new Exception("Dolphin could not be started.");
            DiagnosticsLog.Write("AUTOMATION", "Dolphin process started (PID " + proc.Id + ").");

            // Compare the ACTUAL running process integrity levels. Do not infer this from
            // Compatibility-tab registry flags: Steam or other launch context can change
            // the effective privilege level.
            bool launcherElevated = IsCurrentProcessElevated();
            bool dolphinElevated = IsProcessElevated(proc);

            if (dolphinElevated && !launcherElevated)
            {
                HideAutomationWarnings();

                DialogResult elevate = MessageBox.Show(
                    "Dolphin is running as administrator, but Dolphin NetPlay Launcher is not.\n\n" +
                    "Windows blocks the keyboard/mouse automation needed for NetPlay in this state.\n\n" +
                    "Restart Dolphin NetPlay Launcher as administrator and continue?",
                    "Dolphin NetPlay Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly);

                if (elevate == DialogResult.Yes)
                {
                    string[] originalArgs = Environment.GetCommandLineArgs();
                    List<string> relaunchArgs = new List<string>();
                    for (int i = 1; i < originalArgs.Length; i++)
                    {
                        if (!originalArgs[i].StartsWith("--cleanup-dolphin-pid=", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(originalArgs[i], "--resume-update-check", StringComparison.OrdinalIgnoreCase))
                            relaunchArgs.Add(originalArgs[i]);
                    }

                    if (RelaunchSelfElevated(relaunchArgs.ToArray(), proc.Id, null))
                        Environment.Exit(0);
                }

                throw new OperationCanceledException(
                    "Dolphin is elevated while Dolphin NetPlay Launcher is not.");
            }

            DateTime deadline = DateTime.Now.AddSeconds(15);
            while (!HasExitedSafe(proc) && proc.MainWindowHandle == IntPtr.Zero && DateTime.Now < deadline)
            {
                Thread.Sleep(150);
                proc.Refresh();
                Application.DoEvents();
            }
            if (HasExitedSafe(proc))
            {
                DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited before its main window was detected.");
                throw new Exception("Dolphin closed before its main window could be detected.");
            }
            if (proc.MainWindowHandle == IntPtr.Zero)
                throw new Exception("Dolphin's main window was not detected.");
            DiagnosticsLog.Write("AUTOMATION", "Dolphin main window detected.");
            IntPtr dolphinMainWindow = proc.MainWindowHandle;

            IntPtr setup = OpenNetPlaySetup(proc);
            if (setup == IntPtr.Zero)
            {
                if (HasExitedSafe(proc))
                {
                    DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited while waiting for NetPlay Setup.");
                    throw new Exception(
                        "Dolphin closed before NetPlay setup could finish.\n\n" +
                        "Dolphin NetPlay Launcher was waiting for Dolphin's NetPlay Setup window when Dolphin exited.");
                }

                throw new Exception(
                    "The NetPlay Setup window was not detected.\n\n" +
                    "Dolphin opened, but the launcher could not open Tools > Start NetPlay. " +
                    "This build uses the original keyboard-opening method from the early launcher versions. " +
                    "If manual Tools > Start NetPlay still works, please report that this regression-revert also failed.");
            }

            SetForegroundWindow(setup);
            DiagnosticsLog.Write("AUTOMATION", "NetPlay Setup activated; preparing " + (join ? "Connect" : "Host") + " action.");
            Thread.Sleep(500);

            if (HasExitedSafe(proc))
            {
                DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited after NetPlay Setup opened and before the action was completed.");
                throw new Exception("Dolphin closed before the NetPlay action could be completed.");
            }

            RECT client;
            if (!IsWindow(setup))
            {
                DiagnosticsLog.Write("AUTOMATION", "NetPlay Setup window was closed before the NetPlay action could be completed.");
                throw new Exception(
                    "NetPlay Setup was closed before " + (join ? "joining" : "hosting") + " could finish.\n\n" +
                    "Dolphin is still running, but its NetPlay Setup window was closed during automation.");
            }
            if (!GetClientRect(setup, out client))
                throw new Exception("Could not read the NetPlay client area.");

            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(setup, ref origin))
                throw new Exception("Could not map the NetPlay client area to the screen.");

            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;

            // Calculate against Dolphin's Qt client area, not the Windows frame.
            // This avoids Windows 10 vs 11 title-bar/border differences shifting clicks.
            if (join)
            {
                if (HasExitedSafe(proc))
                {
                    DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited before the Connect action could be clicked.");
                    throw new Exception("Dolphin closed before the NetPlay action could be completed.");
                }

                int x = origin.X + (int)(width * 0.895);
                int y = origin.Y + (int)(height * 0.865);
                ClickAt(x, y);
                DiagnosticsLog.Write("AUTOMATION", "Connect action clicked.");
                if (automationActionCompleted != null)
                    automationActionCompleted();
            }
            else
            {
                // NetPlay Setup restores focus to the last-used child control, so arrow
                // navigation is not deterministic. Tab order is deterministic, but the
                // Connect page has one extra focusable field in Traversal mode.
                //
                // The launcher writes TraversalChoice before opening this dialog, so we
                // already know which layout Dolphin is showing:
                //   Traversal Server -> 3 Tabs reaches the Host tab
                //   Direct Connection -> 2 Tabs reaches the Host tab
                SetForegroundWindow(setup);
                Thread.Sleep(250);

                int hostTabSteps = 3;
                for (int i = 0; i < hostTabSteps; i++)
                {
                    SendKeys.SendWait("{TAB}");
                    Thread.Sleep(100);
                }

                // After three Tabs in Traversal mode, focus is on the Connect tab itself.
                // Right Arrow moves the tab selection from Connect to Host.
                SendKeys.SendWait("{RIGHT}");
                Thread.Sleep(250);

                if (HasExitedSafe(proc))
                {
                    DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited during Host tab navigation.");
                    throw new Exception("Dolphin closed before the NetPlay action could be completed.");
                }

                if (!IsWindow(setup))
                {
                    DiagnosticsLog.Write("AUTOMATION", "NetPlay Setup window was closed during Host tab navigation.");
                    throw new Exception(
                        "NetPlay Setup was closed before hosting could finish.\n\n" +
                        "Dolphin is still running, but its NetPlay Setup window was closed while Dolphin NetPlay Launcher was preparing the Host action.");
                }

                if (!GetClientRect(setup, out client))
                    throw new Exception("Could not refresh the NetPlay client area.");

                origin = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(setup, ref origin))
                    throw new Exception("Could not remap the NetPlay client area to the screen.");

                width = client.Right - client.Left;
                height = client.Bottom - client.Top;

                // The bottom-right action button position has already proven reliable
                // on both machines; after Ctrl+Tab this is the Host button instead of Connect.
                int hostX = origin.X + (int)(width * 0.895);
                int hostY = origin.Y + (int)(height * 0.835);
                ClickAt(hostX, hostY);
                DiagnosticsLog.Write("AUTOMATION", "Host tab navigation completed and Host action clicked.");
                if (automationActionCompleted != null)
                    automationActionCompleted();
            }

            // Wait for Dolphin's actual NetPlay lobby. Dolphin names this window
            // "NetPlay". Tracking the exact HWND and PID keeps this scoped to the
            // Dolphin instance launched for this session.
            IntPtr lobby = IntPtr.Zero;
            deadline = DateTime.Now.AddSeconds(15);
            while (lobby == IntPtr.Zero && DateTime.Now < deadline && !HasExitedSafe(proc))
            {
                Thread.Sleep(200);
                lobby = FindWindowByExactTitleForProcess("NetPlay", proc.Id);
                Application.DoEvents();

                // After Connect, any foreground visible secondary window owned by this exact
                // Dolphin process (other than the main window or NetPlay Setup itself) is treated
                // as a possible connection-result modal. Do not depend on Qt disabling Setup or on
                // specific error text such as "Invalid host"; those details can vary by Dolphin build.
                if (join && lobby == IntPtr.Zero)
                {
                    IntPtr failureModal = GetForegroundWindow();
                    if (IsJoinResultModal(failureModal, proc.Id, dolphinMainWindow, setup))
                    {
                        DiagnosticsLog.Write("AUTOMATION", "Dolphin connection-result modal detected while awaiting Join lobby.");

                        bool armed = false;
                        bool sawJoinResultModal = false;
                        IntPtr activeResultModal = IntPtr.Zero;
                        DateTime activeResultModalSince = DateTime.MinValue;
                        DateTime quietSince = DateTime.MinValue;

                        using (UpdaterControllerPromptForm resultPrompt = new UpdaterControllerPromptForm(
                            settings != null && settings.ControllerNavigation && settings.ShowControllerPrompts,
                            settings != null ? settings.ControllerPromptStyle : "Xbox",
                            false,
                            false,
                            true))
                        {
                            // Dolphin can surface more than one connection-result dialog in sequence.
                            // Keep the acknowledgement helper alive across the whole exact-PID modal
                            // chain, then require a short quiet period before declaring the Join failed.
                            while (!HasExitedSafe(proc))
                            {
                                Application.DoEvents();
                                lobby = FindWindowByExactTitleForProcess("NetPlay", proc.Id);
                                if (lobby != IntPtr.Zero)
                                    break;

                                IntPtr foregroundResult = GetForegroundWindow();
                                if (IsJoinResultModal(foregroundResult, proc.Id, dolphinMainWindow, setup))
                                {
                                    if (foregroundResult != activeResultModal)
                                    {
                                        if (sawJoinResultModal)
                                            DiagnosticsLog.Write("AUTOMATION", "Additional Dolphin connection-result modal detected while awaiting Join lobby.");
                                        activeResultModal = foregroundResult;
                                        activeResultModalSince = DateTime.Now;
                                        sawJoinResultModal = true;
                                        armed = false;
                                    }

                                    quietSince = DateTime.MinValue;
                                    resultPrompt.ShowForWindow(activeResultModal);

                                    SdlControllerManager resultController = ControllerManagerForChildForms;
                                    ControllerAction action = resultController != null && resultController.NavigationEnabled
                                        ? resultController.Poll(false)
                                        : ControllerAction.None;

                                    // Require release after each new modal appears so a held A/Start
                                    // cannot accidentally dismiss a second Dolphin error.
                                    if (!armed)
                                    {
                                        if (action == ControllerAction.None)
                                            armed = true;
                                    }
                                    else if (action == ControllerAction.Accept || action == ControllerAction.FocusPrimary)
                                    {
                                        if (GetForegroundWindow() == activeResultModal &&
                                            IsJoinResultModal(activeResultModal, proc.Id, dolphinMainWindow, setup))
                                        {
                                            SendKeys.SendWait("{ENTER}");
                                            armed = false;
                                            activeResultModalSince = DateTime.MinValue;
                                            DiagnosticsLog.Write("AUTOMATION", "Controller acknowledged Dolphin's Join result modal.");
                                        }
                                    }

                                    // Steam Input can intermittently stop exposing button state while a
                                    // Dolphin connection-error modal owns the foreground. RC13 proved that
                                    // in this state SDL may still enumerate the controller while both SDL
                                    // and XInput report no pressed buttons. Keep the validated 0.10.7f/RC16
                                    // controller path as the first choice, but do not strand a controller-only
                                    // user on an acknowledgement-only Join result forever. If the exact,
                                    // already-validated post-Connect Dolphin result HWND remains foreground
                                    // for four seconds, acknowledge that one modal with Enter. Each chained
                                    // modal gets its own timer and still passes the exact PID/HWND safety gate.
                                    if (activeResultModalSince != DateTime.MinValue &&
                                        (DateTime.Now - activeResultModalSince).TotalMilliseconds >= 4000 &&
                                        GetForegroundWindow() == activeResultModal &&
                                        IsJoinResultModal(activeResultModal, proc.Id, dolphinMainWindow, setup))
                                    {
                                        SendKeys.SendWait("{ENTER}");
                                        armed = false;
                                        activeResultModalSince = DateTime.MinValue;
                                        DiagnosticsLog.Write("AUTOMATION", "Fallback acknowledged Dolphin's Join result modal after controller input remained unavailable for 4 seconds.");
                                    }
                                }
                                else if (sawJoinResultModal)
                                {
                                    resultPrompt.HidePrompt();
                                    armed = false;

                                    // 0.10.7f/g's validated behavior was to follow Dolphin's entire
                                    // result-dialog chain and only begin the quiet period after the
                                    // current result dialog was actually gone. Losing foreground is
                                    // not proof that the exact HWND was dismissed. This distinction
                                    // matters under Steam Input, where controller input/focus can
                                    // temporarily disappear while Dolphin's error dialog still exists.
                                    if (activeResultModal != IntPtr.Zero && IsWindow(activeResultModal))
                                    {
                                        quietSince = DateTime.MinValue;
                                    }
                                    else
                                    {
                                        activeResultModal = IntPtr.Zero;
                                        activeResultModalSince = DateTime.MinValue;

                                        if (quietSince == DateTime.MinValue)
                                        {
                                            quietSince = DateTime.Now;
                                            DiagnosticsLog.Write("AUTOMATION", "Dolphin connection-result modal closed; beginning follow-up quiet period.");
                                        }

                                        // Wait long enough for Dolphin to surface a follow-up error dialog
                                        // before showing our own recovery choice.
                                        if ((DateTime.Now - quietSince).TotalMilliseconds >= 1500)
                                            break;
                                    }
                                }

                                Thread.Sleep(25);
                            }
                            resultPrompt.HidePrompt();
                        }

                        if (lobby != IntPtr.Zero)
                            break;

                        if (HasExitedSafe(proc))
                        {
                            DiagnosticsLog.Write("AUTOMATION", "Dolphin exited after a failed Join result; returning to the launcher.");
                            throw new NetPlayJoinFailedException(proc.Id);
                        }

                        bool returnToLauncher = settings != null && settings.AutoReturnAfterFailedJoin;
                        if (!returnToLauncher)
                        {
                            using (JoinFailureRecoveryForm recovery = new JoinFailureRecoveryForm(settings))
                            {
                                DialogResult recoveryResult = recovery.ShowDialog();
                                returnToLauncher = recoveryResult == DialogResult.Yes;
                                if (returnToLauncher && recovery.AlwaysReturn)
                                {
                                    settings.AutoReturnAfterFailedJoin = true;
                                    SaveDnlSettings(settings);
                                }
                            }
                        }

                        if (returnToLauncher)
                        {
                            CloseNetPlaySetupAndDolphin(proc, setup);
                            DiagnosticsLog.Write("AUTOMATION", "Join did not enter lobby after Dolphin connection result; returning to launcher for retry.");
                            throw new NetPlayJoinFailedException(proc.Id);
                        }

                        DiagnosticsLog.Write("AUTOMATION", "Join did not enter lobby; user chose to remain in Dolphin.");
                        return null;
                    }
                }
            }

            if (lobby == IntPtr.Zero)
            {
                if (HasExitedSafe(proc))
                {
                    DiagnosticsLog.Write("AUTOMATION", "Dolphin process exited while waiting for the NetPlay lobby.");
                    if (join)
                        throw new NetPlayJoinFailedException(proc.Id);
                    throw new Exception(
                        "Dolphin closed before the NetPlay lobby appeared.\n\n" +
                        "Dolphin NetPlay Launcher completed the NetPlay action, but Dolphin exited before the lobby could be detected.");
                }
                throw new Exception("The Dolphin NetPlay lobby was not detected.");
            }

            DiagnosticsLog.Write("AUTOMATION", "NetPlay lobby detected. Setup completed successfully.");

            return new NetPlaySession
            {
                DolphinProcess = proc,
                MainWindow = dolphinMainWindow,
                LobbyWindow = lobby,
                IsJoin = join
            };
        }

        private enum NetPlayLobbyControllerTarget
        {
            Start,
            Buffer
        }

        private static void MonitorNetPlaySession(NetPlaySession session, DnlSettings settings, SdlControllerManager controllerManager)
        {
            DiagnosticsLog.Write("AUTOCLOSE", "Monitoring NetPlay lobby for session lifecycle.");
            Process proc = session.DolphinProcess;
            IntPtr lobby = session.LobbyWindow;
            IntPtr dolphinMainWindow = session.MainWindow;
            if (proc == null || lobby == IntPtr.Zero)
                return;

            bool controllerEnabled = settings != null && settings.ControllerNavigation && controllerManager != null;
            bool autoCloseEnabled = settings != null && settings.AutoCloseDolphin;
            int graceMs = settings != null ? settings.NetPlayCloseGraceMs : 1000;
            DateTime quitConfirmUntil = DateTime.MinValue;
            bool controllerModeLogged = false;
            bool joinedLobby = session.IsJoin;
            bool sawGameplayForeground = false;
            IntPtr gameplayForegroundWindow = IntPtr.Zero;
            NetPlayLobbyControllerTarget controllerTarget = NetPlayLobbyControllerTarget.Start;

            // The NetPlay dialog remains the same top-level Qt window while a NetPlay
            // game is running. Controller handling is deliberately narrow: only the exact
            // tracked lobby while it is already foreground receives any synthesized input.
            // Left/right is owned by the launcher and toggles only Start <-> Buffer; it is
            // never passed through to Dolphin's wider focus graph.
            using (UpdaterControllerPromptForm lobbyControllerPrompt =
                new UpdaterControllerPromptForm(
                    controllerEnabled && settings != null && settings.ShowControllerPrompts,
                    settings != null ? settings.ControllerPromptStyle : "Xbox",
                    true,
                    joinedLobby))
            {
            while (!HasExitedSafe(proc))
            {
                Application.DoEvents();

                if (!IsWindow(lobby))
                {
                    lobbyControllerPrompt.HidePrompt();
                    // Require the lobby HWND to stay gone for the configured grace period.
                    // Dolphin can recreate the NetPlay window during transitions.
                    DateTime goneSince = DateTime.Now;
                    bool cameBack = false;

                    while ((DateTime.Now - goneSince).TotalMilliseconds < graceMs && !HasExitedSafe(proc))
                    {
                        Thread.Sleep(100);
                        Application.DoEvents();

                        // RC23: stay completely passive while the exact NetPlay lobby is not
                        // foreground. Do not poll/refresh SDL while Dolphin may be using the
                        // controller for gameplay.

                        IntPtr replacement = FindWindowByExactTitleForProcess("NetPlay", proc.Id);
                        if (replacement != IntPtr.Zero)
                        {
                            lobby = replacement;
                            session.LobbyWindow = replacement;
                            cameBack = true;
                            controllerModeLogged = false;
                            controllerTarget = NetPlayLobbyControllerTarget.Start;
                            DiagnosticsLog.Write("AUTOMATION", "NetPlay lobby window was recreated; controller lobby tracking resumed.");
                            break;
                        }
                    }

                    if (!cameBack)
                    {
                        if (autoCloseEnabled && !HasExitedSafe(proc))
                        {
                            // Only close the exact Dolphin process started for this session,
                            // and do it as a normal close rather than Kill().
                            DiagnosticsLog.Write("AUTOCLOSE", "NetPlay lobby closed; requesting graceful Dolphin close.");
                            GracefullyCloseWhenStable(proc);
                        }

                        if (settings != null && settings.ReturnToLauncherAfterDolphinClose)
                        {
                            // The launcher UI has already closed for the session. Stay alive
                            // invisibly until the exact Dolphin process actually exits, then
                            // the caller can relaunch the same Dolphin NetPlay Launcher invocation (including the
                            // Steam/SRM ROM argument when one was supplied).
                            DiagnosticsLog.Write("SESSION", "NetPlay lobby ended; waiting for Dolphin to close before returning to launcher.");
                            while (!HasExitedSafe(proc))
                            {
                                Thread.Sleep(100);
                                Application.DoEvents();
                                // RC23: no controller polling while waiting for Dolphin to close.
                                // The launcher resumes controller ownership only after Dolphin exits.
                            }
                        }
                        return;
                    }
                }

                IntPtr foreground = GetForegroundWindow();

                // RC24: RC23 proved that lobby-only polling is the correct safety boundary,
                // but testing also showed Dolphin returns focus to its main window after a
                // running NetPlay game closes rather than back to the still-open lobby. Track
                // only an observed foreground transition within this exact Dolphin process:
                // lobby -> another visible Dolphin top-level window (game/render) -> that exact
                // window closes/hides -> Dolphin main. Requiring the observed game/render HWND
                // to actually end prevents merely clicking Dolphin's main window during gameplay
                // from stealing focus back to the lobby. When that sequence completes and the
                // exact lobby still exists, restore the
                // lobby once. No controller polling occurs while the game/render window owns
                // foreground, and this never attempts to activate the lobby merely because it
                // exists in the background.
                if (foreground != IntPtr.Zero && foreground != lobby)
                {
                    uint foregroundPid = 0;
                    GetWindowThreadProcessId(foreground, out foregroundPid);
                    if (foregroundPid == (uint)proc.Id)
                    {
                        if (dolphinMainWindow != IntPtr.Zero && foreground == dolphinMainWindow)
                        {
                            bool gameplayWindowEnded = gameplayForegroundWindow != IntPtr.Zero &&
                                (!IsWindow(gameplayForegroundWindow) || !IsWindowVisible(gameplayForegroundWindow));
                            if (sawGameplayForeground && gameplayWindowEnded &&
                                IsWindow(lobby) && IsWindowEnabled(lobby))
                            {
                                IntPtr priorGameplayWindow = gameplayForegroundWindow;
                                sawGameplayForeground = false;
                                gameplayForegroundWindow = IntPtr.Zero;
                                if (RestoreNetPlayLobbyForeground(lobby, proc.Id))
                                {
                                    foreground = GetForegroundWindow();
                                    controllerModeLogged = false;
                                    DiagnosticsLog.Write("FOCUS", "NetPlay game window ended; restored foreground to the existing NetPlay lobby (prior gameplay HWND " + priorGameplayWindow.ToInt64().ToString() + ").");
                                }
                                else
                                {
                                    DiagnosticsLog.Write("FOCUS", "NetPlay game window ended, but Windows did not allow the existing NetPlay lobby to reclaim foreground automatically.");
                                }
                            }
                        }
                        else if (DateTime.Now > quitConfirmUntil && IsWindow(foreground) && IsWindowVisible(foreground))
                        {
                            // The exact lobby is known separately. A different foreground
                            // top-level window from the same Dolphin process is treated as the
                            // game/render phase only after the lobby has already been established.
                            // We do not touch controller state in this phase.
                            sawGameplayForeground = true;
                            gameplayForegroundWindow = foreground;
                        }
                    }
                }

                // RC23/RC24: controller polling itself is gated by foreground ownership. This is
                // stronger than merely gating the synthesized UI action: Poll() can perform
                // SDL device refreshes, so calling it while Dolphin's render window owns focus
                // is not truly passive. While the game/render window (or anything else) is
                // foreground, Dolphin NetPlay Launcher does not poll or refresh controller state.
                // Normal lobby controller mode begins only when the exact tracked NetPlay HWND
                // is already foreground. No foreground-stealing calls are used here.
                if (controllerEnabled && foreground == lobby && IsWindow(lobby) && IsWindowEnabled(lobby))
                {
                    ControllerAction action = controllerManager.Poll(true);
                    lobbyControllerPrompt.ShowForWindow(lobby);

                    if (!controllerModeLogged)
                    {
                        DiagnosticsLog.Write("AUTOMATION", joinedLobby
                            ? "NetPlay lobby controller mode active (joined lobby: Quit only)."
                            : "NetPlay lobby controller mode active (host lobby: Start/Buffer/Quit).");
                        controllerModeLogged = true;
                    }

                    switch (action)
                    {
                        case ControllerAction.Left:
                        case ControllerAction.Right:
                            if (!joinedLobby)
                            {
                                controllerTarget = controllerTarget == NetPlayLobbyControllerTarget.Start
                                    ? NetPlayLobbyControllerTarget.Buffer
                                    : NetPlayLobbyControllerTarget.Start;
                                MoveCursorToNetPlayLobbyTarget(lobby, controllerTarget);
                                DiagnosticsLog.Write("AUTOMATION", "NetPlay lobby controller target: " + controllerTarget + ".");
                            }
                            break;

                        case ControllerAction.Up:
                        case ControllerAction.Down:
                            if (!joinedLobby)
                            {
                                // Up/down always means Buffer adjustment for the host. Do not let
                                // Dolphin decide which nested widget receives the arrows.
                                controllerTarget = NetPlayLobbyControllerTarget.Buffer;
                                if (ClickNetPlayLobbyTarget(lobby, controllerTarget) &&
                                    GetForegroundWindow() == lobby && IsWindowEnabled(lobby))
                                {
                                    SendKeys.SendWait(action == ControllerAction.Up ? "{UP}" : "{DOWN}");
                                    MoveCursorToNetPlayLobbyTarget(lobby, controllerTarget);
                                }
                            }
                            break;

                        case ControllerAction.Accept:
                        case ControllerAction.FocusPrimary:
                            if (!joinedLobby)
                            {
                                if (controllerTarget == NetPlayLobbyControllerTarget.Start)
                                {
                                    // Start is activated by a targeted click. This avoids traversing
                                    // Dolphin's unrelated game/chat/player/port controls.
                                    ClickNetPlayLobbyTarget(lobby, NetPlayLobbyControllerTarget.Start);
                                    DiagnosticsLog.Write("AUTOMATION", "Controller activated NetPlay Start target.");
                                }
                                else
                                {
                                    // On Buffer, A/Start simply focuses the known spin box. Up/down
                                    // remains the only operation that changes its value.
                                    ClickNetPlayLobbyTarget(lobby, NetPlayLobbyControllerTarget.Buffer);
                                }
                            }
                            break;

                        case ControllerAction.Cancel:
                            if (GetForegroundWindow() == lobby && IsWindow(lobby) && IsWindowEnabled(lobby))
                            {
                                SendKeys.SendWait("{ESC}");
                                quitConfirmUntil = DateTime.Now.AddSeconds(5);
                                DiagnosticsLog.Write("AUTOMATION", "Controller requested NetPlay lobby Back/Quit; awaiting Dolphin confirmation modal.");
                            }
                            break;
                    }
                }
                else
                {
                    lobbyControllerPrompt.HidePrompt();
                    controllerModeLogged = false;

                    // Immediately after B/Escape, Dolphin normally disables the NetPlay lobby
                    // and shows its own quit-confirmation modal. For only a short window after
                    // that explicit B press, allow A/Start -> Enter or B -> Escape on a visible
                    // foreground secondary window owned by the SAME exact Dolphin process.
                    if (controllerEnabled && DateTime.Now <= quitConfirmUntil && foreground != IntPtr.Zero && foreground != lobby)
                    {
                        uint foregroundPid;
                        GetWindowThreadProcessId(foreground, out foregroundPid);
                        bool validQuitModal = foregroundPid == (uint)proc.Id &&
                            IsWindow(foreground) && IsWindowVisible(foreground) &&
                            IsWindow(lobby) && !IsWindowEnabled(lobby);

                        if (validQuitModal)
                        {
                            // This is the one deliberate exception to lobby-only polling: the
                            // exact Dolphin quit-confirmation modal can only be reached from an
                            // explicit controller B/Back action while the tracked lobby was
                            // foreground. Poll only while that exact same-process modal is foreground.
                            ControllerAction action = controllerManager.Poll(true);
                            if (action == ControllerAction.Accept || action == ControllerAction.FocusPrimary)
                            {
                                if (GetForegroundWindow() == foreground)
                                {
                                    SendKeys.SendWait("{ENTER}");
                                    quitConfirmUntil = DateTime.MinValue;
                                    DiagnosticsLog.Write("AUTOMATION", "Controller confirmed Dolphin's NetPlay quit prompt.");
                                }
                            }
                            else if (action == ControllerAction.Cancel)
                            {
                                if (GetForegroundWindow() == foreground)
                                {
                                    SendKeys.SendWait("{ESC}");
                                    quitConfirmUntil = DateTime.MinValue;
                                    DiagnosticsLog.Write("AUTOMATION", "Controller cancelled Dolphin's NetPlay quit prompt.");
                                }
                            }
                        }
                    }
                }

                Thread.Sleep(25);
            }
            }
        }

        // These are normalized client-area positions measured from Dolphin's stock NetPlay
        // lobby layout. Keeping them relative to the lobby client area makes the prototype
        // independent of desktop resolution and substantially less DPI-sensitive than absolute
        // screen coordinates. Only two known controls are ever targeted.
        private static bool TryGetNetPlayLobbyTargetPoint(IntPtr lobby, NetPlayLobbyControllerTarget target, out POINT screenPoint)
        {
            screenPoint = new POINT();
            if (lobby == IntPtr.Zero || !IsWindow(lobby))
                return false;

            RECT client;
            if (!GetClientRect(lobby, out client))
                return false;

            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;
            if (width < 200 || height < 150)
                return false;

            // Stock Dolphin NetPlay layout: Start is at bottom-left; Buffer spin box is
            // immediately to its right. Values are intentionally near the center of each
            // clickable control rather than on an edge or spinner arrow.
            double xRatio = target == NetPlayLobbyControllerTarget.Start ? 0.075 : 0.245;
            double yRatio = 0.965;

            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(lobby, ref origin))
                return false;

            screenPoint.X = origin.X + (int)Math.Round(width * xRatio);
            screenPoint.Y = origin.Y + (int)Math.Round(height * yRatio);
            return true;
        }

        private static bool MoveCursorToNetPlayLobbyTarget(IntPtr lobby, NetPlayLobbyControllerTarget target)
        {
            POINT point;
            if (!TryGetNetPlayLobbyTargetPoint(lobby, target, out point))
                return false;

            if (GetForegroundWindow() != lobby || !IsWindowEnabled(lobby))
                return false;

            return SetCursorPos(point.X, point.Y);
        }

        private static bool ClickNetPlayLobbyTarget(IntPtr lobby, NetPlayLobbyControllerTarget target)
        {
            POINT point;
            if (!TryGetNetPlayLobbyTargetPoint(lobby, target, out point))
                return false;

            if (GetForegroundWindow() != lobby || !IsWindowEnabled(lobby))
                return false;

            ClickAt(point.X, point.Y);
            return GetForegroundWindow() == lobby || !IsWindowEnabled(lobby);
        }

        private static bool RestoreNetPlayLobbyForeground(IntPtr lobby, int processId)
        {
            if (lobby == IntPtr.Zero || !IsWindow(lobby) || !IsWindowEnabled(lobby))
                return false;

            uint lobbyPid = 0;
            uint lobbyThread = GetWindowThreadProcessId(lobby, out lobbyPid);
            if (lobbyPid != (uint)processId)
                return false;

            // This is deliberately narrower than the general updater/launcher foreground
            // helpers: no physical click and no controller operation. It only restores the
            // already-validated NetPlay HWND after RC24 has observed the game/render -> main
            // window transition in the same Dolphin process.
            try
            {
                ShowWindow(lobby, SW_RESTORE);
                IntPtr foreground = GetForegroundWindow();
                uint dummyPid = 0;
                uint foregroundThread = foreground != IntPtr.Zero
                    ? GetWindowThreadProcessId(foreground, out dummyPid)
                    : 0;
                uint currentThread = GetCurrentThreadId();
                bool attachedLobby = false;
                bool attachedForeground = false;
                try
                {
                    if (lobbyThread != 0 && lobbyThread != currentThread)
                        attachedLobby = AttachThreadInput(currentThread, lobbyThread, true);
                    if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != lobbyThread)
                        attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

                    BringWindowToTop(lobby);
                    SetForegroundWindow(lobby);
                    SetFocus(lobby);
                }
                finally
                {
                    if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
                    if (attachedLobby) AttachThreadInput(currentThread, lobbyThread, false);
                }

                DateTime verifyUntil = DateTime.Now.AddMilliseconds(500);
                while (DateTime.Now < verifyUntil)
                {
                    if (GetForegroundWindow() == lobby)
                        return true;
                    Thread.Sleep(25);
                    Application.DoEvents();
                }
            }
            catch { }

            return GetForegroundWindow() == lobby;
        }

        private static bool RestoreLauncherForeground(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return false;

            IntPtr hWnd = form.Handle;
            DateTime deadline = DateTime.Now.AddSeconds(2);
            while (DateTime.Now < deadline && !form.IsDisposed)
            {
                try
                {
                    form.BringToFront();
                    form.Activate();
                    ShowWindow(hWnd, SW_RESTORE);

                    IntPtr foreground = GetForegroundWindow();
                    uint dummyPid;
                    uint targetThread = GetWindowThreadProcessId(hWnd, out dummyPid);
                    uint foregroundThread = foreground != IntPtr.Zero
                        ? GetWindowThreadProcessId(foreground, out dummyPid)
                        : 0;
                    uint currentThread = GetCurrentThreadId();
                    bool attachedTarget = false;
                    bool attachedForeground = false;
                    try
                    {
                        if (targetThread != 0 && targetThread != currentThread)
                            attachedTarget = AttachThreadInput(currentThread, targetThread, true);
                        if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                            attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

                        BringWindowToTop(hWnd);
                        SetForegroundWindow(hWnd);
                        SetFocus(hWnd);
                    }
                    finally
                    {
                        if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
                        if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
                    }

                    Application.DoEvents();
                    if (GetForegroundWindow() == hWnd)
                    {
                        DiagnosticsLog.Write("FOCUS", "Returned launcher successfully reclaimed the foreground window.");
                        return true;
                    }
                }
                catch { }

                Thread.Sleep(80);
                Application.DoEvents();
            }

            IntPtr finalForeground = GetForegroundWindow();
            uint finalPid = 0;
            if (finalForeground != IntPtr.Zero) GetWindowThreadProcessId(finalForeground, out finalPid);
            DiagnosticsLog.Write("FOCUS", "Returned launcher could not reclaim foreground automatically; foreground PID " + finalPid.ToString() + ".");
            return false;
        }

        private static bool EnsureWindowForeground(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
                return false;

            // The updater continuation often happens immediately after UAC. If the user
            // clicks another app during that handoff, Windows' foreground-lock rules can
            // reject a plain SetForegroundWindow call even though launcher and Dolphin
            // now have matching integrity levels. Keep trying for a few seconds and, when
            // needed, temporarily attach the input queues so the focus transfer is real.
            DateTime overallDeadline = DateTime.Now.AddSeconds(5);

            while (DateTime.Now < overallDeadline)
            {
                // If Dolphin opened ANY other visible top-level window, do not keep
                // trying to activate/click the base window. In the updater flow this
                // means a modal such as "Update available" has appeared. Continuing
                // to focus or title-bar click the base window is exactly what causes
                // Windows' modal beep/title-bar flash.
                if (!IsWindowEnabled(hWnd) ||
                    HasSecondaryVisibleWindowForProcess(processId, hWnd))
                    return false;

                try
                {
                    ShowWindow(hWnd, SW_RESTORE);

                    IntPtr foreground = GetForegroundWindow();
                    uint dummyPid;
                    uint targetThread = GetWindowThreadProcessId(hWnd, out dummyPid);
                    uint foregroundThread = foreground != IntPtr.Zero
                        ? GetWindowThreadProcessId(foreground, out dummyPid)
                        : 0;
                    uint currentThread = GetCurrentThreadId();

                    bool attachedTarget = false;
                    bool attachedForeground = false;
                    try
                    {
                        if (targetThread != 0 && targetThread != currentThread)
                            attachedTarget = AttachThreadInput(currentThread, targetThread, true);

                        if (foregroundThread != 0 &&
                            foregroundThread != currentThread &&
                            foregroundThread != targetThread)
                            attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

                        BringWindowToTop(hWnd);
                        SetForegroundWindow(hWnd);
                        SetFocus(hWnd);
                    }
                    finally
                    {
                        if (attachedForeground)
                            AttachThreadInput(currentThread, foregroundThread, false);
                        if (attachedTarget)
                            AttachThreadInput(currentThread, targetThread, false);
                    }

                    DateTime verifyDeadline = DateTime.Now.AddMilliseconds(450);
                    while (DateTime.Now < verifyDeadline)
                    {
                        if (GetForegroundWindow() == hWnd)
                        {
                            // Require a short stable interval. This prevents a user click
                            // made during the UAC/relaunch transition from stealing focus
                            // between the verification and the next SendKeys call.
                            Thread.Sleep(180);
                            if (GetForegroundWindow() == hWnd)
                                return true;
                        }
                        Thread.Sleep(35);
                        Application.DoEvents();
                    }

                    // A modal can appear during the verification delay above.
                    // Re-check before the last-resort physical activation so we never
                    // click a disabled/blocked Dolphin base window.
                    if (!IsWindowEnabled(hWnd) ||
                        HasSecondaryVisibleWindowForProcess(processId, hWnd))
                        return false;

                    // Last-resort physical activation. This is intentionally only the
                    // title bar; no Dolphin controls are activated.
                    RECT rect;
                    if (GetWindowRect(hWnd, out rect))
                    {
                        int x = rect.Left + Math.Min(180, Math.Max(60, (rect.Right - rect.Left) / 4));
                        int y = rect.Top + 12;
                        ClickAt(x, y);

                        Thread.Sleep(180);
                        if (GetForegroundWindow() == hWnd)
                        {
                            Thread.Sleep(180);
                            if (GetForegroundWindow() == hWnd)
                                return true;
                        }
                    }
                }
                catch
                {
                    // Retry until the overall deadline.
                }

                Thread.Sleep(120);
                Application.DoEvents();
            }

            return false;
        }

        private static bool EnsureWindowForegroundBeforeSend(IntPtr hWnd, int processId)
        {
            // Reacquire rather than merely checking. If the user clicked elsewhere while
            // Dolphin was opening, this gives Dolphin one more controlled chance to take
            // focus before each stage of the updater menu sequence.
            return GetForegroundWindow() == hWnd || EnsureWindowForeground(hWnd, processId);
        }

        private static void RunDolphinUpdateCheck(string dolphinExe, Form owner, DnlSettings settings)
        {
            DiagnosticsLog.Write("UPDATE", "Check for Dolphin Update requested.");
            if (Process.GetProcessesByName("Dolphin").Length > 0)
            {
                Error("Close Dolphin before checking for updates.");
                return;
            }

            WarningForm warning = new WarningForm(
                "Opening Dolphin's updater...",
                "Please don't move the mouse for a moment.");

            Process proc = null;
            bool ownerWasVisible = owner != null && owner.Visible;

            try
            {
                // Keep our launcher out of the way so Dolphin's updater dialogs cannot
                // appear behind it.
                if (ownerWasVisible)
                {
                    owner.Hide();
                    Application.DoEvents();
                }

                warning.Show();
                warning.Refresh();
                Application.DoEvents();

                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = dolphinExe,
                    UseShellExecute = true
                });
                if (proc == null) throw new Exception("Dolphin could not be started.");

                bool launcherElevated = IsCurrentProcessElevated();
                bool dolphinElevated = IsProcessElevated(proc);
                if (dolphinElevated && !launcherElevated)
                {
                    HideAutomationWarnings();

                    DialogResult elevate = MessageBox.Show(
                        "Dolphin is running as administrator, but Dolphin NetPlay Launcher is not.\n\n" +
                        "Windows blocks the keyboard automation used to open Dolphin's updater.\n\n" +
                        "Restart Dolphin NetPlay Launcher as administrator and continue the update check?",
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly);

                    if (elevate == DialogResult.Yes)
                    {
                        string[] originalArgs = Environment.GetCommandLineArgs();
                        List<string> relaunchArgs = new List<string>();
                        for (int i = 1; i < originalArgs.Length; i++)
                        {
                            if (!originalArgs[i].StartsWith("--cleanup-dolphin-pid=", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(originalArgs[i], "--resume-update-check", StringComparison.OrdinalIgnoreCase))
                                relaunchArgs.Add(originalArgs[i]);
                        }

                        if (RelaunchSelfElevated(
                            relaunchArgs.ToArray(),
                            proc.Id,
                            "--resume-update-check"))
                        {
                            Environment.Exit(0);
                        }
                    }

                    throw new OperationCanceledException(
                        "Dolphin is elevated while Dolphin NetPlay Launcher is not.");
                }

                DateTime deadline = DateTime.Now.AddSeconds(15);
                while (!HasExitedSafe(proc) && proc.MainWindowHandle == IntPtr.Zero && DateTime.Now < deadline)
                {
                    Thread.Sleep(150);
                    proc.Refresh();
                    Application.DoEvents();
                }
                if (HasExitedSafe(proc) || proc.MainWindowHandle == IntPtr.Zero)
                    throw new Exception("Dolphin's main window was not detected.");

                // Process.MainWindowHandle can change when Dolphin opens a modal dialog.
                // In the real-update case it may point at "Update available" instead of the
                // actual Dolphin application window. Find the largest visible top-level
                // window owned by this exact Dolphin PID; that is the stable base window we
                // should use for enabled/disabled state and all updater safety checks.
                IntPtr dolphinMain = FindLargestVisibleWindowForProcess(proc.Id);
                if (dolphinMain == IntPtr.Zero)
                    dolphinMain = proc.MainWindowHandle;

                // Dolphin can perform an automatic update check shortly after startup when
                // AutoUpdate.UpdateTrack is configured. Do NOT race our manual Help > Check
                // for Updates automation against that startup check.
                //
                // If auto-update is configured, give Dolphin a short observation window in
                // which to present its own update modal. If one appears, hand off to the
                // normal updater observer and send ZERO menu keystrokes.
                //
                // If auto-update is not configured, only use a tiny settle delay so the
                // manual check still starts quickly.
                DolphinPaths updatePaths = BuildPaths(dolphinExe);
                string configuredUpdateTrack = "";
                try
                {
                    configuredUpdateTrack = ReadIni(
                        updatePaths.DolphinIni, "AutoUpdate", "UpdateTrack", "");
                }
                catch { configuredUpdateTrack = ""; }

                int startupObserveMs =
                    string.IsNullOrWhiteSpace(configuredUpdateTrack) ? 250 : 4000;

                DateTime startupObserveDeadline =
                    DateTime.Now.AddMilliseconds(startupObserveMs);

                while (!HasExitedSafe(proc) && DateTime.Now < startupObserveDeadline)
                {
                    IntPtr currentBase = FindLargestVisibleWindowForProcess(proc.Id);
                    if (currentBase != IntPtr.Zero)
                        dolphinMain = currentBase;

                    if (!IsWindowEnabled(dolphinMain) ||
                        HasSecondaryVisibleWindowForProcess(proc.Id, dolphinMain))
                    {
                        warning.Close();
                        warning.Dispose();
                        WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                            settings != null ? settings.UpdateCloseGraceMs : 1000,
                            settings == null || settings.ShowControllerPrompts,
                            settings != null ? settings.ControllerPromptStyle : "Xbox");
                        return;
                    }

                    Thread.Sleep(40);
                    Application.DoEvents();
                }

                // A downgraded/portable Dolphin may perform its own automatic update check
                // immediately on startup. In that case its update dialog can appear BEFORE
                // we get a chance to drive Help > Check for Updates. The main window is then
                // disabled by the modal. Never fight that modal for focus: doing so causes
                // Windows' disabled-window beep/title-bar flash and can make the launcher
                // report a false keyboard-focus failure even though Dolphin's updater UI is
                // already open.
                //
                // If the exact Dolphin process already owns a visible secondary window while
                // its main window is disabled, the desired update flow has already started.
                // Skip ALL menu keystrokes and go straight to the normal updater observer.
                if (!IsWindowEnabled(dolphinMain) ||
                    HasSecondaryVisibleWindowForProcess(proc.Id, dolphinMain))
                {
                    warning.Close();
                    warning.Dispose();
                    WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                        settings != null ? settings.UpdateCloseGraceMs : 1000,
                        settings == null || settings.ShowControllerPrompts,
                        settings != null ? settings.ControllerPromptStyle : "Xbox");
                    return;
                }

                // NEVER send updater navigation keystrokes unless Dolphin is verifiably
                // the foreground window. A failed SetForegroundWindow can otherwise send
                // Alt/arrow/Enter to whatever app the user was using.
                if (!EnsureWindowForeground(dolphinMain, proc.Id))
                {
                    if (!IsWindowEnabled(dolphinMain) ||
                        HasSecondaryVisibleWindowForProcess(proc.Id, dolphinMain))
                    {
                        warning.Close();
                        warning.Dispose();
                        WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                            settings != null ? settings.UpdateCloseGraceMs : 1000,
                            settings == null || settings.ShowControllerPrompts,
                            settings != null ? settings.ControllerPromptStyle : "Xbox");
                        return;
                    }

                    throw new Exception(
                        "Dolphin opened, but Windows would not give it keyboard focus.\n\n" +
                        "No update-check keystrokes were sent.");
                }

                // Re-check immediately after focus acquisition. Dolphin's automatic update
                // dialog can race us and appear during these few milliseconds. If it did,
                // stop before sending Alt+H or any other menu input to a disabled main window.
                if (!IsWindowEnabled(dolphinMain) ||
                    HasSecondaryVisibleWindowForProcess(proc.Id, dolphinMain))
                {
                    warning.Close();
                    warning.Dispose();
                    WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                        settings != null ? settings.UpdateCloseGraceMs : 1000,
                        settings == null || settings.ShowControllerPrompts,
                        settings != null ? settings.ControllerPromptStyle : "Xbox");
                    return;
                }

                // Keep this sequence responsive, but never trade away the foreground
                // checks. These short pauses only give Dolphin/Qt enough time to process
                // the previous menu key; every stage below still verifies the exact
                // Dolphin main window before another key is sent.
                Thread.Sleep(110);
                SendKeys.SendWait("%h");
                Thread.Sleep(120);

                // IMPORTANT: once Help is open, Dolphin/Qt's QMenu may appear as another
                // visible top-level window owned by the same Dolphin PID. That is NORMAL.
                // Do not interpret "secondary window exists" as an updater modal from this
                // point forward. A real modal blocks/disables the stable Dolphin base window,
                // so the post-menu stages use IsWindowEnabled(dolphinMain) as the stop gate.

                if (!IsWindowEnabled(dolphinMain))
                {
                    warning.Close();
                    warning.Dispose();
                    WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                        settings != null ? settings.UpdateCloseGraceMs : 1000,
                        settings == null || settings.ShowControllerPrompts,
                        settings != null ? settings.ControllerPromptStyle : "Xbox");
                    return;
                }

                if (!EnsureWindowForegroundBeforeSend(dolphinMain, proc.Id))
                    throw new Exception(
                        "Dolphin lost keyboard focus before the update menu could be opened.\n\n" +
                        "No further update-check keystrokes were sent.");

                SendKeys.SendWait("{HOME}");
                Thread.Sleep(50);

                if (!IsWindowEnabled(dolphinMain))
                {
                    warning.Close();
                    warning.Dispose();
                    WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                        settings != null ? settings.UpdateCloseGraceMs : 1000,
                        settings == null || settings.ShowControllerPrompts,
                        settings != null ? settings.ControllerPromptStyle : "Xbox");
                    return;
                }

                if (!EnsureWindowForegroundBeforeSend(dolphinMain, proc.Id))
                    throw new Exception(
                        "Dolphin lost keyboard focus during the update check.\n\n" +
                        "No further update-check keystrokes were sent.");

                SendKeys.SendWait("{DOWN 4}");
                Thread.Sleep(50);

                if (!IsWindowEnabled(dolphinMain))
                {
                    warning.Close();
                    warning.Dispose();
                    WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                        settings != null ? settings.UpdateCloseGraceMs : 1000,
                        settings == null || settings.ShowControllerPrompts,
                        settings != null ? settings.ControllerPromptStyle : "Xbox");
                    return;
                }

                if (!EnsureWindowForegroundBeforeSend(dolphinMain, proc.Id))
                    throw new Exception(
                        "Dolphin lost keyboard focus during the update check.\n\n" +
                        "No further update-check keystrokes were sent.");

                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(100);

                // The mouse-sensitive automation is finished. From here forward we only
                // observe Dolphin/updater process and window state.
                warning.Close();
                warning.Dispose();

                WaitForDolphinUpdateFlow(proc, dolphinExe, dolphinMain,
                    settings != null ? settings.UpdateCloseGraceMs : 1000,
                    settings == null || settings.ShowControllerPrompts,
                    settings != null ? settings.ControllerPromptStyle : "Xbox");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Exception("Dolphin update check failed", ex);
                Error("Could not complete Dolphin's update check.\n\n" + ex.Message);
            }
            finally
            {
                if (!warning.IsDisposed)
                {
                    warning.Close();
                    warning.Dispose();
                }

                if (owner != null && !owner.IsDisposed)
                {
                    owner.Show();
                    owner.Activate();
                    owner.BringToFront();
                }
            }
        }

        private static void WaitForDolphinUpdateFlow(
            Process originalDolphin, string dolphinExe, IntPtr originalMainWindow,
            int closeGraceMs, bool showControllerPrompts, string controllerPromptStyle)
        {
            int originalPid = originalDolphin.Id;
            bool sawUpdateUi = false;
            bool sawUpdater = false;
            DateTime observationStart = DateTime.Now;

            // Controller confirmation is intentionally scoped ONLY to this update-check
            // routine. RunDolphinUpdateCheck refuses to start while any Dolphin process
            // already exists, so originalDolphin is a fresh, updater-only Dolphin instance.
            // We additionally require its main window to be disabled and a secondary
            // Dolphin window to be the ACTUAL foreground window before Enter can be sent.
            // Once that modal disappears, controller-to-Enter translation stops immediately.
            IntPtr controllerModal = IntPtr.Zero;
            bool controllerConfirmArmed = false;
            using (UpdaterControllerPromptForm controllerPrompt =
                new UpdaterControllerPromptForm(showControllerPrompts, controllerPromptStyle))
            {

            // First wait for the updater/check UI to actually appear. This prevents us
            // from mistaking the short delay after the menu click for a completed check.
            while ((DateTime.Now - observationStart).TotalSeconds < 30)
            {
                Application.DoEvents();

                if (AnyUpdaterFromDirectoryRunning(Path.GetDirectoryName(dolphinExe)))
                {
                    sawUpdater = true;
                    sawUpdateUi = true;
                    break;
                }

                if (HasExitedSafe(originalDolphin))
                    break;

                if (HasSecondaryVisibleWindowForProcess(originalPid, originalMainWindow) ||
                    (originalMainWindow != IntPtr.Zero && !IsWindowEnabled(originalMainWindow)))
                {
                    sawUpdateUi = true;
                    break;
                }

                Thread.Sleep(150);
            }

            // If Dolphin itself vanished, it may already have handed control to Updater.exe.
            // In that case, never send Dolphin a close request while the updater is alive.
            DateTime safetyDeadline = DateTime.Now.AddMinutes(15);
            DateTime? normalWindowSince = null;
            while (DateTime.Now < safetyDeadline)
            {
                Application.DoEvents();

                string dolphinDir = Path.GetDirectoryName(dolphinExe);
                bool updaterRunning = AnyUpdaterFromDirectoryRunning(dolphinDir);
                if (updaterRunning)
                {
                    sawUpdater = true;
                    controllerModal = IntPtr.Zero;
                    controllerConfirmArmed = false;
                    controllerPrompt.HidePrompt();
                    Thread.Sleep(250);
                    continue;
                }

                // Allow controller A / South or Start to acknowledge a Dolphin-owned modal
                // during this launcher-initiated update check. This method performs strict
                // process/window validation again immediately before SendKeys. In particular,
                // it NEVER translates controller input while Dolphin's normal main window is
                // enabled, which prevents a stray controller press from becoming Enter during
                // ordinary Dolphin use or gameplay.
                TryHandleUpdaterControllerConfirm(
                    originalDolphin, originalMainWindow, dolphinDir,
                    ref controllerModal, ref controllerConfirmArmed, controllerPrompt);

                // If an updater was launched, give it a moment after exit to finish file
                // replacement/relaunch work before touching Dolphin.
                if (sawUpdater)
                {
                    Thread.Sleep(1500);
                    Application.DoEvents();
                    if (AnyUpdaterFromDirectoryRunning(Path.GetDirectoryName(dolphinExe)))
                        continue;

                    Process relaunched = FindDolphinProcessByExecutable(dolphinExe);
                    if (relaunched != null)
                        GracefullyCloseWhenStable(relaunched);
                    controllerPrompt.HidePrompt();
                    return;
                }

                if (HasExitedSafe(originalDolphin))
                {
                    controllerPrompt.HidePrompt();
                    return;
                }

                bool secondary = HasSecondaryVisibleWindowForProcess(originalPid, originalMainWindow);
                bool mainEnabled = originalMainWindow == IntPtr.Zero || IsWindowEnabled(originalMainWindow);

                // No external updater appeared. Once Dolphin's modal/update-check window
                // is gone and its main window is usable again, the user has finished the
                // check (up-to-date, cancelled, or declined). Now it is safe to close the
                // temporary Dolphin instance that Dolphin NetPlay Launcher started.
                if (sawUpdateUi && !secondary && mainEnabled)
                {
                    if (!normalWindowSince.HasValue)
                        normalWindowSince = DateTime.Now;

                    // Require a short continuously-stable period before closing. If the
                    // user accepted an update, Updater.exe normally appears during this
                    // handoff window and cancels the close path.
                    if ((DateTime.Now - normalWindowSince.Value).TotalMilliseconds >= Clamp(closeGraceMs, 500, 5000))
                    {
                        if (!AnyUpdaterFromDirectoryRunning(Path.GetDirectoryName(dolphinExe)))
                        {
                            GracefullyCloseWhenStable(originalDolphin);
                            controllerPrompt.HidePrompt();
                            return;
                        }
                    }
                }
                else
                {
                    normalWindowSince = null;
                }

                // Poll this temporary update-check state often enough to catch a normal
                // controller button tap. The old 200 ms cadence could miss short presses,
                // making A/Start feel as though it needed to be held or pressed repeatedly.
                // This loop exists only while the launcher-initiated update check is active.
                Thread.Sleep(25);
            }

            // Safety timeout: leave Dolphin alone rather than risk closing during an
            // unusually long or changed updater workflow.
            controllerPrompt.HidePrompt();
            }
        }

        private static void TryHandleUpdaterControllerConfirm(
            Process dolphinProcess,
            IntPtr dolphinMainWindow,
            string dolphinDir,
            ref IntPtr trackedModal,
            ref bool armed,
            UpdaterControllerPromptForm prompt)
        {
            SdlControllerManager controller = ControllerManagerForChildForms;
            if (controller == null || !controller.NavigationEnabled ||
                dolphinProcess == null || HasExitedSafe(dolphinProcess) ||
                dolphinMainWindow == IntPtr.Zero || !IsWindow(dolphinMainWindow) ||
                IsWindowEnabled(dolphinMainWindow) ||
                AnyUpdaterFromDirectoryRunning(dolphinDir))
            {
                trackedModal = IntPtr.Zero;
                armed = false;
                if (prompt != null) prompt.HidePrompt();
                return;
            }

            // Do not hunt for a window and then force it forward. The modal must ALREADY be
            // foreground. That makes the destination of Enter unambiguous and prevents a
            // controller press from stealing focus from some unrelated application.
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == dolphinMainWindow || !IsWindow(foreground) ||
                !IsWindowVisible(foreground))
            {
                trackedModal = IntPtr.Zero;
                armed = false;
                if (prompt != null) prompt.HidePrompt();
                return;
            }

            uint foregroundPid;
            GetWindowThreadProcessId(foreground, out foregroundPid);
            if (foregroundPid != (uint)dolphinProcess.Id)
            {
                trackedModal = IntPtr.Zero;
                armed = false;
                if (prompt != null) prompt.HidePrompt();
                return;
            }

            // The foreground secondary window must also be one of the exact Dolphin process's
            // visible top-level secondary windows. This is deliberately redundant validation.
            if (!IsVisibleSecondaryWindowForProcess(foreground, dolphinProcess.Id, dolphinMainWindow))
            {
                trackedModal = IntPtr.Zero;
                armed = false;
                if (prompt != null) prompt.HidePrompt();
                return;
            }

            if (trackedModal != foreground)
            {
                trackedModal = foreground;
                armed = false;
            }

            if (prompt != null)
                prompt.ShowForWindow(foreground);

            ControllerAction action = controller.Poll(false);

            // The first controller edge observed after a modal appears is ignored. This means
            // a button that was already held while the updater dialog appeared cannot dismiss
            // it accidentally; the user must release and press A/Start again.
            if (!armed)
            {
                if (action == ControllerAction.None)
                    armed = true;
                return;
            }

            bool confirmRequested =
                action == ControllerAction.Accept || action == ControllerAction.FocusPrimary;
            bool cancelRequested = action == ControllerAction.Cancel;

            if (!confirmRequested && !cancelRequested)
                return;

            // Last-moment safety gate. Every condition above is checked again immediately
            // before Enter/Escape. If ANYTHING changed -- focus, process, modal, main-window
            // enabled state, or Updater.exe starting -- no key is sent.
            if (HasExitedSafe(dolphinProcess) ||
                IsWindowEnabled(dolphinMainWindow) ||
                AnyUpdaterFromDirectoryRunning(dolphinDir) ||
                GetForegroundWindow() != foreground ||
                !IsVisibleSecondaryWindowForProcess(foreground, dolphinProcess.Id, dolphinMainWindow))
            {
                armed = false;
                if (prompt != null) prompt.HidePrompt();
                return;
            }

            if (cancelRequested)
            {
                // Close the exact validated Dolphin modal as though the user clicked its
                // window close button. This is more reliable than assuming Escape is bound
                // by every Dolphin updater dialog, and it cannot target the main/game window
                // because 'foreground' passed the exact modal safety checks above.
                PostMessage(foreground, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                SendKeys.SendWait("{ENTER}");
            }

            armed = false;
        }

        private static bool IsVisibleSecondaryWindowForProcess(IntPtr window, int processId, IntPtr mainWindow)
        {
            if (window == IntPtr.Zero || window == mainWindow ||
                !IsWindow(window) || !IsWindowVisible(window))
                return false;

            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != (uint)processId)
                return false;

            bool found = false;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == window && hWnd != mainWindow && IsWindowVisible(hWnd))
                {
                    uint candidatePid;
                    GetWindowThreadProcessId(hWnd, out candidatePid);
                    if (candidatePid == (uint)processId)
                    {
                        found = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsJoinResultModal(IntPtr window, int processId, IntPtr dolphinMainWindow, IntPtr setupWindow)
        {
            if (window == IntPtr.Zero || window == dolphinMainWindow || window == setupWindow ||
                !IsWindow(window) || !IsWindowVisible(window))
                return false;

            uint pid;
            GetWindowThreadProcessId(window, out pid);
            return pid == (uint)processId;
        }

        private static void CloseNetPlaySetupAndDolphin(Process proc, IntPtr setup)
        {
            if (proc == null || HasExitedSafe(proc))
                return;

            if (setup != IntPtr.Zero && IsWindow(setup))
            {
                PostMessage(setup, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                DateTime setupCloseDeadline = DateTime.Now.AddSeconds(2);
                while (IsWindow(setup) && DateTime.Now < setupCloseDeadline && !HasExitedSafe(proc))
                {
                    Thread.Sleep(100);
                    Application.DoEvents();
                }
            }

            if (!HasExitedSafe(proc))
                GracefullyCloseWhenStable(proc);
        }

        private static void GracefullyCloseWhenStable(Process proc)
        {
            if (proc == null || HasExitedSafe(proc)) return;

            DateTime deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline && !HasExitedSafe(proc))
            {
                proc.Refresh();
                IntPtr main = proc.MainWindowHandle;
                if (main != IntPtr.Zero && IsWindowEnabled(main) &&
                    !HasSecondaryVisibleWindowForProcess(proc.Id, main))
                {
                    proc.CloseMainWindow();
                    return;
                }
                Thread.Sleep(200);
                Application.DoEvents();
            }
        }

        private static IntPtr FindLargestVisibleWindowForProcess(int processId)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != (uint)processId)
                    return true;

                RECT rect;
                if (!GetWindowRect(hWnd, out rect))
                    return true;

                long width = Math.Max(0, rect.Right - rect.Left);
                long height = Math.Max(0, rect.Bottom - rect.Top);
                long area = width * height;

                if (area > bestArea)
                {
                    bestArea = area;
                    best = hWnd;
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private static bool HasSecondaryVisibleWindowForProcess(int processId, IntPtr mainWindow)
        {
            bool found = false;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == mainWindow || !IsWindowVisible(hWnd)) return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == (uint)processId)
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool AnyUpdaterFromDirectoryRunning(string dolphinDir)
        {
            foreach (Process p in Process.GetProcessesByName("Updater"))
            {
                try
                {
                    string exe = p.MainModule.FileName;
                    if (string.Equals(Path.GetDirectoryName(exe), dolphinDir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        private static Process FindDolphinProcessByExecutable(string dolphinExe)
        {
            foreach (Process p in Process.GetProcessesByName("Dolphin"))
            {
                try
                {
                    string exe = p.MainModule.FileName;
                    if (string.Equals(exe, dolphinExe, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                catch { }
                p.Dispose();
            }
            return null;
        }

        private static bool HasExitedSafe(Process p)
        {
            try { return p == null || p.HasExited; }
            catch { return true; }
        }

        internal static Image TryLoadBannerFromGameListCache(DolphinPaths paths, string romPath)
        {
            if (paths == null || string.IsNullOrWhiteSpace(paths.GameListCache) ||
                !File.Exists(paths.GameListCache) || string.IsNullOrWhiteSpace(romPath))
                return null;

            try
            {
                using (FileStream fs = File.Open(paths.GameListCache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8))
                {
                    // GameFileCache::DoState serializes a native header struct:
                    //   u32 revision;
                    //   [4 bytes padding on Dolphin's 64-bit desktop builds]
                    //   u64 expected_size;
                    // followed by a u32 element count.
                    //
                    // Current Dolphin cache revision is 27. If Dolphin changes the
                    // layout/revision, simply fail back to the text-only header.
                    if (fs.Length < 20)
                        return null;

                    uint revision = br.ReadUInt32();
                    br.ReadUInt32(); // native struct alignment padding
                    ulong expectedSize = br.ReadUInt64();
                    if (revision != 27 || expectedSize != (ulong)fs.Length)
                        return null;

                    uint entryCount = br.ReadUInt32();
                    if (entryCount > 100000)
                        return null;

                    string wanted = NormalizeCachePath(romPath);

                    for (uint entry = 0; entry < entryCount; entry++)
                    {
                        br.ReadByte(); // m_valid

                        string filePath = ReadCacheString(br);
                        string fileName = ReadCacheString(br);

                        br.ReadUInt64(); // m_file_size
                        br.ReadUInt64(); // m_volume_size
                        br.ReadInt32();  // m_volume_size_type
                        br.ReadByte();   // m_is_datel_disc
                        br.ReadByte();   // m_is_nkit

                        for (int i = 0; i < 5; i++)
                            SkipLanguageStringMap(br);

                        ReadCacheString(br); // m_internal_name
                        ReadCacheString(br); // m_game_id
                        ReadCacheString(br); // m_gametdb_id
                        br.ReadUInt64();      // m_title_id
                        ReadCacheString(br); // m_maker_id
                        br.ReadInt32();       // m_region
                        br.ReadInt32();       // m_country
                        br.ReadInt32();       // m_platform
                        br.ReadInt32();       // m_blob_type
                        br.ReadUInt64();      // m_block_size
                        ReadCacheString(br); // m_compression_method
                        br.ReadUInt16();      // m_revision
                        br.ReadByte();        // m_disc_number
                        br.ReadByte();        // m_is_two_disc_game
                        ReadCacheString(br); // m_apploader_date
                        ReadCacheString(br); // m_custom_name
                        ReadCacheString(br); // m_custom_description
                        ReadCacheString(br); // m_custom_maker

                        CachedBanner volume = ReadCachedBanner(br);
                        CachedBanner custom = ReadCachedBanner(br);

                        bool isWanted = string.Equals(
                            NormalizeCachePath(filePath), wanted,
                            StringComparison.OrdinalIgnoreCase);

                        if (isWanted)
                        {
                            CachedBanner chosen =
                                custom.Pixels != null && custom.Pixels.Length > 0 ? custom : volume;
                            return CachedBannerToBitmap(chosen);
                        }

                        // Two GameCover objects follow the two banners. Each cover is
                        // just a serialized vector<u8>; skip them so we land exactly at
                        // the next GameFile entry.
                        SkipByteVector(br); // m_default_cover
                        SkipByteVector(br); // m_custom_cover
                    }
                }
            }
            catch
            {
                // gamelist.cache is intentionally treated as optional. A malformed,
                // stale, locked, or future-format cache must never affect NetPlay.
            }

            return null;
        }

        private static string NormalizeCachePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            try { path = Path.GetFullPath(path); }
            catch { }

            return path.Replace('/', '\\').TrimEnd('\\');
        }

        private static Bitmap CachedBannerToBitmap(CachedBanner banner)
        {
            if (banner == null || banner.Pixels == null || banner.Pixels.Length == 0 ||
                banner.Width == 0 || banner.Height == 0 ||
                banner.Width > 4096 || banner.Height > 4096 ||
                (long)banner.Width * banner.Height != banner.Pixels.Length)
                return null;

            Bitmap bmp = new Bitmap((int)banner.Width, (int)banner.Height);
            int p = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++, p++)
                {
                    uint argb = banner.Pixels[p];
                    bmp.SetPixel(x, y, Color.FromArgb(
                        (int)((argb >> 24) & 0xFF),
                        (int)((argb >> 16) & 0xFF),
                        (int)((argb >> 8) & 0xFF),
                        (int)(argb & 0xFF)));
                }
            }
            return bmp;
        }

        private sealed class CachedBanner
        {
            public uint[] Pixels;
            public uint Width;
            public uint Height;
        }

        private static string ReadCacheString(BinaryReader br)
        {
            uint count = br.ReadUInt32();
            if (count > 16 * 1024 * 1024)
                throw new InvalidDataException("Unreasonable string length in Dolphin cache.");

            byte[] bytes = br.ReadBytes((int)count);
            if (bytes.Length != count)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        private static void SkipLanguageStringMap(BinaryReader br)
        {
            uint count = br.ReadUInt32();
            if (count > 128)
                throw new InvalidDataException("Unreasonable map size in Dolphin cache.");

            for (uint i = 0; i < count; i++)
            {
                br.ReadInt32(); // DiscIO::Language
                ReadCacheString(br);
            }
        }

        private static CachedBanner ReadCachedBanner(BinaryReader br)
        {
            uint count = br.ReadUInt32();
            if (count > 4 * 1024 * 1024)
                throw new InvalidDataException("Unreasonable banner size in Dolphin cache.");

            uint[] pixels = new uint[count];
            for (uint i = 0; i < count; i++)
                pixels[i] = br.ReadUInt32();

            return new CachedBanner
            {
                Pixels = pixels,
                Width = br.ReadUInt32(),
                Height = br.ReadUInt32()
            };
        }

        private static void SkipByteVector(BinaryReader br)
        {
            uint count = br.ReadUInt32();
            if (count > 128 * 1024 * 1024)
                throw new InvalidDataException("Unreasonable cover size in Dolphin cache.");

            if (br.BaseStream.Position + count > br.BaseStream.Length)
                throw new EndOfStreamException();

            br.BaseStream.Seek(count, SeekOrigin.Current);
        }

        internal static GameInfo ResolveGameInfoForLibrary(DolphinPaths paths, string romPath)
        {
            return ResolveGameInfo(paths, romPath);
        }

        private static GameInfo ResolveGameInfo(DolphinPaths paths, string romPath)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = paths.DolphinTool,
                Arguments = "header --json --input \"" + romPath.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new Exception("DolphinTool.exe could not be started.");

                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode != 0)
                    throw new Exception(string.IsNullOrWhiteSpace(error) ? output : error);

                string gameId = JsonString(output, "game_id");
                int revision = JsonInt(output, "revision", 0);
                string internalName = JsonString(output, "internal_name");

                if (string.IsNullOrWhiteSpace(internalName))
                    internalName = Path.GetFileNameWithoutExtension(romPath);

                string title = LookupTitle(paths, gameId, internalName);

                List<string> info = new List<string>();
                if (!string.IsNullOrWhiteSpace(gameId)) info.Add(gameId);
                if (revision != 0) info.Add("Revision " + revision);

                return new GameInfo
                {
                    Title = title,
                    GameId = gameId,
                    Revision = revision,
                    NetPlayName = info.Count == 0
                        ? title
                        : title + " (" + string.Join(", ", info.ToArray()) + ")"
                };
            }
        }

        private static string JsonString(string json, string key)
        {
            Match m = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
            if (!m.Success) return "";
            string s = m.Groups[1].Value;
            s = s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
            return s;
        }

        private static int JsonInt(string json, string key, int fallback)
        {
            Match m = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(-?\\d+)");
            int value;
            return m.Success && int.TryParse(m.Groups[1].Value, out value) ? value : fallback;
        }

        private static string LookupTitle(DolphinPaths paths, string gameId, string fallback)
        {
            string[] userDbs = { "wiitdb.txt", "titles.txt" };
            foreach (string name in userDbs)
            {
                string path = Path.Combine(paths.UserLoadDir, name);
                Dictionary<string, string> map = ReadTitleDb(path);
                if (map.ContainsKey(gameId)) return map[gameId];
                if (map.Count > 0) break;
            }
            Dictionary<string, string> built = ReadTitleDb(paths.BuiltInTitleDb);
            return built.ContainsKey(gameId) ? built[gameId] : fallback;
        }

        private static Dictionary<string, string> ReadTitleDb(string path)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return map;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string id = line.Substring(0, eq).Trim();
                string title = line.Substring(eq + 1).Trim();
                if (id.Length >= 4 && title.Length > 0) map[id] = title;
            }
            return map;
        }

        private static void SetHostGame(string qtIni, string netplayName)
        {
            List<string> lines = new List<string>(File.ReadAllLines(qtIni));
            int sectionStart = FindSection(lines, "netplay");
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1] != "") lines.Add("");
                lines.Add("[netplay]");
                sectionStart = lines.Count - 1;
            }
            int sectionEnd = FindSectionEnd(lines, sectionStart);
            string newLine = "hostgame=\"" + netplayName.Replace("\"", "\\\"") + "\"";
            int keyIndex = FindKey(lines, sectionStart, sectionEnd, "hostgame");
            if (keyIndex >= 0) lines[keyIndex] = newLine;
            else lines.Insert(sectionStart + 1, newLine);
            File.WriteAllLines(qtIni, lines.ToArray(), new UTF8Encoding(false));
        }

        private static string GetDolphinVersion(string dolphinExe, DnlSettings settings)
        {
            FileInfo exeInfo = null;
            try { exeInfo = new FileInfo(dolphinExe); }
            catch { }

            // Fast path: if Dolphin.exe is the same file as last launch, reuse the exact
            // version we already discovered. Dolphin updates replace/change the EXE, so
            // its timestamp/length automatically invalidate this cache.
            if (settings != null && exeInfo != null &&
                !string.IsNullOrWhiteSpace(settings.CachedDolphinVersion) &&
                !string.Equals(settings.CachedDolphinVersion, "Unknown version", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings.CachedDolphinVersionPath, dolphinExe, StringComparison.OrdinalIgnoreCase) &&
                settings.CachedDolphinVersionWriteTicks == exeInfo.LastWriteTimeUtc.Ticks &&
                settings.CachedDolphinVersionLength == exeInfo.Length)
            {
                return settings.CachedDolphinVersion;
            }

            string detected = null;

            // Preferred path: ask Dolphin directly. Exact and cheap when Dolphin does not
            // require an elevation boundary.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = dolphinExe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        string first = p.StandardOutput.ReadLine();
                        p.WaitForExit(3000);
                        if (!string.IsNullOrWhiteSpace(first))
                            detected = first.Trim();
                    }
                }
            }
            catch
            {
                // "Run as administrator" can block a redirected non-elevated launch.
            }

            // Elevated-Dolphin fallback: scan the EXE in small chunks instead of loading
            // the whole binary and creating one giant ASCII string every startup.
            if (string.IsNullOrWhiteSpace(detected))
            {
                try
                {
                    const int chunkSize = 256 * 1024;
                    const int overlap = 128;
                    byte[] buffer = new byte[chunkSize + overlap];

                    using (FileStream fs = new FileStream(
                        dolphinExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int carry = 0;
                        while (true)
                        {
                            int read = fs.Read(buffer, carry, chunkSize);
                            if (read <= 0) break;

                            int total = carry + read;
                            string ascii = Encoding.ASCII.GetString(buffer, 0, total);
                            Match match = Regex.Match(
                                ascii,
                                @"Dolphin\s+(\d{4}[a-z]?(?:-\d+)?)",
                                RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                detected = "Dolphin " + match.Groups[1].Value;
                                break;
                            }

                            carry = Math.Min(overlap, total);
                            if (carry > 0)
                                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                        }
                    }
                }
                catch { }
            }

            // Final fallback: Windows executable metadata.
            if (string.IsNullOrWhiteSpace(detected))
            {
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(dolphinExe);
                    string[] candidates = { info.ProductVersion, info.FileVersion };
                    foreach (string candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        Match match = Regex.Match(candidate, @"\d{4}[a-z]?(?:-\d+)?", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            detected = "Dolphin " + match.Value;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(detected))
                detected = "Unknown version";

            // Cache successful detections only. If discovery failed transiently, try again
            // next launch rather than permanently caching "Unknown version".
            if (settings != null && exeInfo != null &&
                !string.Equals(detected, "Unknown version", StringComparison.OrdinalIgnoreCase))
            {
                settings.CachedDolphinVersion = detected;
                settings.CachedDolphinVersionPath = dolphinExe;
                settings.CachedDolphinVersionWriteTicks = exeInfo.LastWriteTimeUtc.Ticks;
                settings.CachedDolphinVersionLength = exeInfo.Length;

                // Best effort only; startup must never fail merely because a performance
                // cache could not be written.
                try { SaveDnlSettings(settings); } catch { }
            }

            return detected;
        }

        private static string GetUpdateTrackLabel(string dolphinIni, string version)
        {
            string raw = ReadIni(dolphinIni, "AutoUpdate", "UpdateTrack", "");

            // If Dolphin explicitly stores a track, trust Dolphin's setting.
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (raw.Equals("dev", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("development", StringComparison.OrdinalIgnoreCase))
                    return "Development";

                // Dolphin uses the internal updater track name "beta" for its
                // user-facing release channel. Do not expose that internal name in the
                // launcher; Dolphin's Settings UI calls this Release.
                if (raw.Equals("stable", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("beta", StringComparison.OrdinalIgnoreCase))
                    return "Release";

                return raw;
            }

            // A blank UpdateTrack means "use this build's default track".
            // Current release builds use version strings such as:
            //   Dolphin 2606a
            // while development builds use strings such as:
            //   Dolphin 2606-364
            //
            // Infer only for display; Dolphin's own updater remains authoritative.
            if (!string.IsNullOrWhiteSpace(version))
            {
                string trimmed = version.Trim();

                if (System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed,
                    @"^Dolphin\s+\d{4}[A-Za-z]?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return "Release";

                if (System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed,
                    @"^Dolphin\s+\d{4}-\d+.*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return "Development";
            }

            return "Default (determined by Dolphin)";
        }

        private static string GetRecommendedDolphinExe()
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Recommended layout:
                // Dolphin\Dolphin.exe
                // Dolphin\DolphinNetPlayLauncher\DolphinNetPlayLauncher.exe
                DirectoryInfo launcherDir = new DirectoryInfo(appDir);
                if (launcherDir.Parent != null)
                {
                    string parentCandidate = Path.Combine(launcherDir.Parent.FullName, "Dolphin.exe");
                    if (File.Exists(parentCandidate))
                        return parentCandidate;
                }

                // Also support users who place the launcher directly beside Dolphin.exe.
                string sameFolderCandidate = Path.Combine(appDir, "Dolphin.exe");
                if (File.Exists(sameFolderCandidate))
                    return sameFolderCandidate;
            }
            catch { }

            return null;
        }

        private static string ResolveDolphinExe(DnlSettings settings)
        {
            string saved = settings != null ? settings.DolphinExe : null;
            if (!string.IsNullOrEmpty(saved) && File.Exists(saved)) return saved;

            string recommended = GetRecommendedDolphinExe();
            if (!string.IsNullOrEmpty(recommended))
            {
                settings.DolphinExe = recommended;
                SaveDnlSettings(settings);
                return recommended;
            }

            MessageBox.Show(
                "Dolphin NetPlay Launcher needs to know where Dolphin is installed.\n\n" +
                "Select Dolphin.exe once; the location will be remembered.",
                "Dolphin setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            string selected = BrowseForDolphin();
            if (!string.IsNullOrEmpty(selected))
            {
                settings.DolphinExe = selected;
                SaveDnlSettings(settings);
            }
            return selected;
        }

        private static bool RunFirstTimeSetup(DnlSettings settings, out string dolphinExe)
        {
            dolphinExe = null;

            string candidate = null;
            if (settings != null &&
                !string.IsNullOrWhiteSpace(settings.DolphinExe) &&
                File.Exists(settings.DolphinExe))
            {
                candidate = settings.DolphinExe;
            }
            else
            {
                candidate = GetRecommendedDolphinExe();
            }

            using (FirstRunDolphinForm form = new FirstRunDolphinForm(candidate, settings))
            {
                if (form.ShowDialog() != DialogResult.OK ||
                    string.IsNullOrWhiteSpace(form.SelectedDolphinExe))
                    return false;

                dolphinExe = form.SelectedDolphinExe;
            }

            settings.DolphinExe = dolphinExe;
            settings.LibrarySetupAcknowledged = false;
            SaveDnlSettings(settings);

            DolphinPaths paths = BuildPaths(dolphinExe);

            string[] required = { paths.DolphinExe, paths.DolphinTool };
            foreach (string requiredPath in required)
            {
                if (!File.Exists(requiredPath))
                {
                    Error("Required Dolphin file not found:\n" + requiredPath +
                          "\n\nChoose a different Dolphin installation.");
                    return false;
                }
            }

            if (!File.Exists(paths.DolphinIni) || !File.Exists(paths.QtIni))
            {
                DialogResult result = MessageBox.Show(
                    "Dolphin's configuration files were not found in:\n\n" +
                    paths.UserDir +
                    "\n\nDolphin needs to be opened once so it can initialize its user configuration." +
                    "\n\nOpen Dolphin now?",
                    "Dolphin setup required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result != DialogResult.Yes)
                    return false;

                if (!OpenDolphinAndWaitForSetup(dolphinExe))
                    return false;

                // Dolphin has just closed. Re-resolve its user folder/config now rather
                // than making the user manually reopen Dolphin NetPlay Launcher.
                paths = BuildPaths(dolphinExe);

                if (!File.Exists(paths.DolphinIni) || !File.Exists(paths.QtIni))
                {
                    Error(
                        "Dolphin closed, but its configuration files still could not be found.\n\n" +
                        "Open Dolphin once more and make sure it finishes its first-run setup.");
                    return false;
                }
            }

            settings.FirstRunComplete = true;
            settings.LibrarySetupAcknowledged = false;
            SaveDnlSettings(settings);
            return true;
        }

        private static bool OpenDolphinAndWaitForSetup(string dolphinExe)
        {
            try
            {
                Process p = Process.Start(dolphinExe);
                if (p == null)
                    return false;

                // During onboarding the launcher has no main window yet, so waiting here
                // keeps setup alive invisibly. As soon as the exact Dolphin process closes,
                // onboarding resumes automatically in this same launcher process.
                p.WaitForExit();
                return true;
            }
            catch (Exception ex)
            {
                Error("Could not start Dolphin.\n\n" + ex.Message);
                return false;
            }
        }


        private static bool EnsureFirstRunLibrarySetup(
            DnlSettings settings,
            DolphinPaths paths,
            string dolphinExe)
        {
            if (settings == null)
                return true;

            while (true)
            {
                paths = BuildPaths(dolphinExe);
                List<string> roots = GetDolphinGameRoots(paths);

                // The real Dolphin configuration is authoritative.
                if (roots.Count > 0)
                {
                    settings.LibrarySetupAcknowledged = true;
                    SaveDnlSettings(settings);
                    return true;
                }

                // A deliberate opt-out remains respected on future launches.
                if (settings.LibrarySetupAcknowledged)
                    return true;

                using (FirstRunLibraryForm form = new FirstRunLibraryForm(0, paths.UserDir, settings))
                {
                    DialogResult result = form.ShowDialog();

                    if (result == DialogResult.Retry)
                    {
                        if (!OpenDolphinAndWaitForSetup(dolphinExe))
                            return false;

                        // Dolphin just closed. Loop immediately and inspect its game paths.
                        // If the user added a library, setup completes and the main launcher
                        // opens automatically. If not, the library prompt appears again.
                        continue;
                    }

                    if (result != DialogResult.OK)
                        return false;
                }

                // Explicit "Continue Without Library".
                settings.LibrarySetupAcknowledged = true;
                SaveDnlSettings(settings);
                return true;
            }
        }


        internal static List<string> GetDolphinGameRoots(DolphinPaths paths)
        {
            List<string> roots = new List<string>();
            try
            {
                string dolphinIni = paths.DolphinIni;
                if (File.Exists(dolphinIni))
                {
                    string[] lines = File.ReadAllLines(dolphinIni);
                    foreach (string raw in lines)
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim().Trim('"');

                        if (key.StartsWith("ISOPath", StringComparison.OrdinalIgnoreCase) &&
                            !key.Equals("ISOPaths", StringComparison.OrdinalIgnoreCase) &&
                            Directory.Exists(value))
                        {
                            try { value = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
                            catch { }

                            if (!roots.Exists(delegate(string r)
                                { return string.Equals(r, value, StringComparison.OrdinalIgnoreCase); }))
                                roots.Add(value);
                        }
                    }
                }
            }
            catch { }

            roots.Sort(StringComparer.OrdinalIgnoreCase);
            return roots;
        }

        private static string GetLibraryRootsSignature(DolphinPaths paths)
        {
            List<string> roots = GetDolphinGameRoots(paths);
            string joined = string.Join("\n", roots.ToArray());
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(joined));
        }

        internal static List<string> LoadCachedDolphinGames(DolphinPaths paths)
        {
            try
            {
                if (!File.Exists(LibraryCacheFile))
                    return null;

                string[] lines = File.ReadAllLines(LibraryCacheFile, Encoding.UTF8);
                if (lines.Length < 2 ||
                    !lines[0].Equals("# Dolphin NetPlay Launcher library cache v1", StringComparison.Ordinal))
                    return null;

                const string prefix = "ROOTS=";
                if (!lines[1].StartsWith(prefix, StringComparison.Ordinal))
                    return null;

                string cachedSignature = lines[1].Substring(prefix.Length);
                string currentSignature = GetLibraryRootsSignature(paths);
                if (!string.Equals(cachedSignature, currentSignature, StringComparison.Ordinal))
                    return null;

                List<string> games = new List<string>();
                for (int i = 2; i < lines.Length; i++)
                {
                    string path = lines[i];
                    if (!string.IsNullOrWhiteSpace(path))
                        games.Add(path);
                }
                return games;
            }
            catch
            {
                return null;
            }
        }

        internal static void SaveCachedDolphinGames(DolphinPaths paths, List<string> games)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Dolphin NetPlay Launcher library cache v1");
                lines.Add("ROOTS=" + GetLibraryRootsSignature(paths));

                if (games != null)
                    lines.AddRange(games);

                string temp = LibraryCacheFile + ".tmp";
                File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
                File.Copy(temp, LibraryCacheFile, true);
                File.Delete(temp);
            }
            catch
            {
                // Cache is an optimization only. Never break the library if it cannot save.
            }
        }

        private static string CacheEncode(string value)
        {
            if (value == null) value = "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string CacheDecode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? "")); }
            catch { return ""; }
        }

        internal static Dictionary<string, LibraryMetadataRecord> LoadLibraryMetadataCache()
        {
            Dictionary<string, LibraryMetadataRecord> result =
                new Dictionary<string, LibraryMetadataRecord>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(LibraryMetadataCacheFile))
                    return result;

                string[] lines = File.ReadAllLines(LibraryMetadataCacheFile, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length != 6)
                        continue;

                    string path = CacheDecode(parts[0]);
                    long ticks, length;
                    int revision;
                    if (string.IsNullOrWhiteSpace(path) ||
                        !long.TryParse(parts[1], out ticks) ||
                        !long.TryParse(parts[2], out length) ||
                        !int.TryParse(parts[5], out revision))
                        continue;

                    result[path] = new LibraryMetadataRecord
                    {
                        WriteTicks = ticks,
                        Length = length,
                        Title = CacheDecode(parts[3]),
                        GameId = CacheDecode(parts[4]),
                        Revision = revision
                    };
                }
            }
            catch { }

            return result;
        }

        internal static void SaveLibraryMetadataCache(
            Dictionary<string, LibraryMetadataRecord> records)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Dolphin NetPlay Launcher metadata cache v1");

                if (records != null)
                {
                    foreach (KeyValuePair<string, LibraryMetadataRecord> pair in records)
                    {
                        LibraryMetadataRecord r = pair.Value;
                        if (r == null) continue;

                        lines.Add(
                            CacheEncode(pair.Key) + "\t" +
                            r.WriteTicks.ToString() + "\t" +
                            r.Length.ToString() + "\t" +
                            CacheEncode(r.Title) + "\t" +
                            CacheEncode(r.GameId) + "\t" +
                            r.Revision.ToString());
                    }
                }

                string temp = LibraryMetadataCacheFile + ".tmp";
                File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
                File.Copy(temp, LibraryMetadataCacheFile, true);
                File.Delete(temp);
            }
            catch
            {
                // Metadata cache is a performance optimization only.
            }
        }

        internal static List<string> DiscoverDolphinGames(DolphinPaths paths)
        {
            List<string> results = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> roots = GetDolphinGameRoots(paths);

            string[] exts = { ".iso", ".gcm", ".gcz", ".rvz", ".wia", ".wbfs", ".ciso", ".dol", ".elf" };
            HashSet<string> supportedExts = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        if (supportedExts.Contains(Path.GetExtension(file)) && seen.Add(file))
                            results.Add(file);
                    }
                }
                catch { }
            }

            results.Sort(delegate(string a, string b)
            {
                return string.Compare(Path.GetFileNameWithoutExtension(a),
                                      Path.GetFileNameWithoutExtension(b),
                                      StringComparison.CurrentCultureIgnoreCase);
            });
            return results;
        }

        private static string BrowseForDolphin()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Locate Dolphin.exe";
                dlg.Filter = "Dolphin Emulator (Dolphin.exe)|Dolphin.exe";
                dlg.CheckFileExists = true;
                dlg.Multiselect = false;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
            }
        }

        private static DnlSettings LoadDnlSettings()
        {
            DnlSettings settings = new DnlSettings();
            bool firstRunKeySeen = false;
            bool librarySetupKeySeen = false;
            bool themeStyleKeySeen = false;
            bool migratedLegacyConfig = false;

            try
            {
                // One-time migration from older versions that stored launcher settings
                // in AppData. Preserve those settings, but treat the copied file as a
                // fresh onboarding state: otherwise deleting/re-extracting the portable
                // launcher can silently resurrect an old "setup complete" state.
                if (!File.Exists(ConfigFile) && File.Exists(LegacyConfigFile))
                {
                    try
                    {
                        File.Copy(LegacyConfigFile, ConfigFile, false);
                        migratedLegacyConfig = true;
                    }
                    catch { }
                }

                if (!File.Exists(ConfigFile))
                    return settings;

                foreach (string raw in File.ReadAllLines(ConfigFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key.Equals("DolphinExe", StringComparison.OrdinalIgnoreCase))
                        settings.DolphinExe = value;
                    else if (key.Equals("AutoCloseDolphin", StringComparison.OrdinalIgnoreCase))
                        settings.AutoCloseDolphin = ParseBool(value, true);
                    else if (key.Equals("NetPlayCloseGraceMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int ms;
                        if (int.TryParse(value, out ms))
                            settings.NetPlayCloseGraceMs = Clamp(ms, 500, 10000);
                    }
                    else if (key.Equals("UpdateCloseGraceMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int ms;
                        if (int.TryParse(value, out ms))
                            settings.UpdateCloseGraceMs = Clamp(ms, 500, 5000);
                    }
                    else if (key.Equals("ShowAutomationWarning", StringComparison.OrdinalIgnoreCase))
                        settings.ShowAutomationWarning = ParseBool(value, true);
                    else if (key.Equals("AutomationIntroSeen", StringComparison.OrdinalIgnoreCase))
                        settings.AutomationIntroSeen = ParseBool(value, false);
                    else if (key.Equals("AutoReturnAfterFailedJoin", StringComparison.OrdinalIgnoreCase))
                        settings.AutoReturnAfterFailedJoin = ParseBool(value, false);
                    else if (key.Equals("ReturnToLauncherAfterDolphinClose", StringComparison.OrdinalIgnoreCase))
                        settings.ReturnToLauncherAfterDolphinClose = ParseBool(value, false);
                    else if (key.Equals("RememberLastMode", StringComparison.OrdinalIgnoreCase))
                        settings.RememberLastMode = ParseBool(value, false);
                    else if (key.Equals("LastMode", StringComparison.OrdinalIgnoreCase))
                        settings.LastMode = value.Equals("Join", StringComparison.OrdinalIgnoreCase) ? "Join" : "Host";
                    else if (key.Equals("LastNickname", StringComparison.OrdinalIgnoreCase))
                        settings.LastNickname = string.IsNullOrWhiteSpace(value) ? "Player" : RemoveUnsafeIniCharacters(value);
                    else if (key.Equals("LastTraversalCode", StringComparison.OrdinalIgnoreCase))
                        settings.LastTraversalCode = RemoveUnsafeIniCharacters(value);
                    else if (key.Equals("LastDirectIp", StringComparison.OrdinalIgnoreCase))
                        settings.LastDirectIp = RemoveUnsafeIniCharacters(value);
                    else if (key.Equals("LastDirectPort", StringComparison.OrdinalIgnoreCase))
                    {
                        int port; if (int.TryParse(value, out port)) settings.LastDirectPort = Clamp(port, 1, 65535);
                    }
                    else if (key.Equals("LastJoinConnection", StringComparison.OrdinalIgnoreCase))
                        settings.LastJoinConnection = value.Equals("Direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Traversal";
                    else if (key.Equals("ControllerNavigation", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerNavigation = ParseBool(value, true);
                    else if (key.Equals("ControllerUseLeftStick", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerUseLeftStick = ParseBool(value, true);
                    else if (key.Equals("ControllerPollingMode", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerPollingMode = ControllerNavigation.NormalizePollingMode(value);
                    else if (key.Equals("ControllerPreference", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerPreference = string.IsNullOrWhiteSpace(value) ? "Auto" : value;
                    else if (key.Equals("ControllerHighlightMatchMonitor", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerHighlightMatchMonitor = ParseBool(value, true);
                    else if (key.Equals("ControllerHighlightHz", StringComparison.OrdinalIgnoreCase))
                    {
                        int hz;
                        if (int.TryParse(value, out hz))
                            settings.ControllerHighlightHz = Clamp(hz, 30, 360);
                    }
                    else if (key.Equals("LastGameFolder", StringComparison.OrdinalIgnoreCase))
                        settings.LastGameFolder = value;
                    else if (key.Equals("OpenLibraryOnStandalone", StringComparison.OrdinalIgnoreCase))
                        settings.OpenLibraryOnStandalone = ParseBool(value, false);
                    else if (key.Equals("OpenLibraryOnSteam", StringComparison.OrdinalIgnoreCase))
                        settings.OpenLibraryOnSteam = ParseBool(value, false);
                    else if (key.Equals("ShowControllerPrompts", StringComparison.OrdinalIgnoreCase))
                        settings.ShowControllerPrompts = ParseBool(value, true);
                    else if (key.Equals("ControllerPromptStyle", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerPromptStyle = NormalizePromptStyle(value);
                    else if (key.Equals("ControllerGamesButton", StringComparison.OrdinalIgnoreCase))
                        settings.ControllerGamesButton = NormalizeGamesButton(value);
                    else if (key.Equals("LibraryView", StringComparison.OrdinalIgnoreCase))
                        settings.LibraryView = value.Equals("List", StringComparison.OrdinalIgnoreCase) ? "List" : "Grid";
                    else if (key.Equals("LibraryGridColumns", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(value, out n)) settings.LibraryGridColumns = Clamp(n, 3, 5);
                    }
                    else if (key.Equals("Appearance", StringComparison.OrdinalIgnoreCase))
                        settings.Appearance = NormalizeAppearance(value);
                    else if (key.Equals("ThemeStyle", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ThemeStyle = NormalizeThemeStyle(value);
                        themeStyleKeySeen = true;
                    }
                    else if (key.Equals("AnimatedThemeBackground", StringComparison.OrdinalIgnoreCase))
                        settings.AnimatedThemeBackground = ParseBool(value, true);
                    else if (key.Equals("InterfaceStyle", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.InterfaceStyle = NormalizeInterfaceStyle(value);
                        // Migration from 0.10.7n-nc: ClassicAdventure bundled the Outfit font
                        // and Adventure Blue palette into one setting. Preserve that look once,
                        // then save the two choices independently.
                        if (!themeStyleKeySeen && string.Equals(value, "ClassicAdventure", StringComparison.OrdinalIgnoreCase))
                            settings.ThemeStyle = "AdventureBlue";
                    }
                    else if (key.Equals("InterfaceFont", StringComparison.OrdinalIgnoreCase))
                    {
                        // 0.10.7l/m experimental font choices were retired. Do not carry
                        // Fami/GameCube selections forward into the new interface-style system.
                        settings.InterfaceStyle = "Default";
                    }
                    else if (key.Equals("AccentStyle", StringComparison.OrdinalIgnoreCase))
                        settings.AccentStyle = value.Equals("AnimatedGradient", StringComparison.OrdinalIgnoreCase)
                            ? "AnimatedGradient" : "SystemAccent";
                    else if (key.Equals("InterfaceSounds", StringComparison.OrdinalIgnoreCase))
                        settings.InterfaceSounds = ParseBool(value, false);
                    else if (key.Equals("SoundStyle", StringComparison.OrdinalIgnoreCase))
                        settings.SoundStyle = UiSoundManager.NormalizeStyleName(value);
                    else if (key.Equals("FirstRunComplete", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.FirstRunComplete = ParseBool(value, false);
                        firstRunKeySeen = true;
                    }
                    else if (key.Equals("LibrarySetupAcknowledged", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.LibrarySetupAcknowledged = ParseBool(value, false);
                        librarySetupKeySeen = true;
                    }
                    else if (key.Equals("CachedDolphinVersion", StringComparison.OrdinalIgnoreCase))
                        settings.CachedDolphinVersion = value;
                    else if (key.Equals("CachedDolphinVersionPath", StringComparison.OrdinalIgnoreCase))
                        settings.CachedDolphinVersionPath = value;
                    else if (key.Equals("CachedDolphinVersionWriteTicks", StringComparison.OrdinalIgnoreCase))
                    {
                        long n; if (long.TryParse(value, out n)) settings.CachedDolphinVersionWriteTicks = n;
                    }
                    else if (key.Equals("CachedDolphinVersionLength", StringComparison.OrdinalIgnoreCase))
                    {
                        long n; if (long.TryParse(value, out n)) settings.CachedDolphinVersionLength = n;
                    }
                    else if (key.Equals("WindowWidth", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(value, out n)) settings.WindowWidth = Math.Max(560, n);
                    }
                    else if (key.Equals("WindowHeight", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(value, out n)) settings.WindowHeight = Math.Max(500, n);
                    }
                }
            }
            catch { }

            // Existing pre-0.10.3c users already chose a Dolphin installation.
            // Do not force the new-user walkthrough on them just because their older
            // config.ini naturally lacks the FirstRunComplete key.
            if (!firstRunKeySeen &&
                !string.IsNullOrWhiteSpace(settings.DolphinExe) &&
                File.Exists(settings.DolphinExe))
            {
                settings.FirstRunComplete = true;
            }

            // Only configs from before 0.10.3c lack BOTH onboarding keys. Treat those
            // users as already-established so an upgrade does not unexpectedly nag them.
            // A config produced by 0.10.3c has FirstRunComplete, so it will correctly
            // receive the new library reminder added in 0.10.3ca.
            if (!librarySetupKeySeen && !firstRunKeySeen &&
                !string.IsNullOrWhiteSpace(settings.DolphinExe) &&
                File.Exists(settings.DolphinExe))
            {
                settings.LibrarySetupAcknowledged = true;
            }

            if (migratedLegacyConfig)
            {
                settings.FirstRunComplete = false;
                settings.LibrarySetupAcknowledged = false;
            }

            return settings;
        }

        private static void SaveDnlSettings(DnlSettings settings)
        {
            SaveDnlSettingsToFile(settings, ConfigFile, true, true);
        }

        internal static bool SaveDnlSettingsToFile(DnlSettings settings, string filePath, bool showErrors)
        {
            // Exported settings deliberately omit remembered nickname/room/IP history.
            return SaveDnlSettingsToFile(settings, filePath, showErrors, false);
        }

        private static bool SaveDnlSettingsToFile(DnlSettings settings, string filePath, bool showErrors, bool includeConnectionHistory)
        {
            if (settings == null || string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                List<string> lines = new List<string>();
                lines.Add("# Dolphin NetPlay Launcher settings");
                lines.Add("DolphinExe=" + (settings.DolphinExe ?? ""));
                lines.Add("AutoCloseDolphin=" + settings.AutoCloseDolphin.ToString());
                lines.Add("NetPlayCloseGraceMs=" + Clamp(settings.NetPlayCloseGraceMs, 500, 10000).ToString());
                lines.Add("UpdateCloseGraceMs=" + Clamp(settings.UpdateCloseGraceMs, 500, 5000).ToString());
                lines.Add("ShowAutomationWarning=" + settings.ShowAutomationWarning.ToString());
                lines.Add("AutomationIntroSeen=" + settings.AutomationIntroSeen.ToString());
                lines.Add("AutoReturnAfterFailedJoin=" + settings.AutoReturnAfterFailedJoin.ToString());
                lines.Add("ReturnToLauncherAfterDolphinClose=" + settings.ReturnToLauncherAfterDolphinClose.ToString());
                lines.Add("RememberLastMode=" + settings.RememberLastMode.ToString());
                lines.Add("LastMode=" + (settings.LastMode == "Join" ? "Join" : "Host"));
                if (includeConnectionHistory)
                {
                    lines.Add("LastNickname=" + RemoveUnsafeIniCharacters(string.IsNullOrWhiteSpace(settings.LastNickname) ? "Player" : settings.LastNickname));
                    lines.Add("LastTraversalCode=" + RemoveUnsafeIniCharacters(settings.LastTraversalCode ?? ""));
                    lines.Add("LastDirectIp=" + RemoveUnsafeIniCharacters(settings.LastDirectIp ?? ""));
                    lines.Add("LastDirectPort=" + Clamp(settings.LastDirectPort, 1, 65535).ToString());
                    lines.Add("LastJoinConnection=" + (string.Equals(settings.LastJoinConnection, "Direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Traversal"));
                }
                lines.Add("ControllerNavigation=" + settings.ControllerNavigation.ToString());
                lines.Add("ControllerUseLeftStick=" + settings.ControllerUseLeftStick.ToString());
                lines.Add("ControllerPollingMode=" + ControllerNavigation.NormalizePollingMode(settings.ControllerPollingMode));
                lines.Add("ControllerPreference=" + (settings.ControllerPreference ?? "Auto"));
                lines.Add("ControllerHighlightMatchMonitor=" + settings.ControllerHighlightMatchMonitor.ToString());
                lines.Add("ControllerHighlightHz=" + Clamp(settings.ControllerHighlightHz, 30, 360).ToString());
                lines.Add("LastGameFolder=" + (settings.LastGameFolder ?? ""));
                lines.Add("OpenLibraryOnStandalone=" + settings.OpenLibraryOnStandalone.ToString());
                lines.Add("OpenLibraryOnSteam=" + settings.OpenLibraryOnSteam.ToString());
                lines.Add("ShowControllerPrompts=" + settings.ShowControllerPrompts.ToString());
                lines.Add("ControllerPromptStyle=" + (settings.ControllerPromptStyle ?? "Xbox"));
                lines.Add("ControllerGamesButton=" + (settings.ControllerGamesButton ?? "North"));
                lines.Add("LibraryView=" + (settings.LibraryView == "List" ? "List" : "Grid"));
                lines.Add("LibraryGridColumns=" + Clamp(settings.LibraryGridColumns, 3, 5).ToString());
                lines.Add("Appearance=" + NormalizeAppearance(settings.Appearance));
                lines.Add("ThemeStyle=" + NormalizeThemeStyle(settings.ThemeStyle));
                lines.Add("AnimatedThemeBackground=" + settings.AnimatedThemeBackground.ToString());
                lines.Add("InterfaceStyle=" + NormalizeInterfaceStyle(settings.InterfaceStyle));
                lines.Add("AccentStyle=" + (settings.AccentStyle == "AnimatedGradient"
                    ? "AnimatedGradient" : "SystemAccent"));
                lines.Add("InterfaceSounds=" + settings.InterfaceSounds.ToString());
                lines.Add("SoundStyle=" + UiSoundManager.NormalizeStyleName(settings.SoundStyle));
                lines.Add("FirstRunComplete=" + settings.FirstRunComplete.ToString());
                lines.Add("LibrarySetupAcknowledged=" + settings.LibrarySetupAcknowledged.ToString());
                lines.Add("CachedDolphinVersion=" + (settings.CachedDolphinVersion ?? ""));
                lines.Add("CachedDolphinVersionPath=" + (settings.CachedDolphinVersionPath ?? ""));
                lines.Add("CachedDolphinVersionWriteTicks=" + settings.CachedDolphinVersionWriteTicks.ToString());
                lines.Add("CachedDolphinVersionLength=" + settings.CachedDolphinVersionLength.ToString());
                lines.Add("WindowWidth=" + settings.WindowWidth.ToString());
                lines.Add("WindowHeight=" + settings.WindowHeight.ToString());

                File.WriteAllLines(filePath, lines.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                if (showErrors)
                Error("Dolphin NetPlay Launcher could not save settings to that location.\n\n" +
                      "Choose a writable folder.");
                return false;
            }
            catch (Exception ex)
            {
                if (showErrors)
                    Error("Dolphin NetPlay Launcher could not save settings.\n\n" + ex.Message);
                return false;
            }
        }

        private static string NormalizeThemeStyle(string value)
        {
            if (string.Equals(value, "AdventureBlue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Adventure Blue", StringComparison.OrdinalIgnoreCase))
                return "AdventureBlue";
            if (string.Equals(value, "OledBlack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "OLED Black", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "OLED", StringComparison.OrdinalIgnoreCase))
                return "OledBlack";
            if (string.Equals(value, "GameCubeIndigo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "GameCube Indigo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Indigo", StringComparison.OrdinalIgnoreCase))
                return "GameCubeIndigo";
            if (string.Equals(value, "GameCubeSpice", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "GameCube Spice Orange", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Spice Orange", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Spice", StringComparison.OrdinalIgnoreCase))
                return "GameCubeSpice";
            return "Default";
        }

        private static string NormalizeInterfaceStyle(string value)
        {
            if (string.Equals(value, "Outfit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "ClassicAdventure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Classic Adventure", StringComparison.OrdinalIgnoreCase))
                return "Outfit";
            return "Default";
        }

        private static string NormalizePromptStyle(string value)
        {
            if (string.Equals(value, "PlayStation", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
            if (string.Equals(value, "Switch", StringComparison.OrdinalIgnoreCase)) return "Switch";
            return "Xbox";
        }

        private static string NormalizeGamesButton(string value)
        {
            if (string.Equals(value, "West", StringComparison.OrdinalIgnoreCase)) return "West";
            return "North";
        }

        private static string NormalizeAppearance(string value)
        {
            if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
            return "System";
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            if (bool.TryParse(value, out parsed))
                return parsed;

            if (value == "1") return true;
            if (value == "0") return false;
            return fallback;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static DolphinPaths BuildPaths(string dolphinExe)
        {
            string dir = Path.GetDirectoryName(dolphinExe);

            // Match Dolphin's Windows user-folder priority as closely as practical:
            // portable.txt or LocalUserConfig=1 => <Dolphin>\User
            // UserConfigPath => explicit custom folder
            // otherwise => Documents\Dolphin Emulator
            bool localUserConfig = false;
            string registryUserPath = null;

            string[] registryKeys =
            {
                @"Software\Dolphin Emulator",
                @"Dolphin Emulator"
            };

            foreach (string keyPath in registryKeys)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
                    {
                        if (key == null)
                            continue;

                        object local = key.GetValue("LocalUserConfig");
                        if (local != null)
                        {
                            int localValue;
                            if (int.TryParse(local.ToString(), out localValue) && localValue == 1)
                                localUserConfig = true;
                        }

                        object custom = key.GetValue("UserConfigPath");
                        if (custom != null && !string.IsNullOrWhiteSpace(custom.ToString()))
                            registryUserPath = Environment.ExpandEnvironmentVariables(custom.ToString());
                    }
                }
                catch { }
            }

            bool portable = File.Exists(Path.Combine(dir, "portable.txt")) || localUserConfig;

            string userDir;
            if (portable)
                userDir = Path.Combine(dir, "User");
            else if (!string.IsNullOrWhiteSpace(registryUserPath))
                userDir = registryUserPath;
            else
                userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Dolphin Emulator");

            return new DolphinPaths
            {
                DolphinExe = dolphinExe,
                Dir = dir,
                DolphinTool = Path.Combine(dir, "DolphinTool.exe"),
                UserDir = userDir,
                DolphinIni = Path.Combine(userDir, "Config", "Dolphin.ini"),
                QtIni = Path.Combine(userDir, "Config", "Qt.ini"),
                GameListCache = Path.Combine(userDir, "Cache", "gamelist.cache"),
                BuiltInTitleDb = Path.Combine(dir, "Sys", "wiitdb-en.txt"),
                UserLoadDir = Path.Combine(userDir, "Load"),
                UserTitleDb = Path.Combine(userDir, "Load", "wiitdb.txt"),
                UserTitles = Path.Combine(userDir, "Load", "titles.txt"),
                GameCoversDir = Path.Combine(userDir, "Cache", "GameCovers")
            };
        }

        private static bool ValidateDolphin(DolphinPaths p)
        {
            string[] required = { p.DolphinExe, p.DolphinTool };
            foreach (string path in required)
            {
                if (!File.Exists(path))
                {
                    Error("Required Dolphin file not found:\n" + path +
                          "\n\nUse Change Dolphin to select the correct Dolphin.exe.");
                    return false;
                }
            }

            // Do not create Dolphin's config ourselves. If the selected install has
            // never initialized its user folder, let Dolphin do that.
            if (!File.Exists(p.DolphinIni) || !File.Exists(p.QtIni))
            {
                DialogResult result = MessageBox.Show(
                    "Dolphin's configuration files were not found in:\n\n" +
                    p.UserDir +
                    "\n\nThe launcher found Dolphin.exe, but this Dolphin user folder does not appear initialized." +
                    "\n\nOpen Dolphin now so it can initialize its own configuration?",
                    "Dolphin setup required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    try { Process.Start(p.DolphinExe); }
                    catch (Exception ex)
                    {
                        Error("Could not start Dolphin.\n\n" + ex.Message);
                    }
                }

                return false;
            }

            return true;
        }

        internal static bool ContainsUnsafeIniCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (char c in value)
                if (char.IsControl(c))
                    return true;

            return false;
        }

        internal static string RemoveUnsafeIniCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? "";

            StringBuilder safe = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c))
                {
                    if (safe == null)
                    {
                        safe = new StringBuilder(value.Length);
                        safe.Append(value, 0, i);
                    }
                    continue;
                }
                if (safe != null) safe.Append(c);
            }
            return safe == null ? value : safe.ToString();
        }

        private static bool RejectUnsafeIniValue(string label, string value)
        {
            if (!ContainsUnsafeIniCharacters(value))
                return false;

            Error(label + " contains an unsupported control character. Remove it and try again.");
            DiagnosticsLog.Write("NETPLAY", "Rejected unsafe control character in " + label + "; value redacted.");
            return true;
        }

        internal static string ReadIni(string path, string section, string key, string fallback)
        {
            if (!File.Exists(path))
                return fallback;

            bool inside = false;
            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();

                Match sectionMatch = Regex.Match(trimmed, @"^\[(.+)\]$");
                if (sectionMatch.Success)
                {
                    inside = string.Equals(sectionMatch.Groups[1].Value, section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inside)
                    continue;

                Match kv = Regex.Match(line, @"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*)$",
                    RegexOptions.IgnoreCase);
                if (kv.Success)
                    return kv.Groups[1].Value.Trim();
            }

            return fallback;
        }

        private static void WriteIniValues(string path, string section, Dictionary<string, string> values)
        {
            List<string> lines = new List<string>(File.ReadAllLines(path));
            int sectionStart = FindSection(lines, section);
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1] != "") lines.Add("");
                lines.Add("[" + section + "]");
                sectionStart = lines.Count - 1;
            }
            int sectionEnd = FindSectionEnd(lines, sectionStart);

            foreach (KeyValuePair<string, string> pair in values)
            {
                if (ContainsUnsafeIniCharacters(pair.Value))
                    throw new InvalidDataException("Refusing to write a control character to INI key " + pair.Key + ".");

                int idx = FindKey(lines, sectionStart, sectionEnd, pair.Key);
                string nl = pair.Key + " = " + pair.Value;
                if (idx >= 0) lines[idx] = nl;
                else
                {
                    lines.Insert(sectionStart + 1, nl);
                    sectionEnd++;
                }
            }
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        private static int FindSection(List<string> lines, string section)
        {
            string wanted = "[" + section + "]";
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int FindSectionEnd(List<string> lines, int sectionStart)
        {
            for (int i = sectionStart + 1; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i].Trim(), "^\\[.+\\]$")) return i;
            return lines.Count;
        }

        private static int FindKey(List<string> lines, int sectionStart, int sectionEnd, string key)
        {
            for (int i = sectionStart + 1; i < sectionEnd; i++)
                if (Regex.IsMatch(lines[i], "^\\s*" + Regex.Escape(key) + "\\s*=", RegexOptions.IgnoreCase)) return i;
            return -1;
        }

        private static IntPtr FindVisibleWindowTitleContainsForProcess(
            string text, int processId, IntPtr excludeWindow)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == excludeWindow || !IsWindowVisible(hWnd))
                    return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != (uint)processId)
                    return true;

                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (!string.IsNullOrWhiteSpace(title) &&
                    title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindWindowByExactTitleForProcess(string title, int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != (uint)processId) return true;

                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString() == title)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static void ClickAt(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(100);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static void Error(string text)
        {
            DiagnosticsLog.Write("ERROR", text);
            HideAutomationWarnings();
            MessageBox.Show(
                text,
                "Dolphin NetPlay Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
    }


    internal sealed class UpdaterControllerPromptForm : Form
    {
        private readonly bool promptsEnabled;
        private readonly string promptStyle;
        private readonly bool netPlayLobbyMode;
        private readonly bool netPlayJoinOnlyMode;
        private readonly bool confirmOnlyMode;
        private readonly bool recoveryDialogMode;
        private bool pointerOnRight = true;

        // This helper exists only while one of the short controller-hint bubbles is visible.
        // 50 ms gives a smooth 20 FPS border/icon animation without normal-session overhead.
        private readonly System.Windows.Forms.Timer borderAnimationTimer =
            new System.Windows.Forms.Timer();
        private readonly Stopwatch borderAnimationClock = new Stopwatch();

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct LayerPoint
        {
            public int X;
            public int Y;

            public LayerPoint(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LayerSize
        {
            public int CX;
            public int CY;

            public LayerSize(int cx, int cy)
            {
                CX = cx;
                CY = cy;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref LayerPoint pptDst, ref LayerSize psize,
            IntPtr hdcSrc, ref LayerPoint pptSrc, int crKey,
            ref BlendFunction pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        public UpdaterControllerPromptForm(bool enabled, string style)
            : this(enabled, style, false, false, false, false)
        {
        }

        public UpdaterControllerPromptForm(bool enabled, string style, bool useNetPlayLobbyLayout)
            : this(enabled, style, useNetPlayLobbyLayout, false, false, false)
        {
        }

        public UpdaterControllerPromptForm(bool enabled, string style, bool useNetPlayLobbyLayout, bool netPlayJoinOnly)
            : this(enabled, style, useNetPlayLobbyLayout, netPlayJoinOnly, false, false)
        {
        }

        public UpdaterControllerPromptForm(bool enabled, string style, bool useNetPlayLobbyLayout, bool netPlayJoinOnly, bool confirmOnly)
            : this(enabled, style, useNetPlayLobbyLayout, netPlayJoinOnly, confirmOnly, false)
        {
        }

        public UpdaterControllerPromptForm(bool enabled, string style, bool useNetPlayLobbyLayout, bool netPlayJoinOnly, bool confirmOnly, bool recoveryDialog)
        {
            promptsEnabled = enabled;
            promptStyle = string.IsNullOrWhiteSpace(style) ? "Xbox" : style;
            netPlayLobbyMode = useNetPlayLobbyLayout;
            netPlayJoinOnlyMode = netPlayJoinOnly;
            confirmOnlyMode = confirmOnly;
            recoveryDialogMode = recoveryDialog;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = recoveryDialogMode
                ? new Size(286, 82)
                : (netPlayLobbyMode
                    ? (netPlayJoinOnlyMode ? new Size(180, 62) : new Size(300, 104))
                    : (confirmOnlyMode ? new Size(246, 62) : new Size(246, 82)));
            TabStop = false;

            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            // Faster than 0.10.4n's 12.5 FPS because the highlight now visibly travels.
            // The actual hue cycle is still slow (~7.5 seconds).
            borderAnimationTimer.Interval = 50;
            borderAnimationTimer.Tick += delegate
            {
                if (Visible)
                    RenderLayered();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Layered window = true per-pixel alpha, so rounded corners and the speech
                // pointer can be genuinely anti-aliased instead of using a hard color key.
                // NOACTIVATE keeps Dolphin focused; TRANSPARENT makes this hint click-through.
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW |
                              WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // A layered window is painted into an ARGB bitmap in RenderLayered().
            // Do not let normal WinForms painting replace the per-pixel-alpha surface.
        }

        private void RenderLayered()
        {
            if (!IsHandleCreated || IsDisposed || !Visible)
                return;

            using (Bitmap bitmap =
                new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                DrawBubble(g);
                ApplyLayeredBitmap(bitmap);
            }
        }

        private void DrawBubble(Graphics g)
        {
            const int pointer = 13;
            Rectangle bubble = pointerOnRight
                ? new Rectangle(3, 3, Width - pointer - 7, Height - 7)
                : new Rectangle(pointer + 4, 3, Width - pointer - 7, Height - 7);

            int cy = bubble.Top + bubble.Height / 2;

            using (GraphicsPath shape = CreateSpeechBubblePath(bubble, pointerOnRight, cy))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(252, 252, 254)))
            {
                g.FillPath(fill, shape);

                // A soft low-alpha halo makes the moving color easier to perceive without
                // turning the prompt into a neon sign.
                using (LinearGradientBrush glowBrush =
                    CreateAnimatedBorderBrush(new Rectangle(0, 0, Width, Height), 70))
                using (Pen glow = new Pen(glowBrush, 6.0F))
                {
                    glow.LineJoin = LineJoin.Round;
                    g.DrawPath(glow, shape);
                }

                using (LinearGradientBrush borderBrush =
                    CreateAnimatedBorderBrush(new Rectangle(0, 0, Width, Height), 255))
                using (Pen border = new Pen(borderBrush, 3.0F))
                {
                    border.LineJoin = LineJoin.Round;
                    g.DrawPath(border, shape);
                }
            }

            int left = bubble.Left + 13;
            using (Font header = new Font("Segoe UI", 8.5F, FontStyle.Bold))
            using (SolidBrush hb = new SolidBrush(CurrentAccentColor()))
                g.DrawString("CONTROLLER", header, hb, left, bubble.Top + 8);

            if (recoveryDialogMode)
            {
                DrawNavigationPrompt(g, left, bubble.Top + 28, false, "Navigate", true);
                int y = bubble.Top + 52;
                int x = left;
                x = DrawFacePrompt(g, x, y, "South", "Select");
                x += 9;
                DrawFacePrompt(g, x, y, "East", "Back");
            }
            else if (netPlayLobbyMode)
            {
                if (netPlayJoinOnlyMode)
                {
                    DrawFacePrompt(g, left, bubble.Top + 31, "East", "Quit");
                }
                else
                {
                    DrawNavigationPrompt(g, left, bubble.Top + 27, true, "Start / Buffer");
                    DrawNavigationPrompt(g, left, bubble.Top + 47, false, "Adjust Buffer");

                    int y = bubble.Top + 70;
                    int x = left;
                    x = DrawFacePrompt(g, x, y, "South", "Select");
                    x += 7;
                    x = DrawStartPrompt(g, x, y, "Select");
                    x += 7;
                    DrawFacePrompt(g, x, y, "East", "Quit");
                }
            }
            else
            {
                int y = bubble.Top + 29;
                int x = left;
                x = DrawFacePrompt(g, x, y, "South", confirmOnlyMode ? "OK" : "Confirm");
                x += 10;
                DrawStartPrompt(g, x, y, confirmOnlyMode ? "OK" : "Confirm");

                if (!confirmOnlyMode)
                    DrawFacePrompt(g, left, bubble.Top + 55, "East", "Cancel");
            }
        }

        private GraphicsPath CreateSpeechBubblePath(
            Rectangle bubble, bool pointerRight, int cy)
        {
            float r = 12F;
            float d = r * 2F;
            float tipX = pointerRight ? Width - 2F : 2F;
            float pTop = cy - 9F;
            float pBottom = cy + 9F;

            GraphicsPath p = new GraphicsPath();
            if (pointerRight)
            {
                p.StartFigure();
                p.AddLine(bubble.Left + r, bubble.Top, bubble.Right - r, bubble.Top);
                p.AddArc(bubble.Right - d, bubble.Top, d, d, 270, 90);
                p.AddLine(bubble.Right, bubble.Top + r, bubble.Right, pTop);
                p.AddLine(bubble.Right, pTop, tipX, cy);
                p.AddLine(tipX, cy, bubble.Right, pBottom);
                p.AddLine(bubble.Right, pBottom, bubble.Right, bubble.Bottom - r);
                p.AddArc(bubble.Right - d, bubble.Bottom - d, d, d, 0, 90);
                p.AddLine(bubble.Right - r, bubble.Bottom, bubble.Left + r, bubble.Bottom);
                p.AddArc(bubble.Left, bubble.Bottom - d, d, d, 90, 90);
                p.AddLine(bubble.Left, bubble.Bottom - r, bubble.Left, bubble.Top + r);
                p.AddArc(bubble.Left, bubble.Top, d, d, 180, 90);
                p.CloseFigure();
            }
            else
            {
                p.StartFigure();
                p.AddLine(bubble.Left + r, bubble.Top, bubble.Right - r, bubble.Top);
                p.AddArc(bubble.Right - d, bubble.Top, d, d, 270, 90);
                p.AddLine(bubble.Right, bubble.Top + r, bubble.Right, bubble.Bottom - r);
                p.AddArc(bubble.Right - d, bubble.Bottom - d, d, d, 0, 90);
                p.AddLine(bubble.Right - r, bubble.Bottom, bubble.Left + r, bubble.Bottom);
                p.AddArc(bubble.Left, bubble.Bottom - d, d, d, 90, 90);
                p.AddLine(bubble.Left, bubble.Bottom - r, bubble.Left, pBottom);
                p.AddLine(bubble.Left, pBottom, tipX, cy);
                p.AddLine(tipX, cy, bubble.Left, pTop);
                p.AddLine(bubble.Left, pTop, bubble.Left, bubble.Top + r);
                p.AddArc(bubble.Left, bubble.Top, d, d, 180, 90);
                p.CloseFigure();
            }

            return p;
        }

        private LinearGradientBrush CreateAnimatedBorderBrush(Rectangle rect, int alpha)
        {
            if (rect.Width < 2) rect.Width = 2;
            if (rect.Height < 2) rect.Height = 2;

            double seconds = borderAnimationClock.IsRunning
                ? borderAnimationClock.Elapsed.TotalSeconds
                : 0.0;

            // Noticeably alive, but still calm: one palette cycle every ~7.5 seconds,
            // while the gradient direction itself makes one slow turn every ~12 seconds.
            double phase = (seconds / 7.5) % 1.0;
            float angle = (float)((seconds * 30.0) % 360.0);

            LinearGradientBrush brush = new LinearGradientBrush(
                rect,
                WithAlpha(InterpolatePalette(phase), alpha),
                WithAlpha(InterpolatePalette((phase + 0.66) % 1.0), alpha),
                angle,
                true);

            ColorBlend blend = new ColorBlend();
            blend.Positions = new float[] { 0F, 0.22F, 0.48F, 0.72F, 1F };
            blend.Colors = new Color[]
            {
                WithAlpha(InterpolatePalette(phase), alpha),
                WithAlpha(InterpolatePalette((phase + 0.16) % 1.0), alpha),
                WithAlpha(InterpolatePalette((phase + 0.34) % 1.0), alpha),
                WithAlpha(InterpolatePalette((phase + 0.52) % 1.0), alpha),
                WithAlpha(InterpolatePalette((phase + 0.70) % 1.0), alpha)
            };
            brush.InterpolationColors = blend;
            return brush;
        }

        private Color CurrentAccentColor()
        {
            double seconds = borderAnimationClock.IsRunning
                ? borderAnimationClock.Elapsed.TotalSeconds
                : 0.0;
            return InterpolatePalette((seconds / 7.5) % 1.0);
        }

        private static Color InterpolatePalette(double phase)
        {
            // Brighter than 0.10.4n, but intentionally limited to cool/accent hues.
            Color purple = Color.FromArgb(170, 55, 215);
            Color blue = Color.FromArgb(55, 125, 235);
            Color cyan = Color.FromArgb(35, 185, 220);
            Color teal = Color.FromArgb(35, 175, 145);

            phase = phase - Math.Floor(phase);
            if (phase < 0.25)
                return LerpColor(purple, blue, phase * 4.0);
            if (phase < 0.50)
                return LerpColor(blue, cyan, (phase - 0.25) * 4.0);
            if (phase < 0.75)
                return LerpColor(cyan, teal, (phase - 0.50) * 4.0);
            return LerpColor(teal, purple, (phase - 0.75) * 4.0);
        }

        private static Color WithAlpha(Color c, int alpha)
        {
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, alpha)),
                c.R, c.G, c.B);
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private void ApplyLayeredBitmap(Bitmap bitmap)
        {
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memoryDc = CreateCompatibleDC(screenDc);
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memoryDc, hBitmap);

                LayerPoint destination = new LayerPoint(Left, Top);
                LayerSize size = new LayerSize(bitmap.Width, bitmap.Height);
                LayerPoint source = new LayerPoint(0, 0);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                UpdateLayeredWindow(
                    Handle, screenDc, ref destination, ref size,
                    memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
                    SelectObject(memoryDc, oldBitmap);
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (memoryDc != IntPtr.Zero)
                    DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, screenDc);
            }
        }


        private void DrawNavigationPrompt(Graphics g, int x, int y, bool horizontal, string action)
        {
            DrawNavigationPrompt(g, x, y, horizontal, action, false);
        }

        private void DrawNavigationPrompt(Graphics g, int x, int y, bool horizontal, string action, bool bothAxes)
        {
            Color dark = Color.FromArgb(58, 58, 64);
            Color light = Color.FromArgb(235, 235, 239);
            Color accent = CurrentAccentColor();
            Color pressedAccent = LerpColor(accent, Color.FromArgb(35, 35, 42), 0.22);

            PromptDirection direction = GetPromptDirection(horizontal, bothAxes);
            float travel = GetDirectionalTravel();

            // Compact D-pad icon. Keep the allowed axis/axes highlighted, then gently
            // "press" one valid direction at a time so the hint reads like a tiny demo.
            Rectangle dpadCenter = new Rectangle(x + 7, y + 6, 6, 6);
            Rectangle dpadUp = new Rectangle(x + 7, y, 6, 7);
            Rectangle dpadDown = new Rectangle(x + 7, y + 11, 6, 7);
            Rectangle dpadLeft = new Rectangle(x + 1, y + 6, 7, 6);
            Rectangle dpadRight = new Rectangle(x + 12, y + 6, 7, 6);

            using (SolidBrush baseBrush = new SolidBrush(light))
            using (SolidBrush activeBrush = new SolidBrush(accent))
            using (SolidBrush pressedBrush = new SolidBrush(pressedAccent))
            using (Pen outline = new Pen(Color.FromArgb(105, 105, 112), 1F))
            {
                DrawDpadPart(g, dpadCenter, bothAxes, false, PromptDirection.None, direction, 0F,
                    baseBrush, activeBrush, pressedBrush, outline);
                DrawDpadPart(g, dpadUp, bothAxes || !horizontal, true, PromptDirection.Up, direction, travel,
                    baseBrush, activeBrush, pressedBrush, outline);
                DrawDpadPart(g, dpadDown, bothAxes || !horizontal, true, PromptDirection.Down, direction, travel,
                    baseBrush, activeBrush, pressedBrush, outline);
                DrawDpadPart(g, dpadLeft, bothAxes || horizontal, true, PromptDirection.Left, direction, travel,
                    baseBrush, activeBrush, pressedBrush, outline);
                DrawDpadPart(g, dpadRight, bothAxes || horizontal, true, PromptDirection.Right, direction, travel,
                    baseBrush, activeBrush, pressedBrush, outline);
            }

            // Left-stick icon. The knob rocks along the same valid direction currently
            // demonstrated by the D-pad, while the axis marks continue to show every
            // direction that is actually accepted.
            int stickX = x + 28;
            Rectangle stickOuter = new Rectangle(stickX, y + 1, 17, 17);
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(245, 245, 247)))
            using (Pen sp = new Pen(Color.FromArgb(105, 105, 112), 1.2F))
            {
                g.FillEllipse(sb, stickOuter);
                g.DrawEllipse(sp, stickOuter);
            }
            using (Pen axis = new Pen(accent, 2F))
            {
                if (bothAxes || horizontal)
                    g.DrawLine(axis, stickOuter.Left + 3, stickOuter.Top + 8, stickOuter.Right - 3, stickOuter.Top + 8);
                if (bothAxes || !horizontal)
                    g.DrawLine(axis, stickOuter.Left + 8, stickOuter.Top + 3, stickOuter.Left + 8, stickOuter.Bottom - 3);
            }

            PointF stickOffset = DirectionVector(direction, travel * 3.0F);
            using (SolidBrush knob = new SolidBrush(dark))
                g.FillEllipse(knob, stickOuter.Left + 6 + stickOffset.X,
                    stickOuter.Top + 6 + stickOffset.Y, 5, 5);

            using (Font f = new Font("Segoe UI", 8.5F, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(45, 45, 50)))
                g.DrawString(action, f, b, x + 52, y + 1);
        }

        private enum PromptDirection
        {
            None,
            Up,
            Right,
            Down,
            Left
        }

        private PromptDirection GetPromptDirection(bool horizontal, bool bothAxes)
        {
            double seconds = borderAnimationClock.IsRunning
                ? borderAnimationClock.Elapsed.TotalSeconds
                : 0.0;

            // One demonstrated direction every 0.58 seconds. The movement within each
            // step eases out and back rather than snapping continuously. This is slightly
            // quicker than 0.10.7j while remaining readable at a glance.
            const double directionCycleSeconds = 0.58;
            int step = (int)Math.Floor(seconds / directionCycleSeconds);
            if (bothAxes)
            {
                switch (step % 4)
                {
                    case 0: return PromptDirection.Up;
                    case 1: return PromptDirection.Right;
                    case 2: return PromptDirection.Down;
                    default: return PromptDirection.Left;
                }
            }

            if (horizontal)
                return (step % 2 == 0) ? PromptDirection.Left : PromptDirection.Right;

            return (step % 2 == 0) ? PromptDirection.Up : PromptDirection.Down;
        }

        private float GetDirectionalTravel()
        {
            double seconds = borderAnimationClock.IsRunning
                ? borderAnimationClock.Elapsed.TotalSeconds
                : 0.0;
            const double directionCycleSeconds = 0.58;
            double local = (seconds % directionCycleSeconds) / directionCycleSeconds;
            // Smooth 0 -> 1 -> 0 rock with a small rest at each direction change.
            return (float)(Math.Sin(Math.PI * local) * 0.95);
        }

        private float GetButtonPressTravel()
        {
            double seconds = borderAnimationClock.IsRunning
                ? borderAnimationClock.Elapsed.TotalSeconds
                : 0.0;
            const double buttonCycleSeconds = 1.00;
            const double pressFraction = 0.38;
            double local = (seconds % buttonCycleSeconds) / buttonCycleSeconds;
            if (local > pressFraction)
                return 0F;
            return (float)(Math.Sin(Math.PI * (local / pressFraction)) * 1.7);
        }

        private static PointF DirectionVector(PromptDirection direction, float amount)
        {
            switch (direction)
            {
                case PromptDirection.Up: return new PointF(0F, -amount);
                case PromptDirection.Right: return new PointF(amount, 0F);
                case PromptDirection.Down: return new PointF(0F, amount);
                case PromptDirection.Left: return new PointF(-amount, 0F);
                default: return PointF.Empty;
            }
        }

        private static void DrawDpadPart(
            Graphics g, Rectangle original, bool active, bool canPress,
            PromptDirection partDirection, PromptDirection currentDirection, float travel,
            SolidBrush baseBrush, SolidBrush activeBrush, SolidBrush pressedBrush, Pen outline)
        {
            bool pressed = canPress && active && partDirection == currentDirection && travel > 0.08F;
            RectangleF r = original;
            if (pressed)
            {
                PointF offset = DirectionVector(partDirection, travel);
                r.Offset(offset.X, offset.Y);
            }

            g.FillRectangle(pressed ? pressedBrush : (active ? activeBrush : baseBrush), r);
            g.DrawRectangle(outline, Rectangle.Round(r));
        }

        private int DrawFacePrompt(Graphics g, int x, int y, string face, string action)
        {
            const int size = 19;
            float press = GetButtonPressTravel();
            RectangleF r = new RectangleF(x, y + press, size, size - press * 0.35F);
            Color fill, border, text;
            string glyph = GetFaceGlyph(face, out fill, out border, out text);

            using (SolidBrush b = new SolidBrush(fill))
                g.FillEllipse(b, r);
            using (Pen p = new Pen(border, 1.5F))
                g.DrawEllipse(p, r);

            bool ps = string.Equals(promptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase);
            using (Font f = new Font(ps ? "Segoe UI Symbol" : "Segoe UI",
                                     ps ? 11F : 8F,
                                     ps ? FontStyle.Regular : FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(text))
            {
                SizeF sz = g.MeasureString(glyph, f);
                g.DrawString(glyph, f, b,
                    r.X + (r.Width - sz.Width) / 2F,
                    r.Y + (r.Height - sz.Height) / 2F - (ps ? 2F : 1F));
            }

            using (Font f = new Font("Segoe UI", 8.5F, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(45, 45, 50)))
            {
                g.DrawString(action, f, b, x + size + 5, y + 1);
                return x + size + 5 + (int)Math.Ceiling(g.MeasureString(action, f).Width);
            }
        }

        private int DrawStartPrompt(Graphics g, int x, int y, string action)
        {
            string glyph;
            if (string.Equals(promptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
                glyph = "Options";
            else if (string.Equals(promptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
                glyph = "+";
            else
                glyph = "Start";

            using (Font glyphFont = new Font("Segoe UI",
                                             glyph == "+" ? 10F : 7F,
                                             FontStyle.Bold))
            using (Font actionFont = new Font("Segoe UI", 8.5F, FontStyle.Bold))
            {
                int pad = glyph == "+" ? 9 : 6;
                int w = Math.Max(25,
                    (int)Math.Ceiling(g.MeasureString(glyph, glyphFont).Width) + pad * 2);
                // Keep the pill itself spatially stable. Moving/resizing the entire Start/Options
                // glyph each frame read as a shake rather than a button press. Instead, the fixed
                // outer shell darkens slightly and only its inner face/glyph depresses by 1 px.
                float press = GetButtonPressTravel();
                float press01 = Math.Min(1F, press / 1.7F);
                Rectangle r = new Rectangle(x, y, w, 19);

                using (GraphicsPath p = RoundedRect(r, 6F))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 242, 244)))
                using (Pen pen = new Pen(Color.FromArgb(120, 120, 125)))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }

                if (press01 > 0.04F)
                {
                    Rectangle inner = Rectangle.Inflate(r, -2, -2);
                    inner.Y += 1;
                    inner.Height = Math.Max(1, inner.Height - 1);
                    int shade = 236 - (int)Math.Round(12F * press01);
                    using (GraphicsPath ip = RoundedRect(inner, 4.5F))
                    using (SolidBrush ib = new SolidBrush(Color.FromArgb(shade, shade, shade + 2)))
                        g.FillPath(ib, ip);
                }

                using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 55, 60)))
                {
                    SizeF sz = g.MeasureString(glyph, glyphFont);
                    float glyphPress = press01 > 0.12F ? 1F : 0F;
                    g.DrawString(glyph, glyphFont, b,
                        r.X + (r.Width - sz.Width) / 2F,
                        r.Y + (r.Height - sz.Height) / 2F - 1F + glyphPress);
                }

                using (SolidBrush b = new SolidBrush(Color.FromArgb(45, 45, 50)))
                {
                    g.DrawString(action, actionFont, b, x + r.Width + 5, y + 1);
                    return x + r.Width + 5 +
                        (int)Math.Ceiling(g.MeasureString(action, actionFont).Width);
                }
            }
        }

        private string GetFaceGlyph(string face, out Color fill, out Color border, out Color text)
        {
            bool east = string.Equals(face, "East", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(promptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                fill = Color.White;
                text = Color.FromArgb(35, 35, 42);
                if (east) { border = Color.FromArgb(225, 90, 90); return "○"; }
                border = Color.FromArgb(80, 130, 220); return "×";
            }

            if (string.Equals(promptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
            {
                fill = Color.FromArgb(245, 245, 245);
                border = Color.FromArgb(95, 95, 100);
                text = Color.FromArgb(35, 35, 40);
                return east ? "A" : "B";
            }

            if (east)
            {
                fill = Color.FromArgb(220, 65, 65);
                border = Color.FromArgb(155, 35, 35);
                text = Color.White;
                return "B";
            }

            fill = Color.FromArgb(75, 175, 80);
            border = Color.FromArgb(45, 120, 50);
            text = Color.White;
            return "A";
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            float d = radius * 2F;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void ShowForWindow(IntPtr target)
        {
            if (!promptsEnabled ||
                target == IntPtr.Zero ||
                !Program.IsWindow(target) ||
                !Program.IsWindowVisible(target))
            {
                HidePrompt();
                return;
            }

            Program.RECT rect;
            if (!Program.GetWindowRect(target, out rect))
            {
                HidePrompt();
                return;
            }

            Rectangle screen = Screen.FromHandle(target).WorkingArea;
            int dialogHeight = Math.Max(1, rect.Bottom - rect.Top);

            int desiredY = rect.Top + Math.Max(18, dialogHeight - Height - 28);
            int leftX = rect.Left - Width - 8;
            int rightX = rect.Right + 8;

            if (leftX >= screen.Left)
            {
                pointerOnRight = true;
                Location = new Point(leftX,
                    Math.Max(screen.Top, Math.Min(desiredY, screen.Bottom - Height)));
            }
            else if (rightX + Width <= screen.Right)
            {
                pointerOnRight = false;
                Location = new Point(rightX,
                    Math.Max(screen.Top, Math.Min(desiredY, screen.Bottom - Height)));
            }
            else
            {
                pointerOnRight = true;
                int x = Math.Max(screen.Left,
                    Math.Min(rect.Left, screen.Right - Width));
                int y = Math.Max(screen.Top, rect.Top - Height - 6);
                Location = new Point(x, y);
            }

            if (!borderAnimationClock.IsRunning)
                borderAnimationClock.Start();
            if (!borderAnimationTimer.Enabled)
                borderAnimationTimer.Start();

            if (!Visible)
                Show();

            // Show() creates the native layered HWND; now draw its first ARGB frame.
            RenderLayered();
        }

        public void HidePrompt()
        {
            borderAnimationTimer.Stop();
            borderAnimationClock.Stop();

            if (Visible)
                Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                borderAnimationTimer.Stop();
                borderAnimationTimer.Dispose();
                borderAnimationClock.Stop();
            }
            base.Dispose(disposing);
        }
    }


    internal sealed class NetPlaySession
    {
        public Process DolphinProcess;
        public IntPtr MainWindow;
        public IntPtr LobbyWindow;
        public bool IsJoin;
    }

    internal sealed class NetPlayJoinFailedException : Exception
    {
        public readonly int DolphinProcessId;

        public NetPlayJoinFailedException(int dolphinProcessId)
            : base("Dolphin reported a NetPlay connection failure.")
        {
            DolphinProcessId = dolphinProcessId;
        }
    }

    internal sealed class GameInfo
    {
        public string Title;
        public string GameId;
        public int Revision;
        public string NetPlayName;
    }

    internal sealed class LibraryMetadataRecord
    {
        public long WriteTicks;
        public long Length;
        public string Title;
        public string GameId;
        public int Revision;
    }

    internal static class AccentVisuals
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static bool Animated(DnlSettings settings)
        {
            return settings != null &&
                string.Equals(settings.AccentStyle, "AnimatedGradient",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static Color CurrentColor()
        {
            double phase = (Clock.Elapsed.TotalSeconds / 7.5) % 1.0;
            return Palette(phase);
        }

        public static Color CurrentColor(double offset)
        {
            double phase = ((Clock.Elapsed.TotalSeconds / 7.5) + offset) % 1.0;
            return Palette(phase);
        }

        public static LinearGradientBrush CreateGradient(Rectangle rect, int alpha)
        {
            if (rect.Width < 2) rect.Width = 2;
            if (rect.Height < 2) rect.Height = 2;

            double seconds = Clock.Elapsed.TotalSeconds;
            double phase = (seconds / 7.5) % 1.0;
            float angle = (float)((seconds * 30.0) % 360.0);

            LinearGradientBrush brush = new LinearGradientBrush(
                rect,
                Color.FromArgb(alpha, CurrentColor(0.00)),
                Color.FromArgb(alpha, CurrentColor(0.66)),
                angle,
                true);

            ColorBlend blend = new ColorBlend();
            blend.Positions = new float[] { 0F, 0.25F, 0.50F, 0.75F, 1F };
            blend.Colors = new Color[]
            {
                Color.FromArgb(alpha, CurrentColor(0.00)),
                Color.FromArgb(alpha, CurrentColor(0.16)),
                Color.FromArgb(alpha, CurrentColor(0.34)),
                Color.FromArgb(alpha, CurrentColor(0.52)),
                Color.FromArgb(alpha, CurrentColor(0.70))
            };
            brush.InterpolationColors = blend;
            return brush;
        }

        private static Color Palette(double phase)
        {
            Color purple = Color.FromArgb(170, 55, 215);
            Color blue = Color.FromArgb(55, 125, 235);
            Color cyan = Color.FromArgb(35, 185, 220);
            Color teal = Color.FromArgb(35, 175, 145);

            phase -= Math.Floor(phase);
            if (phase < 0.25) return Lerp(purple, blue, phase * 4.0);
            if (phase < 0.50) return Lerp(blue, cyan, (phase - 0.25) * 4.0);
            if (phase < 0.75) return Lerp(cyan, teal, (phase - 0.50) * 4.0);
            return Lerp(teal, purple, (phase - 0.75) * 4.0);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    internal sealed class DnlSettings
    {
        public string DolphinExe = "";
        public bool AutoCloseDolphin = true;
        public int NetPlayCloseGraceMs = 2000;
        public int UpdateCloseGraceMs = 1000;
        public bool ShowAutomationWarning = true;
        public bool AutomationIntroSeen = false;
        public bool AutoReturnAfterFailedJoin = false;
        public bool ReturnToLauncherAfterDolphinClose = false;
        public bool RememberLastMode = false;
        public string LastMode = "Host";
        // Launcher-owned connection history. Fresh installs intentionally do not import
        // NetPlay targets or identity fields from an existing Dolphin.ini.
        public string LastNickname = "Player";
        public string LastTraversalCode = "";
        public string LastDirectIp = "";
        public int LastDirectPort = 2626;
        public string LastJoinConnection = "Traversal";
        public bool ControllerNavigation = true;
        public bool ControllerUseLeftStick = true;
        // RC38: launcher-only controller input polling can run above the old 16 ms baseline.
        // MatchMonitor is capped at 240 Hz and remains subject to WinForms timer delivery.
        public string ControllerPollingMode = "MatchMonitor";
        public string ControllerPreference = "Auto";
        public bool ControllerHighlightMatchMonitor = true;
        public int ControllerHighlightHz = 60;
        public string LastGameFolder = "";
        public bool OpenLibraryOnStandalone = false;
        public bool OpenLibraryOnSteam = false;
        public bool ShowControllerPrompts = true;
        public string ControllerPromptStyle = "Xbox";
        public string ControllerGamesButton = "North";
        public string LibraryView = "Grid";
        public int LibraryGridColumns = 3;
        public int WindowWidth = 600;
        public int WindowHeight = 575;
        public string Appearance = "Dark";
        public string ThemeStyle = "AdventureBlue";
        public bool AnimatedThemeBackground = true;
        public string InterfaceStyle = "Outfit";
        public string AccentStyle = "AnimatedGradient";
        public bool InterfaceSounds = true;
        public string SoundStyle = "ClassicUI";
        public bool FirstRunComplete = false;
        public bool LibrarySetupAcknowledged = false;

        // Performance cache. These are implementation details, not user-facing options.
        public string CachedDolphinVersion = "";
        public string CachedDolphinVersionPath = "";
        public long CachedDolphinVersionWriteTicks = 0;
        public long CachedDolphinVersionLength = 0;

        public DnlSettings CloneSettings()
        {
            return (DnlSettings)MemberwiseClone();
        }

    }

    internal sealed class DolphinPaths
    {
        public string DolphinExe;
        public string Dir;
        public string DolphinTool;
        public string UserDir;
        public string DolphinIni;
        public string QtIni;
        public string GameListCache;
        public string BuiltInTitleDb;
        public string UserLoadDir;
        public string UserTitleDb;
        public string UserTitles;
        public string GameCoversDir;
    }

    internal enum LaunchMode { Host, Join }
    internal enum JoinConnection { Traversal, Direct }

    internal static class AppFonts
    {
        private static readonly object Sync = new object();
        private static readonly PrivateFontCollection PrivateFonts = new PrivateFontCollection();
        private static readonly Dictionary<string, FontFamily> Families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        internal static string FontsDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts"); }
        }

        internal static string[] Choices
        {
            get { return new string[] { "Default", "Outfit" }; }
        }

        internal static bool UseClassic(DnlSettings settings)
        {
            return settings != null && (string.Equals(settings.InterfaceStyle, "Outfit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(settings.InterfaceStyle, "ClassicAdventure", StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (loaded) return;
                loaded = true;
                LoadFile("Outfit-Regular.ttf");
                LoadFile("Outfit-Bold.ttf");
                foreach (FontFamily family in PrivateFonts.Families)
                {
                    if (!Families.ContainsKey(family.Name))
                        Families[family.Name] = family;
                }
            }
        }

        private static void LoadFile(string fileName)
        {
            try
            {
                string path = Path.Combine(FontsDir, fileName);
                if (File.Exists(path))
                    PrivateFonts.AddFontFile(path);
            }
            catch { }
        }

        internal static bool IsAvailable(DnlSettings settings)
        {
            if (!UseClassic(settings)) return true;
            FontFamily family;
            return TryGetOutfit(out family);
        }

        private static bool TryGetOutfit(out FontFamily family)
        {
            family = null;
            EnsureLoaded();
            if (Families.TryGetValue("Outfit", out family)) return true;
            try
            {
                family = new FontFamily("Outfit");
                return true;
            }
            catch { return false; }
        }

        internal static Font Create(DnlSettings settings, float size, FontStyle style)
        {
            if (!UseClassic(settings))
                return new Font("Segoe UI", size, style);

            FontFamily family;
            if (!TryGetOutfit(out family))
                return new Font("Segoe UI", size, style);

            FontStyle useStyle = style;
            if (!family.IsStyleAvailable(useStyle))
            {
                if ((style & FontStyle.Bold) != 0 && family.IsStyleAvailable(FontStyle.Bold))
                    useStyle = FontStyle.Bold;
                else if (family.IsStyleAvailable(FontStyle.Regular))
                    useStyle = FontStyle.Regular;
            }

            try { return new Font(family, size, useStyle, GraphicsUnit.Point); }
            catch { return new Font("Segoe UI", size, style); }
        }

        internal static void Apply(Control root, DnlSettings settings)
        {
            if (root == null || settings == null) return;
            bool classic = UseClassic(settings) && IsAvailable(settings);
            ApplyRecursive(root, settings, classic);
        }

        private static void ApplyRecursive(Control control, DnlSettings settings, bool classic)
        {
            // Do not paint a second text pass over ordinary WinForms controls. Their native
            // text is already rendered by the control itself; overlaying a shadow + second
            // foreground pass makes labels/buttons look doubled or blurry. Classic Adventure
            // therefore changes the interface font only. Shadow is used only where Dolphin NetPlay Launcher owns
            // the entire text draw path (currently the owner-drawn library list).
            try
            {
                if (control.Font != null && IsInterfaceFont(control.Font))
                {
                    float size = control.Font.SizeInPoints;
                    FontStyle style = control.Font.Style;
                    control.Font = classic
                        ? Create(settings, size, style)
                        : new Font("Segoe UI", size, style);

                    ListBox list = control as ListBox;
                    if (list != null)
                    {
                        // Ordinary lists track the interface font. Owner-drawn multi-line
                        // lists (notably Public NetPlay Sessions) deliberately use a taller
                        // row and must not collapse when Options reapplies fonts/themes.
                        if (list.DrawMode != DrawMode.OwnerDrawFixed || list.ItemHeight <= 40)
                            list.ItemHeight = Math.Max(22, list.Font.Height + 6);
                    }
                }

            }
            catch { }

            foreach (Control child in control.Controls)
                ApplyRecursive(child, settings, classic);
        }

        private static bool IsInterfaceFont(Font font)
        {
            if (font == null) return false;
            string name = font.Name ?? "";
            if (name.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.Equals("Consolas", StringComparison.OrdinalIgnoreCase)) return false;
            return name.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Microsoft Sans Serif", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Outfit", StringComparison.OrdinalIgnoreCase);
        }

    }

    internal sealed class UiSoundClip : IDisposable
    {
        public IntPtr Data;
        public int ByteLength;

        private UiSoundClip()
        {
        }

        public static UiSoundClip Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (new string(br.ReadChars(4)) != "RIFF")
                        return null;
                    br.ReadUInt32();
                    if (new string(br.ReadChars(4)) != "WAVE")
                        return null;

                    ushort formatTag = 0;
                    ushort channels = 0;
                    uint sampleRate = 0;
                    ushort bitsPerSample = 0;
                    byte[] pcm = null;

                    while (fs.Position + 8 <= fs.Length)
                    {
                        string chunkId = new string(br.ReadChars(4));
                        uint chunkSize = br.ReadUInt32();
                        long chunkStart = fs.Position;

                        if (chunkId == "fmt ")
                        {
                            if (chunkSize < 16)
                                return null;

                            formatTag = br.ReadUInt16();
                            channels = br.ReadUInt16();
                            sampleRate = br.ReadUInt32();
                            br.ReadUInt32();
                            br.ReadUInt16();
                            bitsPerSample = br.ReadUInt16();
                        }
                        else if (chunkId == "data")
                        {
                            if (chunkSize > int.MaxValue)
                                return null;
                            pcm = br.ReadBytes((int)chunkSize);
                        }

                        long next = chunkStart + chunkSize;
                        if ((chunkSize & 1) != 0)
                            next++;
                        if (next > fs.Length)
                            next = fs.Length;
                        fs.Position = next;
                    }

                    // Custom sound folders are intentionally simple: ordinary uncompressed
                    // PCM WAVs are accepted. Convert common mono/stereo 8/16-bit files to
                    // Dolphin NetPlay Launcher's shared PCM16 mono 44.1 kHz pool in memory.
                    if (formatTag != 1 || (channels != 1 && channels != 2) ||
                        (bitsPerSample != 8 && bitsPerSample != 16) ||
                        sampleRate < 8000 || sampleRate > 192000 ||
                        pcm == null || pcm.Length == 0)
                        return null;

                    int bytesPerSample = bitsPerSample / 8;
                    int frameBytes = bytesPerSample * channels;
                    int sourceFrames = pcm.Length / frameBytes;
                    if (sourceFrames <= 0)
                        return null;

                    double[] mono = new double[sourceFrames];
                    int offset = 0;
                    for (int i = 0; i < sourceFrames; i++)
                    {
                        double sum = 0.0;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            double sample;
                            if (bitsPerSample == 16)
                            {
                                short v = (short)(pcm[offset] | (pcm[offset + 1] << 8));
                                sample = v / 32768.0;
                                offset += 2;
                            }
                            else
                            {
                                sample = (pcm[offset] - 128) / 128.0;
                                offset += 1;
                            }
                            sum += sample;
                        }
                        mono[i] = sum / channels;
                    }

                    const int targetRate = 44100;
                    int targetFrames = sampleRate == targetRate
                        ? sourceFrames
                        : Math.Max(1, (int)Math.Round(sourceFrames * targetRate / (double)sampleRate));

                    byte[] converted = new byte[targetFrames * 2];
                    for (int i = 0; i < targetFrames; i++)
                    {
                        double sourcePosition = i * sampleRate / (double)targetRate;
                        int i0 = Math.Min(sourceFrames - 1, (int)sourcePosition);
                        int i1 = Math.Min(sourceFrames - 1, i0 + 1);
                        double fraction = sourcePosition - i0;
                        double value = mono[i0] + (mono[i1] - mono[i0]) * fraction;
                        value = Math.Max(-1.0, Math.Min(1.0, value));
                        short s = (short)Math.Round(value * 32767.0);
                        converted[i * 2] = (byte)(s & 0xFF);
                        converted[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                    }

                    UiSoundClip clip = new UiSoundClip();
                    clip.ByteLength = converted.Length;
                    clip.Data = Marshal.AllocHGlobal(converted.Length);
                    Marshal.Copy(converted, 0, clip.Data, converted.Length);
                    return clip;
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (Data != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(Data); } catch { }
                Data = IntPtr.Zero;
            }
            ByteLength = 0;
        }
    }

    internal sealed class UiWaveChannel : IDisposable
    {
        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint CallbackFunction = 0x00030000;
        private const uint WomDone = 0x03BD;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WaveOutCallback(
            IntPtr hwo,
            uint message,
            IntPtr instance,
            IntPtr param1,
            IntPtr param2);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(
            out IntPtr waveOut,
            uint deviceId,
            ref WaveFormatEx format,
            WaveOutCallback callback,
            IntPtr instance,
            uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(
            IntPtr waveOut,
            IntPtr header,
            uint headerSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(
            IntPtr waveOut,
            IntPtr header,
            uint headerSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(
            IntPtr waveOut,
            IntPtr header,
            uint headerSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr waveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr waveOut);

        private readonly object sync = new object();
        private readonly WaveOutCallback callback;
        private readonly uint headerSize;
        private IntPtr waveOut = IntPtr.Zero;
        private IntPtr header = IntPtr.Zero;
        private bool prepared;
        private bool busy;
        private bool disposed;

        public UiWaveChannel()
        {
            callback = new WaveOutCallback(OnWaveOut);
            headerSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
            header = Marshal.AllocHGlobal((int)headerSize);

            WaveFormatEx format = new WaveFormatEx();
            format.wFormatTag = 1;
            format.nChannels = 1;
            format.nSamplesPerSec = 44100;
            format.wBitsPerSample = 16;
            format.nBlockAlign = 2;
            format.nAvgBytesPerSec = 44100 * 2;
            format.cbSize = 0;

            int result = waveOutOpen(
                out waveOut,
                WaveMapper,
                ref format,
                callback,
                IntPtr.Zero,
                CallbackFunction);

            if (result != 0)
            {
                waveOut = IntPtr.Zero;
                try { Marshal.FreeHGlobal(header); } catch { }
                header = IntPtr.Zero;
            }
        }

        public bool IsUsable
        {
            get { return waveOut != IntPtr.Zero && header != IntPtr.Zero && !disposed; }
        }

        public bool TryPlay(UiSoundClip clip)
        {
            if (clip == null || clip.Data == IntPtr.Zero || clip.ByteLength <= 0)
                return false;

            lock (sync)
            {
                if (!IsUsable || busy)
                    return false;

                CleanupPreparedHeader();

                WaveHeader h = new WaveHeader();
                h.lpData = clip.Data;
                h.dwBufferLength = (uint)clip.ByteLength;
                h.dwBytesRecorded = 0;
                h.dwUser = IntPtr.Zero;
                h.dwFlags = 0;
                h.dwLoops = 0;
                h.lpNext = IntPtr.Zero;
                h.reserved = IntPtr.Zero;

                Marshal.StructureToPtr(h, header, false);

                if (waveOutPrepareHeader(waveOut, header, headerSize) != 0)
                    return false;

                prepared = true;
                busy = true;

                if (waveOutWrite(waveOut, header, headerSize) != 0)
                {
                    busy = false;
                    CleanupPreparedHeader();
                    return false;
                }

                return true;
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                if (!IsUsable)
                    return;

                try { waveOutReset(waveOut); } catch { }
                busy = false;
                CleanupPreparedHeader();
            }
        }

        private void OnWaveOut(
            IntPtr hwo,
            uint message,
            IntPtr instance,
            IntPtr param1,
            IntPtr param2)
        {
            if (message != WomDone)
                return;

            // Keep the multimedia callback tiny. Header cleanup happens on the next
            // Play/Reset call, outside the waveOut callback.
            lock (sync)
            {
                busy = false;
            }
        }

        private void CleanupPreparedHeader()
        {
            if (!prepared || waveOut == IntPtr.Zero || header == IntPtr.Zero)
                return;

            try { waveOutUnprepareHeader(waveOut, header, headerSize); } catch { }
            prepared = false;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;

                if (waveOut != IntPtr.Zero)
                {
                    try { waveOutReset(waveOut); } catch { }
                    busy = false;
                    CleanupPreparedHeader();
                    try { waveOutClose(waveOut); } catch { }
                    waveOut = IntPtr.Zero;
                }

                if (header != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(header); } catch { }
                    header = IntPtr.Zero;
                }
            }
        }
    }

    internal sealed class UiWavePool : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<UiWaveChannel> channels = new List<UiWaveChannel>();
        private int nextChannel;

        public UiWavePool(int count)
        {
            int requested = Math.Max(2, count);
            for (int i = 0; i < requested; i++)
            {
                UiWaveChannel channel = new UiWaveChannel();
                if (channel.IsUsable)
                    channels.Add(channel);
                else
                    channel.Dispose();
            }
        }

        public bool Play(UiSoundClip clip)
        {
            if (clip == null)
                return false;

            lock (sync)
            {
                if (channels.Count == 0)
                    return false;

                // Round-robin start point keeps rapid navigation from hammering one
                // hardware/software stream while a longer cue continues on another.
                for (int offset = 0; offset < channels.Count; offset++)
                {
                    int index = (nextChannel + offset) % channels.Count;
                    if (channels[index].TryPlay(clip))
                    {
                        nextChannel = (index + 1) % channels.Count;
                        return true;
                    }
                }

                // Eight channels is enough for normal UI use. If every channel is
                // genuinely occupied, skipping the newest cue is preferable to cutting
                // off a long sound that is already playing.
                return false;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                for (int i = 0; i < channels.Count; i++)
                    channels[i].Dispose();
                channels.Clear();
            }
        }
    }

    internal sealed class UiSoundStyleChoice
    {
        public string Id;
        public string DisplayName;

        public UiSoundStyleChoice(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal static class UiSoundManager
    {
        private static readonly object Sync = new object();
        private static string loadedStyle = "";
        private static readonly Dictionary<string, UiSoundClip> Clips =
            new Dictionary<string, UiSoundClip>(StringComparer.OrdinalIgnoreCase);

        // IMPORTANT: waveOut is asynchronous. During a live style switch an old cue
        // may still be referenced by the multimedia subsystem even after Reset().
        // Retain old clip buffers for the life of this Dolphin NetPlay Launcher process instead of freeing
        // them while Windows may still hold a pointer. The whole UI sound library is
        // small, so this costs only a few MB even after visiting every style.
        private static readonly List<UiSoundClip> RetiredClips = new List<UiSoundClip>();

        private static UiWavePool output;

        private static readonly string[] SoundNames = new string[]
        {
            "navigate", "switch", "library_open", "library_close", "stage_game",
            "use_game", "launch", "confirm", "cancel", "clear_game", "error"
        };


        // The pool now allows different sounds to overlap instead of restarting one
        // shared SoundPlayer. Keep the small gate only for held-direction repeat so
        // dozens of identical navigation cues do not stack into a wall of sound.
        private static readonly Dictionary<string, DateTime> LastPlayUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static string SoundsFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds"); }
        }

        public static string NormalizeStyleName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "Adventure" : value.Trim();
            name = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
                return "Adventure";
            return name;
        }

        public static bool IsValidStyleFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return false;

            foreach (string cue in SoundNames)
            {
                if (!File.Exists(Path.Combine(folder, cue + ".wav")))
                    return false;
            }
            return true;
        }

        public static string DisplayNameForStyle(string id)
        {
            if (string.Equals(id, "ClassicUI", StringComparison.OrdinalIgnoreCase))
                return "Classic UI (Lokif CC0)";
            return id;
        }

        public static List<UiSoundStyleChoice> GetAvailableStyles()
        {
            List<UiSoundStyleChoice> result = new List<UiSoundStyleChoice>();
            string root = SoundsFolder;
            try
            {
                if (!Directory.Exists(root))
                    Directory.CreateDirectory(root);

                string[] dirs = Directory.GetDirectories(root);
                Array.Sort(dirs, StringComparer.CurrentCultureIgnoreCase);

                // Familiar built-ins first when present.
                string[] preferred = new string[] { "Adventure", "Royal", "ClassicUI" };
                HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string id in preferred)
                {
                    string dir = Path.Combine(root, id);
                    if (IsValidStyleFolder(dir))
                    {
                        result.Add(new UiSoundStyleChoice(id, DisplayNameForStyle(id)));
                        added.Add(id);
                    }
                }

                foreach (string dir in dirs)
                {
                    string id = Path.GetFileName(dir);
                    if (added.Contains(id) || !IsValidStyleFolder(dir))
                        continue;
                    result.Add(new UiSoundStyleChoice(id, id));
                    added.Add(id);
                }
            }
            catch { }

            if (result.Count == 0)
                result.Add(new UiSoundStyleChoice("Adventure", "Adventure"));
            return result;
        }

        public static string ValidateStyleFolder(string folder)
        {
            StringBuilder sb = new StringBuilder();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                sb.AppendLine("Folder not found.");
                return sb.ToString();
            }

            string themeName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            sb.AppendLine("Theme: " + themeName);
            sb.AppendLine("Folder: " + folder);
            sb.AppendLine();

            List<string> missing = new List<string>();
            List<string> unreadable = new List<string>();
            int valid = 0;

            foreach (string cue in SoundNames)
            {
                string path = Path.Combine(folder, cue + ".wav");
                if (!File.Exists(path))
                {
                    missing.Add(cue + ".wav");
                    continue;
                }

                UiSoundClip clip = UiSoundClip.Load(path);
                if (clip == null)
                {
                    unreadable.Add(cue + ".wav");
                    continue;
                }

                valid++;
                clip.Dispose();
            }

            sb.AppendLine("Valid cues: " + valid + "/" + SoundNames.Length);

            if (missing.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Missing:");
                foreach (string file in missing)
                    sb.AppendLine("  - " + file);
            }

            if (unreadable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Unsupported or unreadable:");
                foreach (string file in unreadable)
                    sb.AppendLine("  - " + file);
                sb.AppendLine();
                sb.AppendLine("Custom WAVs must be uncompressed PCM, 8-bit or 16-bit, mono or stereo, 8-192 kHz.");
            }

            bool complete = missing.Count == 0 && unreadable.Count == 0;
            sb.AppendLine();
            if (complete)
            {
                sb.AppendLine("RESULT: Valid sound theme.");
                string root = Path.GetFullPath(SoundsFolder).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("Copy this folder into Dolphin NetPlay Launcher's Sounds folder for it to appear in the Sound style list.");
                else
                    sb.AppendLine("This theme is inside Dolphin NetPlay Launcher's Sounds folder and is discoverable.");
            }
            else
            {
                sb.AppendLine("RESULT: Incomplete sound theme. It will not appear in the Sound style list yet.");
            }

            return sb.ToString();
        }

        public static void Configure(DnlSettings settings)
        {
            if (settings == null || !settings.InterfaceSounds)
                return;

            string style = NormalizeStyleName(settings.SoundStyle);
            string requestedDir = Path.Combine(SoundsFolder, style);
            if (!IsValidStyleFolder(requestedDir))
            {
                DiagnosticsLog.Write("AUDIO", "Requested sound style is incomplete or missing: " + style + "; falling back to Adventure.");
                style = "Adventure";
                requestedDir = Path.Combine(SoundsFolder, style);
            }

            lock (Sync)
            {
                if (string.Equals(style, loadedStyle, StringComparison.OrdinalIgnoreCase) &&
                    Clips.Count > 0 && output != null)
                    return;

                DiagnosticsLog.Write("AUDIO", "Loading interface sound style: " + style + ".");

                if (output == null)
                    output = new UiWavePool(8);

                // Do NOT reset channels or free the old style here. A long sound is
                // allowed to finish naturally while the new theme becomes active.
                // More importantly, this avoids freeing unmanaged PCM while waveOut
                // may still reference it on another thread.
                foreach (UiSoundClip oldClip in Clips.Values)
                {
                    if (oldClip != null)
                        RetiredClips.Add(oldClip);
                }
                Clips.Clear();
                LastPlayUtc.Clear();

                string dir = requestedDir;
                int loadedCount = 0;
                foreach (string name in SoundNames)
                {
                    UiSoundClip clip = UiSoundClip.Load(Path.Combine(dir, name + ".wav"));
                    Clips[name] = clip;
                    if (clip != null)
                        loadedCount++;
                }

                loadedStyle = style;
                DiagnosticsLog.Write("AUDIO", "Interface sound style ready: " + style +
                    " (" + loadedCount + "/" + SoundNames.Length + " cues loaded).");
            }
        }

        public static void PlayNamed(DnlSettings settings, string name)
        {
            if (settings == null || !settings.InterfaceSounds || string.IsNullOrEmpty(name))
                return;

            try
            {
                Configure(settings);

                int minimumGapMs = name.Equals("navigate", StringComparison.OrdinalIgnoreCase) ? 82
                    : (name.Equals("switch", StringComparison.OrdinalIgnoreCase) ? 70 : 0);

                lock (Sync)
                {
                    if (minimumGapMs > 0)
                    {
                        DateTime now = DateTime.UtcNow;
                        DateTime last;
                        if (LastPlayUtc.TryGetValue(name, out last) &&
                            (now - last).TotalMilliseconds < minimumGapMs)
                            return;
                        LastPlayUtc[name] = now;
                    }

                    UiSoundClip clip;
                    if (output != null &&
                        Clips.TryGetValue(name, out clip) &&
                        clip != null)
                    {
                        output.Play(clip);
                    }
                }
            }
            catch
            {
                // Sounds are cosmetic. Never let audio failure affect controller/input flow.
            }
        }

        public static void PlayConfirm(DnlSettings settings) { PlayNamed(settings, "confirm"); }
        public static void PlayBack(DnlSettings settings) { PlayNamed(settings, "cancel"); }
        public static void PlaySessionsOpen(DnlSettings settings) { PlayNamed(settings, "library_open"); }
        public static void PlaySessionsClose(DnlSettings settings) { PlayNamed(settings, "library_close"); }

        public static void PlayForControllerAction(
            Form form,
            DnlSettings settings,
            ControllerAction action,
            Control beforeControl,
            bool libraryWasVisible,
            bool libraryIsVisible)
        {
            if (settings == null || !settings.InterfaceSounds) return;

            if (action == ControllerAction.ToggleGames)
            {
                PlayNamed(settings, libraryWasVisible ? "library_close" : "library_open");
                return;
            }

            if (action == ControllerAction.ToggleSessions)
            {
                // SetSessionsVisible owns the library_open/library_close sound so mouse,
                // R3 and button activation all share exactly one panel sound.
                return;
            }

            if (action == ControllerAction.FocusPaste ||
                action == ControllerAction.FocusClear ||
                action == ControllerAction.FocusPrimary)
            {
                PlayNamed(settings, "navigate");
                return;
            }

            if (action == ControllerAction.Accept)
            {
                if (beforeControl is ListBox || beforeControl is FlowLayoutPanel)
                {
                    PlayNamed(settings, "stage_game");
                    return;
                }

                Button button = beforeControl as Button;
                if (button != null)
                {
                    string label = (button.Text ?? "").Replace("▶", "").Trim();

                    if (label.Equals("Use Game", StringComparison.OrdinalIgnoreCase))
                    {
                        PlayNamed(settings, "use_game");
                        return;
                    }
                    if (label.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                    {
                        PlayNamed(settings, "clear_game");
                        return;
                    }
                    if (label.Equals("Games...", StringComparison.OrdinalIgnoreCase))
                    {
                        PlayNamed(settings, libraryWasVisible ? "library_close" : "library_open");
                        return;
                    }
                    if (label.Equals("Sessions...", StringComparison.OrdinalIgnoreCase))
                    {
                        // The Sessions panel itself owns its open/close sound.
                        return;
                    }
                    if (label.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        label.Equals("Join", StringComparison.OrdinalIgnoreCase))
                    {
                        PlayNamed(settings, "launch");
                        return;
                    }
                    if (label.Equals("Hide", StringComparison.OrdinalIgnoreCase))
                    {
                        // Embedded Sessions Hide plays Back in its click handler so mouse
                        // and controller activation share exactly one sound.
                        return;
                    }
                    if (label.Equals("Cancel", StringComparison.OrdinalIgnoreCase) ||
                        label.Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        PlayNamed(settings, "cancel");
                        return;
                    }
                }

                PlayNamed(settings, "confirm");
                return;
            }

            if (action == ControllerAction.Cancel)
            {
                PlayNamed(settings, "cancel");
                return;
            }

            if (action == ControllerAction.Left || action == ControllerAction.Right)
            {
                if (beforeControl is RadioButton)
                {
                    PlayNamed(settings, "switch");
                    return;
                }
                PlayNamed(settings, "navigate");
                return;
            }

            if (action == ControllerAction.Up || action == ControllerAction.Down ||
                action == ControllerAction.PreviousTab || action == ControllerAction.NextTab)
            {
                PlayNamed(settings, "navigate");
                return;
            }
        }
    }

    internal static class AppTheme
    {
        public static bool IsDark(DnlSettings settings)
        {
            if (IsOled(settings) || IsGameCube(settings)) return true;
            string mode = settings != null ? settings.Appearance : "System";
            if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key != null ? key.GetValue("AppsUseLightTheme") : null;
                    if (v is int) return ((int)v) == 0;
                }
            }
            catch { }
            return false;
        }

        public static bool IsAdventure(DnlSettings settings)
        {
            return settings != null && string.Equals(settings.ThemeStyle, "AdventureBlue", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOled(DnlSettings settings)
        {
            return settings != null && string.Equals(settings.ThemeStyle, "OledBlack", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGameCubeIndigo(DnlSettings settings)
        {
            return settings != null && string.Equals(settings.ThemeStyle, "GameCubeIndigo", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGameCubeSpice(DnlSettings settings)
        {
            return settings != null && string.Equals(settings.ThemeStyle, "GameCubeSpice", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGameCube(DnlSettings settings)
        {
            return IsGameCubeIndigo(settings) || IsGameCubeSpice(settings);
        }

        public static Color ThemeButton(DnlSettings settings)
        {
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(101, 69, 182); // #6545B6 reference indigo
            if (IsGameCubeSpice(settings)) return Color.FromArgb(220, 86, 0);
            if (IsAdventure(settings)) return Color.FromArgb(38, 73, 130);
            if (IsOled(settings)) return Color.FromArgb(12, 13, 16);
            return IsDark(settings) ? Color.FromArgb(52, 56, 62) : Color.FromArgb(242, 244, 247);
        }

        public static Color ThemeSelected(DnlSettings settings)
        {
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(126, 96, 207);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(255, 104, 0); // #FF6800 reference spice
            if (IsAdventure(settings)) return Color.FromArgb(54, 126, 191);
            if (IsOled(settings)) return Color.FromArgb(30, 68, 110);
            return IsDark(settings) ? Color.FromArgb(43, 73, 108) : Color.FromArgb(224, 237, 252);
        }

        public static Color Back(bool d) { return d ? Color.FromArgb(30, 32, 36) : SystemColors.Control; }
        public static Color Surface(bool d) { return d ? Color.FromArgb(39, 42, 47) : SystemColors.Control; }
        public static Color Field(bool d) { return d ? Color.FromArgb(48, 51, 57) : SystemColors.Window; }
        public static Color Fore(bool d) { return d ? Color.FromArgb(235, 237, 240) : SystemColors.ControlText; }
        public static Color Border(bool d) { return d ? Color.FromArgb(78, 83, 91) : Color.FromArgb(205, 210, 218); }

        public static Color Back(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(8, 20, 48);
            if (IsOled(settings)) return Color.Black;
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(35, 24, 68);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(88, 31, 0);
            return Back(IsDark(settings));
        }
        public static Color Surface(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(18, 42, 84);
            if (IsOled(settings)) return Color.FromArgb(2, 2, 3);
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(73, 48, 132);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(153, 55, 0);
            return Surface(IsDark(settings));
        }
        public static Color Field(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(11, 29, 64);
            if (IsOled(settings)) return Color.FromArgb(5, 5, 7);
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(48, 32, 91);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(103, 36, 0);
            return Field(IsDark(settings));
        }
        public static Color Fore(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(246, 250, 255);
            if (IsOled(settings)) return Color.FromArgb(242, 244, 248);
            if (IsGameCube(settings)) return Color.FromArgb(250, 247, 244);
            return Fore(IsDark(settings));
        }
        public static Color Border(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(86, 137, 203);
            if (IsOled(settings)) return Color.FromArgb(38, 42, 50);
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(155, 127, 222);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(255, 151, 76);
            return Border(IsDark(settings));
        }

        public static Color ArtworkWell(DnlSettings settings)
        {
            if (IsAdventure(settings)) return Color.FromArgb(10, 27, 59);
            if (IsOled(settings)) return Color.Black;
            if (IsGameCubeIndigo(settings)) return Color.FromArgb(42, 28, 79);
            if (IsGameCubeSpice(settings)) return Color.FromArgb(93, 32, 0);
            return IsDark(settings) ? Color.FromArgb(34, 36, 40) : Color.FromArgb(238, 238, 238);
        }

        public static void Apply(Form form, DnlSettings settings)
        {
            if (form == null) return;
            bool dark = IsDark(settings);
            bool adventure = IsAdventure(settings);
            bool oled = IsOled(settings);
            bool cube = IsGameCube(settings);
            form.BackColor = Back(settings);
            form.ForeColor = Fore(settings);
            ApplyChildren(form, settings, dark, adventure, oled, cube);
            LauncherForm launcher = form as LauncherForm;
            if (launcher != null) launcher.ApplyThemeLogo();
            form.Invalidate(true);
        }

        private static void ApplyChildren(Control parent, DnlSettings settings, bool dark, bool adventure, bool oled, bool cube)
        {
            foreach (Control c in parent.Controls)
            {
                c.ForeColor = Fore(settings);
                AdventureLabel adventureLabel = c as AdventureLabel;
                if (adventureLabel != null)
                {
                    adventureLabel.AdventureStyle = adventure;
                    // Labels should sit directly on the themed surface/backdrop.
                    // A solid Surface-colored label reads as a little boxed badge
                    // (for example MODE/Nickname) when the parent uses another shade.
                    adventureLabel.BackColor = Color.Transparent;
                }
                AdventureButton adventureButton = c as AdventureButton;
                if (adventureButton != null)
                {
                    adventureButton.RoundedThemeStyle = true;
                    adventureButton.AdventureStyle = adventure;
                    adventureButton.TintedThemeStyle = cube;
                    adventureButton.DarkThemeStyle = dark && !adventure && !cube;
                }
                AdventureRadioButton adventureRadio = c as AdventureRadioButton;
                if (adventureRadio != null)
                {
                    adventureRadio.RoundedThemeStyle = true;
                    adventureRadio.AdventureStyle = adventure;
                    adventureRadio.TintedThemeStyle = cube;
                    adventureRadio.DarkThemeStyle = dark && !adventure && !cube;
                    if (cube) adventureRadio.BackColor = ThemeButton(settings);
                }
                AdventureGroupBox adventureGroup = c as AdventureGroupBox;
                if (adventureGroup != null)
                {
                    adventureGroup.AdventureStyle = adventure;
                    adventureGroup.TintedThemeStyle = cube;
                    if (cube) adventureGroup.BackColor = Surface(settings);
                    adventureGroup.DarkThemeStyle = dark && !adventure && !cube;
                    adventureGroup.RoundedThemeStyle = true;
                    adventureGroup.Invalidate();
                }
                ThemedComboBox themedCombo = c as ThemedComboBox;
                if (themedCombo != null)
                {
                    themedCombo.AdventureStyle = adventure;
                    themedCombo.TintedThemeStyle = cube;
                    if (cube) themedCombo.BackColor = Field(settings);
                    themedCombo.OledThemeStyle = oled;
                    themedCombo.DarkThemeStyle = dark && !adventure && !cube;
                    themedCombo.Invalidate();
                }
                ThemedInfoBadge infoBadge = c as ThemedInfoBadge;
                if (infoBadge != null)
                {
                    infoBadge.AdventureStyle = adventure;
                    infoBadge.DarkThemeStyle = dark && !adventure;
                    infoBadge.Invalidate();
                }
                ThemedDepthPanel depthPanel = c as ThemedDepthPanel;
                if (depthPanel != null)
                {
                    depthPanel.AdventureStyle = adventure;
                    depthPanel.TintedThemeStyle = cube;
                    depthPanel.DarkThemeStyle = dark && !adventure && !cube;
                    depthPanel.Invalidate();
                }

                AnimatedThemePanel animatedPanel = c as AnimatedThemePanel;
                if (animatedPanel != null)
                {
                    string animationTheme = "Light";
                    if (settings != null)
                    {
                        if (IsGameCubeIndigo(settings)) animationTheme = "GameCubeIndigo";
                        else if (IsGameCubeSpice(settings)) animationTheme = "GameCubeSpice";
                        else if (IsAdventure(settings)) animationTheme = "AdventureBlue";
                        else if (IsOled(settings)) animationTheme = "OledBlack";
                        else if (IsDark(settings)) animationTheme = "Dark";
                    }

                    animatedPanel.ThemeStyle = animationTheme;
                    animatedPanel.AnimationEnabled = settings != null &&
                        settings.AnimatedThemeBackground;
                    animatedPanel.BackColor = Back(settings);
                    if (animatedPanel.AnimationEnabled)
                        DiagnosticsLog.Write("THEME", "Animated background enabled: " + animatedPanel.ThemeStyle + ".");
                }

                if (c is ListBox || c is FlowLayoutPanel)
                    NativeScrollbarTheme.Apply(c, dark || adventure || cube);

                if (c is TextBoxBase || c is ListBox || c is ListView || c is NumericUpDown || c is ComboBox)
                {
                    c.BackColor = Field(settings);
                }
                else if (c is Button)
                {
                    Button b = (Button)c;
                    bool customRounded = b is AdventureButton;
                    b.UseVisualStyleBackColor = !customRounded && !(dark || adventure || cube);
                    b.FlatStyle = (customRounded || dark || adventure || cube) ? FlatStyle.Flat : FlatStyle.Standard;
                    b.BackColor = ThemeButton(settings);
                    if (customRounded || dark || adventure || cube) b.FlatAppearance.BorderColor = Border(settings);
                }
                else if (c is AnimatedThemePanel)
                {
                    // AnimatedThemePanel paints the theme backdrop itself.
                }
                else if (string.Equals(c.Tag as string, "BackdropTransparent", StringComparison.Ordinal))
                {
                    // Structural chrome intentionally sits directly on the animated
                    // launcher canvas. Keep it transparent across live theme changes.
                    c.BackColor = Color.Transparent;
                }
                else if (c is TabPage || c is GroupBox || c is Panel ||
                         c is TableLayoutPanel || c is FlowLayoutPanel || c is TabControl)
                {
                    AdventureGroupBox raisedCard = c as AdventureGroupBox;
                    c.BackColor = (raisedCard != null && raisedCard.RaisedSection)
                        ? Color.FromArgb(208, Surface(settings))
                        : Surface(settings);
                }
                else if (!(c is PictureBox) && c.BackColor != Color.Transparent)
                {
                    c.BackColor = Surface(settings);
                }

                SelectorPanel sp = c as SelectorPanel;
                if (sp != null)
                {
                    sp.DarkMode = dark || adventure || cube;
                    sp.AdventureMode = adventure;
                    sp.TintedMode = cube;
                    sp.OledMode = oled;
                    sp.BackColor = cube ? ThemeButton(settings)
                        : (adventure ? Color.FromArgb(22, 49, 94)
                        : (oled ? Color.FromArgb(2, 2, 4)
                        : (dark ? Color.FromArgb(43, 46, 52) : Color.FromArgb(244, 246, 249))));
                    sp.Invalidate();
                }

                ControllerPromptBar prompt = c as ControllerPromptBar;
                if (prompt != null)
                {
                    prompt.DarkMode = dark || adventure || cube;
                    prompt.Invalidate();
                }

                if (c.HasChildren) ApplyChildren(c, settings, dark, adventure, oled, cube);
            }
        }
    }

    internal sealed class ControllerPromptBar : Control
    {
        public string PromptStyle = "Xbox";
        public string GamesButton = "North";
        public bool LibraryMode;
        public bool SessionsMode;
        public bool ShowPasteShortcut;
        public string PrimaryAction = "Host";
        public bool DarkMode;

        public ControllerPromptBar()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int x = 2;
            if (LibraryMode)
            {
                x = DrawPrompt(e.Graphics, x, GetGamesFace(), "Close Games");
                x = DrawStickPrompt(e.Graphics, x + 10, "Sessions");
                x = DrawPrompt(e.Graphics, x + 10, "South", "Choose");
                x = DrawShoulderPrompt(e.Graphics, x + 10, "Page");
                DrawPrompt(e.Graphics, x + 10, "East", "Back");
            }
            else
            {
                x = DrawPrompt(e.Graphics, x, GetGamesFace(), "Games");
                x = DrawStickPrompt(e.Graphics, x + 10, SessionsMode ? "Close Sessions" : "Sessions");

                if (ShowPasteShortcut)
                    x = DrawPrompt(e.Graphics, x + 10, GetPasteFace(), "Paste");

                x = DrawPrompt(e.Graphics, x + 10, "South", "Select");
                x = DrawPrompt(e.Graphics, x + 10, "East", "Back");
                x = DrawSelectPrompt(e.Graphics, x + 10, "Clear");
                DrawStartPrompt(e.Graphics, x + 10, PrimaryAction);
            }
        }

        private string GetGamesFace()
        {
            return string.Equals(GamesButton, "West", StringComparison.OrdinalIgnoreCase) ? "West" : "North";
        }

        private string GetPasteFace()
        {
            // Use whichever North/West face button is not assigned to Games.
            return string.Equals(GamesButton, "West", StringComparison.OrdinalIgnoreCase) ? "North" : "West";
        }

        private int DrawSelectPrompt(Graphics g, int x, string action)
        {
            string glyph;
            if (string.Equals(PromptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
                glyph = "Create";
            else if (string.Equals(PromptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
                glyph = "-";
            else
                glyph = "Select";

            using (Font glyphFont = new Font("Segoe UI", glyph == "-" ? 12F : 7.5F, FontStyle.Bold))
            using (Font actionFont = new Font("Segoe UI", 8.5F))
            {
                int pad = glyph == "-" ? 11 : 7;
                int buttonWidth = Math.Max(28, (int)Math.Ceiling(g.MeasureString(glyph, glyphFont).Width) + pad * 2);
                Rectangle r = new Rectangle(x, 6, buttonWidth, 21);
                using (GraphicsPath p = RoundedRect(r, 7F))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 242, 242)))
                using (Pen pen = new Pen(Color.FromArgb(120, 120, 120)))
                { g.FillPath(b, p); g.DrawPath(pen, p); }

                using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 55, 55)))
                {
                    SizeF sz = g.MeasureString(glyph, glyphFont);
                    g.DrawString(glyph, glyphFont, b, r.X + (r.Width - sz.Width) / 2F,
                        r.Y + (r.Height - sz.Height) / 2F - 1F);
                }
                using (SolidBrush b = new SolidBrush(DarkMode ? Color.FromArgb(220, 224, 230) : Color.FromArgb(70, 70, 70)))
                {
                    g.DrawString(action, actionFont, b, x + r.Width + 5, 8);
                    return x + r.Width + 5 + (int)Math.Ceiling(g.MeasureString(action, actionFont).Width);
                }
            }
        }

        private int DrawStartPrompt(Graphics g, int x, string action)
        {
            string glyph;
            if (string.Equals(PromptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
                glyph = "Options";
            else if (string.Equals(PromptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
                glyph = "+";
            else
                glyph = "Start";

            using (Font glyphFont = new Font("Segoe UI", glyph == "+" ? 11F : 7.5F, FontStyle.Bold))
            using (Font actionFont = new Font("Segoe UI", 8.5F))
            {
                int pad = glyph == "+" ? 11 : 7;
                int buttonWidth = Math.Max(28, (int)Math.Ceiling(g.MeasureString(glyph, glyphFont).Width) + pad * 2);
                Rectangle r = new Rectangle(x, 6, buttonWidth, 21);

                using (GraphicsPath p = RoundedRect(r, 7F))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 242, 242)))
                using (Pen pen = new Pen(Color.FromArgb(120, 120, 120)))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }

                using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 55, 55)))
                {
                    SizeF sz = g.MeasureString(glyph, glyphFont);
                    g.DrawString(glyph, glyphFont, b,
                        r.X + (r.Width - sz.Width) / 2F,
                        r.Y + (r.Height - sz.Height) / 2F - 1F);
                }

                using (SolidBrush b = new SolidBrush(DarkMode ? Color.FromArgb(220, 224, 230) : Color.FromArgb(70, 70, 70)))
                {
                    g.DrawString(action, actionFont, b, x + r.Width + 5, 8);
                    return x + r.Width + 5 + (int)Math.Ceiling(g.MeasureString(action, actionFont).Width);
                }
            }
        }

        private int DrawStickPrompt(Graphics g, int x, string action)
        {
            // Draw an actual analog-stick cap/stem instead of a shoulder-button-shaped
            // pill. R3 is a stick click, and the prompt should communicate that at a glance.
            bool isSwitch = string.Equals(PromptStyle, "Switch", StringComparison.OrdinalIgnoreCase);
            string glyph = isSwitch ? "R" : "R3";
            using (Font glyphFont = new Font("Segoe UI", 6.75F, FontStyle.Bold))
            using (Font actionFont = new Font("Segoe UI", 8.5F))
            {
                const int iconWidth = 28;
                Rectangle cap = new Rectangle(x + 4, 5, 20, 13);
                Rectangle stem = new Rectangle(x + 11, 16, 6, 8);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(242, 242, 242)))
                using (Pen pen = new Pen(Color.FromArgb(120, 120, 120), 1F))
                {
                    using (GraphicsPath capPath = RoundedRect(cap, 6F))
                    {
                        g.FillPath(bg, capPath);
                        g.DrawPath(pen, capPath);
                    }
                    g.FillRectangle(bg, stem);
                    g.DrawLine(pen, stem.Left, stem.Top, stem.Left, stem.Bottom);
                    g.DrawLine(pen, stem.Right, stem.Top, stem.Right, stem.Bottom);
                    g.DrawLine(pen, stem.Left, stem.Bottom, stem.Right, stem.Bottom);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 55, 55)))
                {
                    SizeF sz = g.MeasureString(glyph, glyphFont);
                    g.DrawString(glyph, glyphFont, b, cap.X + (cap.Width - sz.Width) / 2F, cap.Y + 1F);
                }
                string label = isSwitch ? "R Stick " + action : action;
                using (SolidBrush b = new SolidBrush(DarkMode ? Color.FromArgb(220, 224, 230) : Color.FromArgb(70, 70, 70)))
                {
                    g.DrawString(label, actionFont, b, x + iconWidth + 5, 8);
                    return x + iconWidth + 5 + (int)Math.Ceiling(g.MeasureString(label, actionFont).Width);
                }
            }
        }

        private int DrawPrompt(Graphics g, int x, string face, string action)
        {
            const int size = 23;
            Rectangle r = new Rectangle(x, 5, size, size);
            Color fill;
            Color border;
            Color textColor;
            string glyph = GetFaceGlyph(face, out fill, out border, out textColor);

            using (SolidBrush bg = new SolidBrush(fill))
                g.FillEllipse(bg, r);
            using (Pen p = new Pen(border, 1.5F))
                g.DrawEllipse(p, r);
            using (SolidBrush b = new SolidBrush(textColor))
            {
                bool playStation = string.Equals(PromptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase);
                string faceFontName = playStation ? "Segoe UI Symbol" : "Segoe UI";
                float faceFontSize = playStation ? 13.5F : 9F;
                FontStyle faceFontStyle = playStation ? FontStyle.Regular : FontStyle.Bold;
                using (Font f = new Font(faceFontName, faceFontSize, faceFontStyle))
                {
                    SizeF gs = g.MeasureString(glyph, f);
                    g.DrawString(glyph, f, b,
                        r.X + (r.Width - gs.Width) / 2F,
                        r.Y + (r.Height - gs.Height) / 2F - (playStation ? 2F : 1F));
                }
            }

            using (SolidBrush b = new SolidBrush(DarkMode ? Color.FromArgb(220, 224, 230) : Color.FromArgb(70, 70, 70)))
            using (Font f = new Font("Segoe UI", 8.5F))
            {
                g.DrawString(action, f, b, x + size + 5, 8);
                return x + size + 5 + (int)Math.Ceiling(g.MeasureString(action, f).Width);
            }
        }

        private int DrawShoulderPrompt(Graphics g, int x, string action)
        {
            string left;
            string right;
            if (string.Equals(PromptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                left = "L1"; right = "R1";
            }
            else if (string.Equals(PromptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
            {
                left = "L"; right = "R";
            }
            else
            {
                left = "LB"; right = "RB";
            }

            string glyph = left + "/" + right;
            Rectangle r = new Rectangle(x, 6, 48, 21);
            using (GraphicsPath p = RoundedRect(r, 7F))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 242, 242)))
            using (Pen pen = new Pen(Color.FromArgb(120, 120, 120)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }

            using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 55, 55)))
            using (Font f = new Font("Segoe UI", 7.5F, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(glyph, f);
                g.DrawString(glyph, f, b, r.X + (r.Width - sz.Width) / 2F, r.Y + 2F);
            }

            using (SolidBrush b = new SolidBrush(DarkMode ? Color.FromArgb(220, 224, 230) : Color.FromArgb(70, 70, 70)))
            using (Font f = new Font("Segoe UI", 8.5F))
            {
                g.DrawString(action, f, b, x + r.Width + 5, 8);
                return x + r.Width + 5 + (int)Math.Ceiling(g.MeasureString(action, f).Width);
            }
        }

        private string GetFaceGlyph(string face, out Color fill, out Color border, out Color textColor)
        {
            bool north = string.Equals(face, "North", StringComparison.OrdinalIgnoreCase);
            bool west = string.Equals(face, "West", StringComparison.OrdinalIgnoreCase);
            bool east = string.Equals(face, "East", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(PromptStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                fill = Color.FromArgb(247, 247, 250);
                textColor = Color.FromArgb(35, 35, 42);
                if (north) { border = Color.FromArgb(70, 190, 105); return "△"; }
                if (west)  { border = Color.FromArgb(225, 95, 160); return "□"; }
                if (east)  { border = Color.FromArgb(225, 90, 90); return "○"; }
                border = Color.FromArgb(80, 130, 220); return "×";
            }

            if (string.Equals(PromptStyle, "Switch", StringComparison.OrdinalIgnoreCase))
            {
                fill = Color.FromArgb(248, 248, 248);
                border = Color.FromArgb(80, 80, 80);
                textColor = Color.FromArgb(35, 35, 35);
                if (north) return "X";
                if (west) return "Y";
                if (east) return "A";
                return "B";
            }

            // Xbox
            border = Color.FromArgb(80, 80, 80);
            textColor = Color.White;
            if (north) { fill = Color.FromArgb(230, 185, 45); return "Y"; }
            if (west)  { fill = Color.FromArgb(55, 125, 210); return "X"; }
            if (east)  { fill = Color.FromArgb(205, 65, 65); return "B"; }
            fill = Color.FromArgb(70, 160, 75); return "A";
        }

        private GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }


    internal static class AdventurePaint
    {
        internal static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(c.A,
                Math.Min(255, c.R + amount),
                Math.Min(255, c.G + amount),
                Math.Min(255, c.B + amount));
        }

        internal static Color Darken(Color c, int amount)
        {
            return Color.FromArgb(c.A,
                Math.Max(0, c.R - amount),
                Math.Max(0, c.G - amount),
                Math.Max(0, c.B - amount));
        }

        internal static void DrawOutlinedText(Graphics g, string text, Font font, Rectangle bounds,
            Color fore, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text)) return;
            Color outline = Color.FromArgb(5, 9, 18);
            Point[] offsets = new Point[]
            {
                new Point(-1, 0), new Point(1, 0),
                new Point(0, -1), new Point(0, 1),
                new Point(1, 1), new Point(2, 2)
            };
            foreach (Point off in offsets)
            {
                Rectangle sr = bounds;
                sr.Offset(off.X, off.Y);
                TextRenderer.DrawText(g, text, font, sr, outline, flags);
            }
            TextRenderer.DrawText(g, text, font, bounds, fore, flags);
        }
    }

    internal sealed class AdventureButton : Button
    {
        private bool adventureStyle;
        private bool roundedThemeStyle;
        private bool darkThemeStyle;
        private bool tintedThemeStyle;
        public bool TintedThemeStyle
        {
            get { return tintedThemeStyle; }
            set { tintedThemeStyle = value; Invalidate(); }
        }
        public bool RoundedThemeStyle
        {
            get { return roundedThemeStyle; }
            set
            {
                roundedThemeStyle = value;
                UpdateAdventureRegion();
                Invalidate();
            }
        }
        public bool DarkThemeStyle
        {
            get { return darkThemeStyle; }
            set { darkThemeStyle = value; Invalidate(); }
        }
        public bool AdventureStyle
        {
            get { return adventureStyle; }
            set
            {
                adventureStyle = value;
                UpdateAdventureRegion();
                Invalidate();
            }
        }
        public Action<PaintEventArgs> AdventureOverlayPaint { get; set; }
        private bool pressed;
        private bool hot;

        public AdventureButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateAdventureRegion();
        }

        private void UpdateAdventureRegion()
        {
            Region old = Region;
            if (!roundedThemeStyle || Width <= 0 || Height <= 0)
            {
                Region = null;
            }
            else
            {
                using (GraphicsPath p = RoundedRect(new Rectangle(0, 0, Width, Height), 6F))
                    Region = new Region(p);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!RoundedThemeStyle)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color disabledBase = AdventureStyle ? Color.FromArgb(31, 48, 78)
                : (TintedThemeStyle ? AdventurePaint.Darken(BackColor, 24)
                : (DarkThemeStyle ? Color.FromArgb(43, 46, 52) : Color.FromArgb(224, 227, 232)));
            Color baseColor = Enabled ? BackColor : disabledBase;
            if (hot && Enabled) baseColor = AdventurePaint.Lighten(baseColor, AdventureStyle ? 12 : (DarkThemeStyle ? 9 : 7));
            if (pressed && Enabled) baseColor = AdventurePaint.Darken(baseColor, AdventureStyle ? 10 : 8);

            int topLift = AdventureStyle ? (Enabled ? 34 : 12) : (DarkThemeStyle ? (Enabled ? 22 : 8) : (Enabled ? 18 : 8));
            int midLift = AdventureStyle ? (Enabled ? 8 : 4) : (DarkThemeStyle ? (Enabled ? 5 : 2) : (Enabled ? 5 : 2));
            int bottomDrop = AdventureStyle ? (Enabled ? 22 : 8) : (DarkThemeStyle ? (Enabled ? 16 : 7) : (Enabled ? 18 : 8));
            Color top = AdventurePaint.Lighten(baseColor, topLift);
            Color mid = AdventurePaint.Lighten(baseColor, midLift);
            Color bottom = AdventurePaint.Darken(baseColor, bottomDrop);
            Color borderColor = AdventureStyle ? Color.FromArgb(96, 155, 224)
                : (TintedThemeStyle ? AdventurePaint.Lighten(baseColor, 40)
                : (DarkThemeStyle ? Color.FromArgb(88, 94, 104) : Color.FromArgb(183, 190, 201)));
            using (GraphicsPath path = RoundedRect(r, 6F))
            using (LinearGradientBrush brush = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(borderColor, 1F))
            {
                ColorBlend blend = new ColorBlend();
                blend.Positions = new float[] { 0F, 0.42F, 1F };
                blend.Colors = new Color[] { top, mid, bottom };
                brush.InterpolationColors = blend;
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(border, path);
            }
            Color hiColor = AdventureStyle ? Color.FromArgb(105, 190, 240)
                : (TintedThemeStyle ? AdventurePaint.Lighten(baseColor, 56)
                : (DarkThemeStyle ? Color.FromArgb(108, 114, 124) : Color.FromArgb(255, 255, 255)));
            Color lowColor = AdventureStyle ? Color.FromArgb(20, 32, 64)
                : (TintedThemeStyle ? AdventurePaint.Darken(baseColor, 36)
                : (DarkThemeStyle ? Color.FromArgb(27, 29, 33) : Color.FromArgb(176, 182, 191)));
            using (Pen hi = new Pen(hiColor, 1F))
                e.Graphics.DrawLine(hi, 5, 2, Math.Max(5, Width - 6), 2);
            using (Pen low = new Pen(lowColor, 1F))
                e.Graphics.DrawLine(low, 5, Math.Max(2, Height - 3), Math.Max(5, Width - 6), Math.Max(2, Height - 3));

            Rectangle tr = ClientRectangle;
            if (pressed) tr.Offset(0, 1);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Color textColor = Enabled ? ForeColor : (DarkThemeStyle || AdventureStyle || TintedThemeStyle
                ? Color.FromArgb(150, 165, 185) : Color.FromArgb(135, 140, 150));
            if (AdventureStyle)
                AdventurePaint.DrawOutlinedText(e.Graphics, Text, Font, tr, textColor, flags);
            else
                TextRenderer.DrawText(e.Graphics, Text, Font, tr, textColor, flags);

            if (AdventureOverlayPaint != null)
                AdventureOverlayPaint(e);
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class AdventureRadioButton : RadioButton
    {
        private bool adventureStyle;
        private bool roundedThemeStyle;
        private bool darkThemeStyle;
        private bool tintedThemeStyle;
        public bool TintedThemeStyle
        {
            get { return tintedThemeStyle; }
            set { tintedThemeStyle = value; Invalidate(); }
        }
        public bool RoundedThemeStyle
        {
            get { return roundedThemeStyle; }
            set
            {
                roundedThemeStyle = value;
                UpdateAdventureRegion();
                Invalidate();
            }
        }
        public bool DarkThemeStyle
        {
            get { return darkThemeStyle; }
            set { darkThemeStyle = value; Invalidate(); }
        }
        public bool AdventureStyle
        {
            get { return adventureStyle; }
            set
            {
                adventureStyle = value;
                UpdateAdventureRegion();
                Invalidate();
            }
        }

        public AdventureRadioButton()
        {
            Appearance = System.Windows.Forms.Appearance.Button;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateAdventureRegion();
        }

        private void UpdateAdventureRegion()
        {
            Region old = Region;
            if (!roundedThemeStyle || Width <= 0 || Height <= 0)
            {
                Region = null;
            }
            else
            {
                using (GraphicsPath p = RoundedRect(new Rectangle(0, 0, Width, Height), 6F))
                    Region = new Region(p);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!RoundedThemeStyle)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color baseColor;
            Color borderColor;
            Color hiColor;
            Color lowColor;
            if (AdventureStyle)
            {
                baseColor = Checked ? Color.FromArgb(54, 126, 191) : Color.FromArgb(25, 53, 100);
                borderColor = Color.FromArgb(86, 137, 203);
                hiColor = Color.FromArgb(105, 190, 240);
                lowColor = Color.FromArgb(14, 28, 58);
            }
            else if (TintedThemeStyle)
            {
                baseColor = Checked ? AdventurePaint.Lighten(BackColor, 24) : BackColor;
                borderColor = AdventurePaint.Lighten(BackColor, 40);
                hiColor = AdventurePaint.Lighten(BackColor, 58);
                lowColor = AdventurePaint.Darken(BackColor, 36);
            }
            else if (DarkThemeStyle)
            {
                baseColor = Checked ? Color.FromArgb(52, 82, 118) : Color.FromArgb(43, 46, 52);
                borderColor = Color.FromArgb(82, 88, 98);
                hiColor = Color.FromArgb(105, 111, 121);
                lowColor = Color.FromArgb(27, 29, 33);
            }
            else
            {
                baseColor = Checked ? Color.FromArgb(224, 237, 252) : Color.FromArgb(244, 246, 249);
                borderColor = Color.FromArgb(195, 201, 211);
                hiColor = Color.White;
                lowColor = Color.FromArgb(185, 191, 201);
            }
            Color top = AdventurePaint.Lighten(baseColor, AdventureStyle ? (Checked ? 38 : 24) : (Checked ? 18 : 12));
            Color mid = AdventurePaint.Lighten(baseColor, AdventureStyle ? (Checked ? 12 : 5) : (Checked ? 5 : 3));
            Color bottom = AdventurePaint.Darken(baseColor, AdventureStyle ? (Checked ? 18 : 20) : (Checked ? 14 : 12));
            using (GraphicsPath path = RoundedRect(r, 6F))
            using (LinearGradientBrush b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(borderColor, 1F))
            {
                ColorBlend blend = new ColorBlend();
                blend.Positions = new float[] { 0F, 0.45F, 1F };
                blend.Colors = new Color[] { top, mid, bottom };
                b.InterpolationColors = blend;
                e.Graphics.FillPath(b, path);
                e.Graphics.DrawPath(border, path);
            }
            using (Pen hi = new Pen(hiColor, 1F))
                e.Graphics.DrawLine(hi, 7, 2, Math.Max(7, Width - 8), 2);
            using (Pen low = new Pen(lowColor, 1F))
                e.Graphics.DrawLine(low, 7, Math.Max(2, Height - 3), Math.Max(7, Width - 8), Math.Max(2, Height - 3));

            Color textColor = AdventureStyle
                ? (Enabled ? Color.FromArgb(248, 252, 255) : Color.FromArgb(150, 165, 185))
                : ForeColor;
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (AdventureStyle)
                AdventurePaint.DrawOutlinedText(e.Graphics, Text, Font, ClientRectangle, textColor, flags);
            else
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, textColor, flags);
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class AdventureGroupBox : GroupBox
    {
        public bool AdventureStyle { get; set; }
        public bool TintedThemeStyle { get; set; }
        public bool DarkThemeStyle { get; set; }
        public bool RoundedThemeStyle { get; set; }
        public bool RaisedSection { get; set; }

        public AdventureGroupBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            SizeChanged += delegate { ApplyRoundedRegion(); };
        }

        private void ApplyRoundedRegion()
        {
            if (Width < 4 || Height < 4) return;
            using (GraphicsPath p = RoundedRect(new Rectangle(0, 0, Width, Height), 8F))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!AdventureStyle && !RaisedSection)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Do not clear to an opaque parent color. OnPaintBackground has already
            // composed the parent through our alpha BackColor, letting the animated
            // launcher canvas breathe through this raised card.

            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Size textSize = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);

            Color top;
            Color bottom;
            Color border;
            Color highlight;
            Color shadow;
            if (AdventureStyle)
            {
                top = RaisedSection ? Color.FromArgb(35, 72, 128) : BackColor;
                bottom = RaisedSection ? Color.FromArgb(14, 34, 72) : BackColor;
                border = Color.FromArgb(74, 129, 194);
                highlight = Color.FromArgb(93, 158, 216);
                shadow = Color.FromArgb(7, 16, 38);
            }
            else if (TintedThemeStyle)
            {
                top = RaisedSection ? AdventurePaint.Lighten(BackColor, 20) : BackColor;
                bottom = RaisedSection ? AdventurePaint.Darken(BackColor, 18) : BackColor;
                border = AdventurePaint.Lighten(BackColor, 34);
                highlight = AdventurePaint.Lighten(BackColor, 48);
                shadow = AdventurePaint.Darken(BackColor, 34);
            }
            else if (DarkThemeStyle)
            {
                top = RaisedSection ? Color.FromArgb(56, 60, 68) : BackColor;
                bottom = RaisedSection ? Color.FromArgb(35, 38, 43) : BackColor;
                border = Color.FromArgb(83, 89, 99);
                highlight = Color.FromArgb(109, 115, 125);
                shadow = Color.FromArgb(18, 20, 24);
            }
            else
            {
                top = RaisedSection ? Color.FromArgb(250, 251, 253) : BackColor;
                bottom = RaisedSection ? Color.FromArgb(226, 231, 238) : BackColor;
                border = Color.FromArgb(188, 194, 204);
                highlight = Color.White;
                shadow = Color.FromArgb(174, 180, 190);
            }

            using (GraphicsPath path = RoundedRect(r, 8F))
            {
                if (RaisedSection)
                {
                    using (LinearGradientBrush bg = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                        e.Graphics.FillPath(bg, path);
                }
                else
                {
                    using (SolidBrush bg = new SolidBrush(BackColor))
                        e.Graphics.FillPath(bg, path);
                }
                using (Pen bp = new Pen(border, 1F))
                    e.Graphics.DrawPath(bp, path);
            }

            if (RaisedSection && Width > 18 && Height > 16)
            {
                using (Pen hi = new Pen(highlight, 1F))
                {
                    e.Graphics.DrawLine(hi, 12, 3, Width - 13, 3);
                    e.Graphics.DrawLine(hi, 3, 12, 3, Height - 13);
                }
                using (Pen sh = new Pen(shadow, 2F))
                {
                    e.Graphics.DrawLine(sh, 12, Height - 3, Width - 12, Height - 3);
                    e.Graphics.DrawLine(sh, Width - 3, 12, Width - 3, Height - 12);
                }
            }

            Rectangle tr = new Rectangle(10, 0, Math.Max(1, Width - 20), Math.Max(Font.Height + 5, textSize.Height + 2));
            if (AdventureStyle)
                AdventurePaint.DrawOutlinedText(e.Graphics, Text, Font, tr, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            else
                TextRenderer.DrawText(e.Graphics, Text, Font, tr, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2F, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class AdventureLabel : Label
    {
        public bool AdventureStyle { get; set; }

        public AdventureLabel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!AdventureStyle || BorderStyle != BorderStyle.None || string.IsNullOrEmpty(Text))
            {
                base.OnPaint(e);
                return;
            }

            OnPaintBackground(e);
            Rectangle r = ClientRectangle;
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            switch (TextAlign)
            {
                case ContentAlignment.TopCenter:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.BottomCenter:
                    flags |= TextFormatFlags.HorizontalCenter; break;
                case ContentAlignment.TopRight:
                case ContentAlignment.MiddleRight:
                case ContentAlignment.BottomRight:
                    flags |= TextFormatFlags.Right; break;
                default:
                    flags |= TextFormatFlags.Left; break;
            }

            switch (TextAlign)
            {
                case ContentAlignment.MiddleLeft:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.MiddleRight:
                    flags |= TextFormatFlags.VerticalCenter; break;
                case ContentAlignment.BottomLeft:
                case ContentAlignment.BottomCenter:
                case ContentAlignment.BottomRight:
                    flags |= TextFormatFlags.Bottom; break;
                default:
                    flags |= TextFormatFlags.Top; break;
            }

            if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
            if (!AutoSize && Height > Font.Height + 6) flags |= TextFormatFlags.WordBreak;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;

            Color outline = Color.FromArgb(5, 9, 18);
            Color fore = Enabled ? ForeColor : SystemColors.GrayText;

            // OoT-inspired crisp outline/drop shadow. Multiple one-pixel passes create
            // separation without the blurry double-rendering that native controls showed.
            Point[] offsets = new Point[]
            {
                new Point(-1, 0), new Point(1, 0),
                new Point(0, -1), new Point(0, 1),
                new Point(1, 1), new Point(2, 2)
            };
            foreach (Point off in offsets)
            {
                Rectangle sr = r;
                sr.Offset(off.X, off.Y);
                TextRenderer.DrawText(e.Graphics, Text, Font, sr, outline, flags);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, r, fore, flags);
        }
    }

    internal sealed class ThemedComboBox : ComboBox
    {
        public bool AdventureStyle { get; set; }
        public bool TintedThemeStyle { get; set; }
        public bool DarkThemeStyle { get; set; }
        public bool OledThemeStyle { get; set; }

        public ThemedComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 18;
            FlatStyle = FlatStyle.Flat;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            const int WM_PAINT = 0x000F;
            const int WM_NCPAINT = 0x0085;
            if ((m.Msg == WM_PAINT || m.Msg == WM_NCPAINT) &&
                (AdventureStyle || TintedThemeStyle || DarkThemeStyle || OledThemeStyle) &&
                IsHandleCreated && Width > 8 && Height > 8)
            {
                PaintThemeChrome();
            }
        }

        private void PaintThemeChrome()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    Rectangle outer = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                    int arrowWidth = Math.Max(18, SystemInformation.VerticalScrollBarWidth);
                    Rectangle arrow = new Rectangle(
                        Math.Max(0, Width - arrowWidth - 1),
                        1,
                        Math.Max(1, arrowWidth),
                        Math.Max(1, Height - 2));

                    Color baseColor = BackColor;
                    if (OledThemeStyle)
                        baseColor = Color.FromArgb(8, 8, 10);
                    else if (DarkThemeStyle && !TintedThemeStyle && !AdventureStyle)
                        baseColor = Color.FromArgb(48, 51, 57);

                    Color top = AdventureStyle
                        ? Color.FromArgb(45, 90, 148)
                        : AdventurePaint.Lighten(baseColor, TintedThemeStyle ? 24 : 12);
                    Color bottom = AdventureStyle
                        ? Color.FromArgb(20, 42, 82)
                        : AdventurePaint.Darken(baseColor, TintedThemeStyle ? 18 : 10);
                    Color border = AdventureStyle
                        ? Color.FromArgb(86, 137, 203)
                        : AdventurePaint.Lighten(baseColor, TintedThemeStyle ? 38 : 24);

                    using (LinearGradientBrush b = new LinearGradientBrush(
                        arrow, top, bottom, LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(b, arrow);
                    }

                    using (Pen p = new Pen(border))
                    {
                        g.DrawRectangle(p, outer);
                        g.DrawLine(p, arrow.Left, 1, arrow.Left, Height - 2);
                    }

                    int cx = arrow.Left + arrow.Width / 2;
                    int cy = Height / 2 + 1;
                    Point[] triangle = new Point[]
                    {
                        new Point(cx - 4, cy - 2),
                        new Point(cx + 4, cy - 2),
                        new Point(cx, cy + 3)
                    };

                    Color arrowColor = ForeColor;
                    using (SolidBrush b = new SolidBrush(arrowColor))
                        g.FillPolygon(b, triangle);
                }
            }
            catch
            {
                // The themed chrome is cosmetic. Never let a paint failure affect the UI.
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color back = AdventureStyle
                ? (selected ? Color.FromArgb(43, 91, 151) : Color.FromArgb(11, 29, 64))
                : (TintedThemeStyle
                    ? (selected ? AdventurePaint.Lighten(BackColor, 24) : BackColor)
                : (OledThemeStyle
                    ? (selected ? Color.FromArgb(36, 82, 132) : Color.FromArgb(8, 8, 10))
                    : (DarkThemeStyle
                    ? (selected ? Color.FromArgb(64, 116, 177) : Color.FromArgb(48, 51, 57))
                    : (selected ? Color.FromArgb(218, 233, 250) : Color.White))));
            Color fore = AdventureStyle || TintedThemeStyle || DarkThemeStyle ? Color.FromArgb(244, 248, 255) : Color.FromArgb(30, 30, 30);
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            string text = GetItemText(Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus && !AdventureStyle)
                e.DrawFocusRectangle();
        }
    }

    internal sealed class ThemedInfoBadge : Control
    {
        private bool blinkOn = true;
        public bool AdventureStyle { get; set; }
        public bool DarkThemeStyle { get; set; }
        public bool BlinkOn
        {
            get { return blinkOn; }
            set { blinkOn = value; Invalidate(); }
        }

        public ThemedInfoBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color baseColor;
            Color border;
            Color shadow;
            Color fore;
            if (AdventureStyle)
            {
                baseColor = blinkOn ? Color.FromArgb(222, 164, 39) : Color.FromArgb(38, 73, 130);
                border = blinkOn ? Color.FromArgb(242, 196, 76) : Color.FromArgb(75, 132, 198);
                shadow = Color.FromArgb(7, 16, 38);
                fore = Color.White;
            }
            else if (DarkThemeStyle)
            {
                baseColor = blinkOn ? Color.FromArgb(196, 139, 38) : Color.FromArgb(52, 56, 62);
                border = Color.FromArgb(93, 99, 109);
                shadow = Color.FromArgb(18, 20, 24);
                fore = Color.White;
            }
            else
            {
                baseColor = blinkOn ? Color.FromArgb(255, 210, 70) : Color.FromArgb(242, 244, 247);
                border = Color.FromArgb(183, 190, 201);
                shadow = Color.FromArgb(174, 180, 190);
                fore = Color.FromArgb(40, 40, 40);
            }

            Rectangle shadowRect = r;
            shadowRect.Offset(2, 2);
            shadowRect.Width = Math.Max(1, shadowRect.Width - 2);
            shadowRect.Height = Math.Max(1, shadowRect.Height - 2);
            using (GraphicsPath sp = RoundedRect(shadowRect, 6F))
            using (SolidBrush sb = new SolidBrush(shadow)) e.Graphics.FillPath(sb, sp);

            Rectangle face = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));
            Color top = AdventurePaint.Lighten(baseColor, blinkOn ? 20 : 12);
            Color bottom = AdventurePaint.Darken(baseColor, blinkOn ? 18 : 10);
            using (GraphicsPath fp = RoundedRect(face, 6F))
            using (LinearGradientBrush bg = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
            using (Pen bp = new Pen(border, 1F))
            {
                e.Graphics.FillPath(bg, fp);
                e.Graphics.DrawPath(bp, fp);
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, face, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2F, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SelectorPanel : Panel
    {
        public bool DarkMode { get; set; }
        public bool AdventureMode { get; set; }
        public bool TintedMode { get; set; }
        public bool OledMode { get; set; }

        public SelectorPanel()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(244, 246, 249);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath p = RoundedRect(r, 8F))
            {
                if (AdventureMode)
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(r,
                        Color.FromArgb(42, 82, 150), Color.FromArgb(17, 39, 82), LinearGradientMode.Vertical))
                    using (Pen pen = new Pen(Color.FromArgb(102, 157, 220), 1.2F))
                    {
                        e.Graphics.FillPath(b, p);
                        e.Graphics.DrawPath(pen, p);
                    }
                }
                else if (TintedMode)
                {
                    Color top = AdventurePaint.Lighten(BackColor, 28);
                    Color bottom = AdventurePaint.Darken(BackColor, 20);
                    using (LinearGradientBrush b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                    using (Pen pen = new Pen(AdventurePaint.Lighten(BackColor, 44), 1.1F))
                    {
                        e.Graphics.FillPath(b, p);
                        e.Graphics.DrawPath(pen, p);
                    }
                }
                else
                {
                    Color top = OledMode ? Color.FromArgb(28, 30, 34)
                        : (DarkMode ? Color.FromArgb(58, 62, 70) : Color.FromArgb(255, 255, 255));
                    Color bottom = OledMode ? Color.FromArgb(5, 5, 7)
                        : (DarkMode ? Color.FromArgb(34, 37, 42) : Color.FromArgb(228, 232, 238));
                    using (LinearGradientBrush b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                    using (Pen pen = new Pen(OledMode ? Color.FromArgb(58, 62, 70)
                        : (DarkMode ? Color.FromArgb(86, 92, 102) : Color.FromArgb(195, 201, 211)), 1F))
                    {
                        e.Graphics.FillPath(b, p);
                        e.Graphics.DrawPath(pen, p);
                    }
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }


    internal sealed class FirstRunDolphinForm : Form
    {
        private readonly DnlSettings settings;
        private readonly Label pathLabel = new AdventureLabel();
        private readonly Button primaryButton = new AdventureButton();
        private readonly Button chooseButton = new AdventureButton();
        private string candidate;

        public string SelectedDolphinExe { get; private set; }

        public FirstRunDolphinForm(string foundCandidate, DnlSettings settings)
        {
            this.settings = settings;
            candidate = foundCandidate;

            Text = "Dolphin NetPlay Launcher - First-time setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 285);
            Font = new Font("Segoe UI", 9F);

            Label title = new AdventureLabel();
            title.Text = string.IsNullOrWhiteSpace(candidate)
                ? "Let's find Dolphin"
                : "Dolphin found!";
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 20);
            Controls.Add(title);

            Label body = new AdventureLabel();
            body.AutoSize = false;
            body.Location = new Point(27, 62);
            body.Size = new Size(505, 66);
            body.Text = string.IsNullOrWhiteSpace(candidate)
                ? "Dolphin NetPlay Launcher could not find Dolphin.exe automatically.\r\n\r\n" +
                  "For the easiest setup, place the DolphinNetPlayLauncher folder inside your Dolphin installation folder."
                : "Dolphin NetPlay Launcher found Dolphin.exe in the recommended location. " +
                  "Use this installation, or choose a different Dolphin install.";
            Controls.Add(body);

            pathLabel.AutoSize = false;
            pathLabel.Location = new Point(27, 137);
            pathLabel.Size = new Size(505, 52);
            pathLabel.BorderStyle = BorderStyle.FixedSingle;
            pathLabel.Padding = new Padding(7);
            pathLabel.Text = string.IsNullOrWhiteSpace(candidate)
                ? "No Dolphin installation selected."
                : candidate;
            Controls.Add(pathLabel);

            primaryButton.Text = string.IsNullOrWhiteSpace(candidate) ? "Locate Dolphin..." : "Use This Dolphin";
            primaryButton.Size = new Size(150, 38);
            primaryButton.Location = new Point(382, 218);
            primaryButton.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    ChooseAnother();
                    return;
                }

                SelectedDolphinExe = candidate;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(primaryButton);

            chooseButton.Text = "Choose Another...";
            chooseButton.Size = new Size(140, 38);
            chooseButton.Location = new Point(232, 218);
            chooseButton.Visible = !string.IsNullOrWhiteSpace(candidate);
            chooseButton.Click += delegate { ChooseAnother(); };
            Controls.Add(chooseButton);

            Button cancel = new AdventureButton();
            cancel.Text = "Cancel";
            cancel.Size = new Size(90, 38);
            cancel.Location = new Point(27, 218);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = primaryButton;
            CancelButton = cancel;

            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);
        }

        private void ChooseAnother()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Locate Dolphin.exe";
                dlg.Filter = "Dolphin Emulator (Dolphin.exe)|Dolphin.exe";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                candidate = dlg.FileName;
                pathLabel.Text = candidate;
                primaryButton.Text = "Use This Dolphin";
                chooseButton.Visible = true;
                primaryButton.Focus();
            }
        }
    }

    internal sealed class FirstRunLibraryForm : Form
    {
        private readonly DnlSettings settings;

        public FirstRunLibraryForm(int gameFolderCount, string userDir, DnlSettings settings)
        {
            this.settings = settings;

            Text = "Dolphin NetPlay Launcher - Game Library";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(590, 405);
            Font = new Font("Segoe UI", 9F);

            Label title = new AdventureLabel();
            title.Text = "Before you continue";
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 20);
            Controls.Add(title);

            Label libraryTitle = new AdventureLabel();
            libraryTitle.Text = "Game library";
            libraryTitle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            libraryTitle.AutoSize = true;
            libraryTitle.Location = new Point(27, 67);
            Controls.Add(libraryTitle);

            Label libraryBody = new AdventureLabel();
            libraryBody.AutoSize = false;
            libraryBody.Location = new Point(27, 91);
            libraryBody.Size = new Size(535, 102);
            libraryBody.Text =
                "Dolphin NetPlay Launcher uses the game folders already configured in Dolphin. " +
                "Make sure your GameCube and/or Wii game folders are already visible in Dolphin's game list.\r\n\r\n" +
                (gameFolderCount > 0
                    ? "Configured Dolphin game folders found: " + gameFolderCount + "."
                    : "No configured Dolphin game folders were found yet. The built-in Games library will be empty until you add them in Dolphin.");
            Controls.Add(libraryBody);

            Label netplayTitle = new AdventureLabel();
            netplayTitle.Text = "NetPlay";
            netplayTitle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            netplayTitle.AutoSize = true;
            netplayTitle.Location = new Point(27, 207);
            Controls.Add(netplayTitle);

            Label netplayBody = new AdventureLabel();
            netplayBody.AutoSize = false;
            netplayBody.Location = new Point(27, 231);
            netplayBody.Size = new Size(535, 64);
            netplayBody.Text =
                "Host: Select a local game before hosting.\r\n" +
                "Join: You do not need to select a local game first. The host determines the game.";
            Controls.Add(netplayBody);

            Label hint = new AdventureLabel();
            hint.AutoSize = false;
            hint.Location = new Point(27, 300);
            hint.Size = new Size(535, 48);
            hint.Text = gameFolderCount > 0
                ? "Your Dolphin library looks ready."
                : "Open Dolphin, add your game folders there, then close Dolphin and run the launcher again. " +
                  "You will be reminded until you either configure the library or explicitly continue without it.";
            Controls.Add(hint);

            Button open = new AdventureButton();
            open.Text = "Open Dolphin";
            open.Size = new Size(125, 38);
            open.Location = new Point(245, 355);
            open.DialogResult = DialogResult.Retry;
            Controls.Add(open);

            Button cont = new AdventureButton();
            cont.Text = gameFolderCount > 0 ? "Continue" : "Continue Without Library";
            cont.Size = new Size(gameFolderCount > 0 ? 125 : 170, 38);
            cont.Location = new Point(gameFolderCount > 0 ? 437 : 392, 355);
            cont.DialogResult = DialogResult.OK;
            Controls.Add(cont);

            Button cancel = new AdventureButton();
            cancel.Text = "Cancel";
            cancel.Size = new Size(90, 38);
            cancel.Location = new Point(27, 355);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = cont;
            CancelButton = cancel;

            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);
        }
    }


    internal static class NativeScrollbarTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

        internal static void Apply(Control control, bool darkLike)
        {
            if (control == null || control.IsDisposed) return;
            try
            {
                if (!control.IsHandleCreated) control.CreateControl();
                SetWindowTheme(control.Handle, darkLike ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }
    }

    internal sealed class RoundedImageBox : PictureBox
    {
        public int ImageInset { get; set; }
        public float CornerRadius { get; set; }

        public RoundedImageBox()
        {
            ImageInset = 2;
            CornerRadius = 10F;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle clipRect = new Rectangle(
                ImageInset, ImageInset,
                Math.Max(1, Width - ImageInset * 2),
                Math.Max(1, Height - ImageInset * 2));

            using (GraphicsPath clip = RoundedRect(clipRect, CornerRadius))
            {
                GraphicsState state = e.Graphics.Save();
                e.Graphics.SetClip(clip);
                if (Image != null)
                {
                    Rectangle dest = FitZoom(Image.Size, clipRect);
                    e.Graphics.DrawImage(Image, dest);
                }
                e.Graphics.Restore(state);
            }
        }

        private static Rectangle FitZoom(Size imageSize, Rectangle bounds)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0) return bounds;
            double scale = Math.Min(
                (double)bounds.Width / imageSize.Width,
                (double)bounds.Height / imageSize.Height);
            int w = Math.Max(1, (int)Math.Round(imageSize.Width * scale));
            int h = Math.Max(1, (int)Math.Round(imageSize.Height * scale));
            return new Rectangle(
                bounds.X + (bounds.Width - w) / 2,
                bounds.Y + (bounds.Height - h) / 2,
                w, h);
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2F, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class ThemedDepthPanel : Panel
    {
        public bool AdventureStyle { get; set; }
        public bool TintedThemeStyle { get; set; }
        public bool DarkThemeStyle { get; set; }
        public float CornerRadius { get; set; }
        public int DepthPixels { get; set; }
        public bool DrawInnerPlate { get; set; }
        public Rectangle InnerPlateBounds { get; set; }
        public Color InnerPlateBackColor { get; set; }

        public ThemedDepthPanel()
        {
            CornerRadius = 8F;
            DepthPixels = 3;
            InnerPlateBackColor = Color.Empty;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(3);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            int depth = Math.Max(1, DepthPixels);
            Rectangle face = new Rectangle(0, 0,
                Math.Max(1, Width - depth - 1),
                Math.Max(1, Height - depth - 1));
            Rectangle shadow = face;
            shadow.Offset(depth, depth);

            Color shadowColor = AdventureStyle ? Color.FromArgb(7, 16, 38)
                : (TintedThemeStyle ? AdventurePaint.Darken(BackColor, 34)
                : (DarkThemeStyle ? Color.FromArgb(18, 20, 24) : Color.FromArgb(174, 180, 190)));
            Color borderColor = AdventureStyle ? Color.FromArgb(94, 154, 222)
                : (TintedThemeStyle ? AdventurePaint.Lighten(BackColor, 34)
                : (DarkThemeStyle ? Color.FromArgb(87, 93, 103) : Color.FromArgb(188, 194, 204)));
            Color highlightColor = AdventureStyle ? Color.FromArgb(88, 151, 211)
                : (TintedThemeStyle ? AdventurePaint.Lighten(BackColor, 50)
                : (DarkThemeStyle ? Color.FromArgb(107, 113, 123) : Color.White));

            using (GraphicsPath sp = RoundedRect(shadow, CornerRadius))
            using (Pen shadowPen = new Pen(shadowColor, 2F))
                e.Graphics.DrawPath(shadowPen, sp);

            using (GraphicsPath fp = RoundedRect(face, CornerRadius))
            using (Pen border = new Pen(borderColor, 1F))
                e.Graphics.DrawPath(border, fp);

            if (face.Width > 14 && face.Height > 10)
            {
                using (Pen hi = new Pen(highlightColor, 1F))
                {
                    e.Graphics.DrawLine(hi, face.Left + 6, face.Top + 2, face.Right - 6, face.Top + 2);
                    e.Graphics.DrawLine(hi, face.Left + 2, face.Top + 6, face.Left + 2, face.Bottom - 6);
                }
            }

            if (DrawInnerPlate && InnerPlateBounds.Width > 4 && InnerPlateBounds.Height > 4)
            {
                Rectangle plate = InnerPlateBounds;
                Rectangle plateShadow = plate;
                plateShadow.Offset(2, 2);
                Color plateBack = InnerPlateBackColor.IsEmpty ? BackColor : InnerPlateBackColor;
                using (GraphicsPath ps = RoundedRect(plateShadow, 5F))
                using (SolidBrush sb = new SolidBrush(shadowColor))
                    e.Graphics.FillPath(sb, ps);
                using (GraphicsPath pp = RoundedRect(plate, 5F))
                using (SolidBrush pb = new SolidBrush(plateBack))
                using (Pen bp = new Pen(borderColor, 1F))
                {
                    e.Graphics.FillPath(pb, pp);
                    e.Graphics.DrawPath(bp, pp);
                }
                using (Pen hi = new Pen(highlightColor, 1F))
                    e.Graphics.DrawLine(hi, plate.Left + 5, plate.Top + 2, plate.Right - 5, plate.Top + 2);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2F, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class AnimatedThemePanel : Panel
    {
        private readonly System.Windows.Forms.Timer animationTimer;
        private static readonly DateTime sharedAnimationStarted = DateTime.UtcNow;
        private bool animationEnabled;
        private bool animationAllowed = true;
        private string themeStyle;

        // All launcher panels are viewports onto one shared animated canvas.
        // Expanding the form reveals more of the same backdrop instead of starting
        // a second copy of the animation in the side panel.
        public int BackgroundOriginX { get; set; }
        public int BackgroundCanvasWidth { get; set; }

        public bool AnimationAllowed
        {
            get { return animationAllowed; }
            set
            {
                animationAllowed = value;
                if (animationEnabled && animationAllowed && Visible)
                    animationTimer.Start();
                else
                    animationTimer.Stop();
                Invalidate();
            }
        }

        public bool AnimationEnabled
        {
            get { return animationEnabled && animationAllowed; }
            set
            {
                animationEnabled = value;
                if (animationEnabled && animationAllowed && Visible)
                    animationTimer.Start();
                else
                    animationTimer.Stop();
                Invalidate();
            }
        }

        public string ThemeStyle
        {
            get { return themeStyle; }
            set
            {
                themeStyle = value ?? "Default";
                Invalidate();
            }
        }

        public AnimatedThemePanel()
        {
            themeStyle = "Default";
            BackgroundOriginX = 0;
            BackgroundCanvasWidth = 1200;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            animationTimer = new System.Windows.Forms.Timer();
            // The backdrop moves very slowly, so 30 Hz is visually smooth while keeping
            // it well away from the latency-sensitive controller path.
            animationTimer.Interval = 33;
            animationTimer.Tick += delegate
            {
                // RC33: side-panel background painting was empirically starving the
                // latency-sensitive Games/Sessions controller/cursor timers.  During a
                // short active-navigation window, keep the already-painted backdrop on
                // screen and skip only NEW cosmetic invalidations.  The animation clock
                // itself never pauses, so motion resumes naturally after input settles.
                // RC35 interaction-priority gate: only the main panel can still
                // animate in normal use, because Games/Sessions have AnimationAllowed
                // disabled.  Skip new cosmetic invalidations briefly during active
                // side-panel navigation so cursor/list timers get the UI thread.
                if (animationEnabled && animationAllowed && Visible && UiAnimationBudget.BackdropMayAnimate)
                    Invalidate();
            };

            VisibleChanged += delegate
            {
                if (animationEnabled && animationAllowed && Visible) animationTimer.Start();
                else animationTimer.Stop();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                animationTimer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!animationEnabled || !animationAllowed)
            {
                base.OnPaintBackground(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
            e.Graphics.Clear(BackColor);

            double seconds = (DateTime.UtcNow - sharedAnimationStarted).TotalSeconds;
            bool indigo = string.Equals(themeStyle, "GameCubeIndigo", StringComparison.OrdinalIgnoreCase);
            bool spice = string.Equals(themeStyle, "GameCubeSpice", StringComparison.OrdinalIgnoreCase);
            bool adventure = string.Equals(themeStyle, "AdventureBlue", StringComparison.OrdinalIgnoreCase);
            bool oled = string.Equals(themeStyle, "OledBlack", StringComparison.OrdinalIgnoreCase);
            bool dark = string.Equals(themeStyle, "Dark", StringComparison.OrdinalIgnoreCase);

            // Original slow-skybox-inspired effect. Every theme gets the same motion
            // language, while the tint follows that theme's palette.
            Color[] centers;
            if (indigo)
            {
                centers = new Color[] {
                    Color.FromArgb(64, 126, 82, 205),
                    Color.FromArgb(52, 92, 116, 220),
                    Color.FromArgb(44, 151, 105, 216),
                    Color.FromArgb(42, 78, 132, 210) };
            }
            else if (spice)
            {
                centers = new Color[] {
                    Color.FromArgb(62, 255, 150, 48),
                    Color.FromArgb(48, 255, 99, 12),
                    Color.FromArgb(44, 255, 187, 72),
                    Color.FromArgb(40, 224, 74, 8) };
            }
            else if (adventure)
            {
                centers = new Color[] {
                    Color.FromArgb(48, 72, 155, 232),
                    Color.FromArgb(42, 38, 119, 214),
                    Color.FromArgb(38, 72, 199, 210),
                    Color.FromArgb(34, 28, 92, 190) };
            }
            else if (oled)
            {
                centers = new Color[] {
                    Color.FromArgb(24, 18, 90, 145),
                    Color.FromArgb(20, 16, 122, 164),
                    Color.FromArgb(18, 30, 72, 130),
                    Color.FromArgb(16, 10, 84, 118) };
            }
            else if (dark)
            {
                centers = new Color[] {
                    Color.FromArgb(30, 78, 100, 128),
                    Color.FromArgb(26, 95, 78, 128),
                    Color.FromArgb(24, 70, 82, 115),
                    Color.FromArgb(22, 95, 97, 118) };
            }
            else
            {
                // Light/System-light: very low-alpha cool pearl/blue movement.
                centers = new Color[] {
                    Color.FromArgb(24, 188, 211, 242),
                    Color.FromArgb(20, 176, 199, 231),
                    Color.FromArgb(18, 202, 218, 239),
                    Color.FromArgb(18, 164, 194, 228) };
            }

            // Paint in SHARED launcher coordinates. Each AnimatedThemePanel is only a
            // viewport into this canvas; side panels start at x=600.
            float canvasWidth = Math.Max(1200, BackgroundCanvasWidth);
            e.Graphics.TranslateTransform(-BackgroundOriginX, 0);

            // A very slow "rotating sky" impression: broad translucent diagonal bands
            // slide sideways at different rates. The movement is obvious if watched for
            // a second or two, but it should not compete with text/buttons.
            float travel = Math.Max(canvasWidth, Height) * 1.35F;
            for (int i = 0; i < 4; i++)
            {
                double speed = 16.0 + i * 5.5; // pixels/second
                float offset = (float)((seconds * speed + i * travel * 0.31) % travel);
                float x = offset - travel * 0.45F;
                RectangleF band = new RectangleF(
                    x,
                    -Height * 0.45F,
                    canvasWidth * 0.38F,
                    Height * 1.9F);

                e.Graphics.ResetTransform();
                // Re-enter shared launcher coordinates after ResetTransform. Without this,
                // the side panel restarts the artwork at x=0 and visibly looks like a
                // second copy while the window expands.
                e.Graphics.TranslateTransform(-BackgroundOriginX, 0);
                e.Graphics.TranslateTransform(band.X + band.Width / 2F, band.Y + band.Height / 2F);
                e.Graphics.RotateTransform(spice ? -14F : -18F);
                e.Graphics.TranslateTransform(-(band.X + band.Width / 2F), -(band.Y + band.Height / 2F));

                Color c = centers[i];
                using (LinearGradientBrush bandBrush = new LinearGradientBrush(
                    band,
                    Color.FromArgb(0, c),
                    c,
                    LinearGradientMode.Horizontal))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Positions = new float[] { 0F, 0.35F, 0.65F, 1F };
                    blend.Colors = new Color[] {
                        Color.FromArgb(0, c),
                        Color.FromArgb(c.A, c),
                        Color.FromArgb(c.A, c),
                        Color.FromArgb(0, c)
                    };
                    bandBrush.InterpolationColors = blend;
                    e.Graphics.FillRectangle(bandBrush, band);
                }
            }

            e.Graphics.ResetTransform();
            // Glows and the theme wash must use the exact same shared coordinates as
            // the bands. The panel is only a viewport onto the launcher-wide canvas.
            e.Graphics.TranslateTransform(-BackgroundOriginX, 0);

            // Large feathered glows give the background depth instead of reading as
            // simple stripes.
            for (int i = 0; i < centers.Length; i++)
            {
                float px = (float)(canvasWidth * (0.14 + i * 0.25) +
                    Math.Sin(seconds * (0.16 + i * 0.021) + i * 1.7) * canvasWidth * 0.32);
                float py = (float)(Height * (0.20 + (i % 2) * 0.38) +
                    Math.Cos(seconds * (0.12 + i * 0.018) + i * 0.9) * Height * 0.25);
                float rw = canvasWidth * (0.78F + i * 0.08F);
                float rh = Height * (0.48F + i * 0.06F);
                RectangleF ellipse = new RectangleF(px - rw / 2F, py - rh / 2F, rw, rh);

                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddEllipse(ellipse);
                    using (PathGradientBrush brush = new PathGradientBrush(path))
                    {
                        Color c = centers[i];
                        brush.CenterColor = Color.FromArgb(Math.Min(76, c.A + 8), c);
                        brush.SurroundColors = new Color[] { Color.FromArgb(0, c) };
                        e.Graphics.FillPath(brush, path);
                    }
                }
            }

            // Theme wash keeps the moving texture behind the controls instead of
            // turning the whole launcher into an animated wallpaper.
            Color washColor;
            if (indigo) washColor = Color.FromArgb(24, 27, 18, 62);
            else if (spice) washColor = Color.FromArgb(28, 75, 27, 0);
            else if (adventure) washColor = Color.FromArgb(30, 6, 18, 48);
            else if (oled) washColor = Color.FromArgb(18, 0, 0, 0);
            else if (dark) washColor = Color.FromArgb(24, 28, 30, 34);
            else washColor = Color.FromArgb(18, 248, 249, 252);

            using (SolidBrush wash = new SolidBrush(washColor))
            {
                e.Graphics.FillRectangle(wash, new RectangleF(0, 0, canvasWidth, Height));
            }
        }
    }


    internal sealed class PublicNetPlaySession
    {
        public string Name = "";
        public string Region = "";
        public string Game = "";
        public string Method = "";
        public string ServerId = "";
        public string Version = "";
        public bool HasPassword;
        public int PlayerCount;
        public int Port;
        public bool InGame;

        public override string ToString()
        {
            string title = string.IsNullOrWhiteSpace(Name) ? "(Unnamed session)" : Name;
            string game = string.IsNullOrWhiteSpace(Game) ? "Unknown game" : Game;
            return title + " — " + game;
        }
    }

    internal sealed class NetPlayPasswordForm : Form
    {
        private readonly TextBox passwordBox = new TextBox();

        public string Password { get { return passwordBox.Text; } }

        public NetPlayPasswordForm(DnlSettings settings, string sessionName)
        {
            Text = "Password Required";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(410, 155);
            MinimumSize = SizeFromClientSize(new Size(410, 155));
            MaximumSize = SizeFromClientSize(new Size(520, 155));
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            Label label = new AdventureLabel();
            label.Text = "Password for " + (string.IsNullOrWhiteSpace(sessionName) ? "this session" : sessionName);
            label.Location = new Point(18, 18);
            label.Size = new Size(370, 24);
            Controls.Add(label);

            passwordBox.Location = new Point(18, 50);
            passwordBox.Size = new Size(370, 26);
            passwordBox.UseSystemPasswordChar = true;
            Controls.Add(passwordBox);
            LauncherForm.EnableSoftRoundedEntry(passwordBox);

            Button join = new AdventureButton();
            join.Text = "Continue";
            join.Location = new Point(214, 103);
            join.Size = new Size(82, 30);
            join.DialogResult = DialogResult.OK;
            Controls.Add(join);

            Button cancel = new AdventureButton();
            cancel.Text = "Cancel";
            cancel.Location = new Point(306, 103);
            cancel.Size = new Size(82, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = join;
            CancelButton = cancel;

            Shown += delegate { passwordBox.Focus(); };

            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);
            ControllerNavigation.Attach(this, Program.ControllerManagerForChildForms, settings, null);
        }
    }

    internal sealed class PublicSessionsForm : Form
    {
        private readonly DnlSettings settings;
        private readonly string indexUrl;
        private readonly string localDolphinVersion;
        private readonly TextBox filterBox = new TextBox();
        private readonly ListBox sessionList = new ListBox();
        private readonly Label detailName = new AdventureLabel();
        private readonly Label detailGame = new AdventureLabel();
        private readonly Label detailMeta = new AdventureLabel();
        private readonly Label statusLabel = new AdventureLabel();
        private readonly Button joinButton = new AdventureButton();
        private readonly Button refreshButton = new AdventureButton();
        private readonly CheckBox sameVersionCheck = new CheckBox();
        private readonly Button closeButton = new AdventureButton();
        private readonly List<PublicNetPlaySession> allSessions = new List<PublicNetPlaySession>();
        private readonly BackgroundWorker loader = new BackgroundWorker();
        private readonly bool embeddedMode;
        private Font sessionBoldFont;

        public event EventHandler SessionChosen;
        public event EventHandler HideRequested;

        public PublicNetPlaySession SelectedSession { get; private set; }
        public string ResolvedServerId { get; private set; }

        public PublicSessionsForm(DnlSettings settings, string indexUrl, string localDolphinVersion)
            : this(settings, indexUrl, localDolphinVersion, false)
        {
        }

        public PublicSessionsForm(DnlSettings settings, string indexUrl, string localDolphinVersion, bool embeddedMode)
        {
            this.embeddedMode = embeddedMode;
            this.settings = settings;
            this.indexUrl = string.IsNullOrWhiteSpace(indexUrl)
                ? "https://lobby.dolphin-emu.org"
                : indexUrl.Trim().TrimEnd('/');
            this.localDolphinVersion = localDolphinVersion ?? "";

            Text = "Public NetPlay Sessions";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = embeddedMode ? new Size(470, 690) : new Size(820, 620);
            MinimumSize = embeddedMode ? Size.Empty : SizeFromClientSize(new Size(720, 540));
            MaximizeBox = !embeddedMode;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = !embeddedMode;

            // Embedded Sessions must not run a second animated renderer. e4 did that
            // and paid for two background paint loops while also making the side panel
            // look like a competing canvas. The parent sessionsPanel owns the animation;
            // this embedded child is only a viewport onto it.
            Control sessionHost = this;
            if (embeddedMode)
            {
                SetStyle(ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            Label heading = new AdventureLabel();
            heading.Text = "PUBLIC NETPLAY SESSIONS";
            heading.Font = new Font("Segoe UI", 12.5F, FontStyle.Bold);
            heading.Location = new Point(18, 16);
            heading.AutoSize = true;
            sessionHost.Controls.Add(heading);

            Label source = new AdventureLabel();
            source.Text = embeddedMode ? "Dolphin public lobby" : "Dolphin index: " + this.indexUrl;
            source.Location = new Point(18, 39);
            source.Size = new Size(embeddedMode ? 260 : 520, 20);
            source.ForeColor = Color.DimGray;
            sessionHost.Controls.Add(source);

            filterBox.Location = new Point(18, 68);
            filterBox.Size = new Size(embeddedMode ? 310 : 560, 28);
            filterBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            filterBox.TextChanged += delegate { PopulateFiltered(); };
            sessionHost.Controls.Add(filterBox);

            Label filterHint = new AdventureLabel();
            filterHint.Text = "Search session, game, region, version...";
            filterHint.Location = new Point(24, 72);
            filterHint.AutoSize = true;
            filterHint.ForeColor = Color.DimGray;
            filterHint.Enabled = false;
            sessionHost.Controls.Add(filterHint);
            filterBox.TextChanged += delegate { filterHint.Visible = string.IsNullOrEmpty(filterBox.Text); };
            filterHint.Visible = true;

            refreshButton.Text = "Refresh";
            refreshButton.Location = embeddedMode ? new Point(340, 66) : new Point(692, 66);
            refreshButton.Size = embeddedMode ? new Size(108, 32) : new Size(105, 32);
            refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refreshButton.Click += delegate { BeginRefresh(); };
            sessionHost.Controls.Add(refreshButton);

            sameVersionCheck.Text = string.IsNullOrWhiteSpace(this.localDolphinVersion)
                ? "Same Dolphin version"
                : "Same Dolphin version (" + this.localDolphinVersion + ")";
            sameVersionCheck.AutoSize = true;
            sameVersionCheck.Location = new Point(18, 104);
            sameVersionCheck.Checked = false;
            sameVersionCheck.CheckedChanged += delegate { PopulateFiltered(); };
            sessionHost.Controls.Add(sameVersionCheck);

            sessionList.Location = new Point(18, 132);
            sessionList.Size = embeddedMode ? new Size(430, 372) : new Size(779, 311);
            sessionList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            sessionList.DrawMode = DrawMode.OwnerDrawFixed;
            sessionList.ItemHeight = 58;
            sessionList.IntegralHeight = false;
            sessionList.SelectedIndexChanged += delegate { UpdateDetails(); };
            sessionList.DoubleClick += delegate { JoinSelected(); };
            sessionList.DrawItem += DrawSessionItem;
            sessionHost.Controls.Add(sessionList);
            LauncherForm.EnableSoftRoundedEntry(filterBox);
            LauncherForm.EnableSoftRoundedEntry(sessionList);

            AdventureGroupBox details = new AdventureGroupBox();
            details.RaisedSection = true;
            details.Text = "Selected Session";
            details.Location = embeddedMode ? new Point(18, 518) : new Point(18, 461);
            details.Size = embeddedMode ? new Size(430, 92) : new Size(779, 92);
            details.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            sessionHost.Controls.Add(details);

            detailName.Location = new Point(14, 22);
            detailName.Size = new Size(embeddedMode ? 398 : 740, 22);
            detailName.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            details.Controls.Add(detailName);

            detailGame.Location = new Point(14, 44);
            detailGame.Size = new Size(embeddedMode ? 398 : 740, 20);
            details.Controls.Add(detailGame);

            detailMeta.Location = new Point(14, 65);
            detailMeta.Size = new Size(embeddedMode ? 398 : 740, 20);
            detailMeta.ForeColor = Color.DimGray;
            details.Controls.Add(detailMeta);

            statusLabel.Location = embeddedMode ? new Point(18, 624) : new Point(18, 574);
            statusLabel.Size = new Size(embeddedMode ? 210 : 530, 25);
            statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            statusLabel.ForeColor = Color.DimGray;
            sessionHost.Controls.Add(statusLabel);

            joinButton.Text = "Use for Join";
            joinButton.Location = embeddedMode ? new Point(236, 618) : new Point(595, 568);
            joinButton.Size = embeddedMode ? new Size(112, 32) : new Size(105, 32);
            joinButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            joinButton.Enabled = false;
            joinButton.Click += delegate { JoinSelected(); };
            sessionHost.Controls.Add(joinButton);

            closeButton.Text = embeddedMode ? "Hide" : "Close";
            closeButton.Location = embeddedMode ? new Point(358, 618) : new Point(710, 568);
            closeButton.Size = embeddedMode ? new Size(90, 32) : new Size(87, 32);
            closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            if (!embeddedMode)
                closeButton.DialogResult = DialogResult.Cancel;
            closeButton.Click += delegate
            {
                if (embeddedMode)
                {
                    // Hide is a Back/Cancel semantic, not a panel-toggle sound.
                    UiSoundManager.PlayBack(settings);
                    if (HideRequested != null) HideRequested(this, EventArgs.Empty);
                }
            };
            sessionHost.Controls.Add(closeButton);

            AcceptButton = joinButton;
            if (!embeddedMode)
                CancelButton = closeButton;

            loader.DoWork += Loader_DoWork;
            loader.RunWorkerCompleted += Loader_Completed;

            FormClosed += delegate
            {
                if (loader.IsBusy)
                    DiagnosticsLog.Write("SESSIONS", "Session browser closed while refresh was still in progress.");

                if (sessionBoldFont != null)
                {
                    try { sessionBoldFont.Dispose(); } catch { }
                    sessionBoldFont = null;
                }
            };

            Shown += delegate
            {
                sessionList.Focus();
                BeginRefresh();
            };

            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);

            // AppFonts.Apply intentionally normalizes ordinary ListBox rows, but this
            // browser owns a three-line session row. Reassert its explicit row height
            // after font application so session/game/meta text cannot paint on top of
            // neighboring rows.
            sessionList.Font = AppFonts.Create(settings, 10.25F, FontStyle.Regular);
            sessionBoldFont = new Font(sessionList.Font, FontStyle.Bold);
            filterBox.Font = AppFonts.Create(settings, 10F, FontStyle.Regular);
            detailGame.Font = AppFonts.Create(settings, 9.75F, FontStyle.Regular);
            detailMeta.Font = AppFonts.Create(settings, 9.25F, FontStyle.Regular);
            statusLabel.Font = AppFonts.Create(settings, 9.25F, FontStyle.Regular);
            sessionList.ItemHeight = 74;
            sessionList.Invalidate();

            if (!embeddedMode)
                ControllerNavigation.Attach(this, Program.ControllerManagerForChildForms, settings, null);
        }

        public void FocusSessionsFromController()
        {
            if (sessionList.Items.Count > 0)
            {
                if (sessionList.SelectedIndex < 0) sessionList.SelectedIndex = 0;
                sessionList.Focus();
            }
            else
            {
                filterBox.Focus();
            }
        }

        public void FocusHideFromController()
        {
            closeButton.Focus();
        }

        private Control GetControllerActiveControl()
        {
            Control[] candidates = new Control[]
            {
                filterBox, refreshButton, sameVersionCheck, sessionList, joinButton, closeButton
            };

            foreach (Control candidate in candidates)
            {
                if (candidate != null && !candidate.IsDisposed &&
                    (candidate.Focused || candidate.ContainsFocus))
                    return candidate;
            }

            return ControllerNavigation.GetDeepActiveControl(this);
        }

        public void RefreshSessions()
        {
            // A global font/theme refresh can reach this embedded child form. Keep the
            // intended three-line public-session row geometry authoritative and refresh
            // the cached bold font only at this coarse boundary, never per row paint.
            sessionList.ItemHeight = 74;
            if (sessionBoldFont != null)
            {
                try { sessionBoldFont.Dispose(); } catch { }
            }
            sessionBoldFont = new Font(sessionList.Font, FontStyle.Bold);
            BeginRefresh();
        }

        private void BeginRefresh()
        {
            if (loader.IsBusy)
                return;

            refreshButton.Enabled = false;
            joinButton.Enabled = false;
            statusLabel.Text = "Loading public sessions...";
            DiagnosticsLog.Write("SESSIONS", "Refreshing public NetPlay sessions from " + indexUrl + ".");
            loader.RunWorkerAsync();
        }

        private void Loader_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = FetchSessions(indexUrl);
        }

        private void Loader_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            if (IsDisposed)
                return;

            refreshButton.Enabled = true;

            if (e.Error != null)
            {
                DiagnosticsLog.Exception("Public session refresh failed", e.Error);
                statusLabel.Text = "Could not load public sessions.";
                MessageBox.Show(
                    this,
                    "Dolphin NetPlay Launcher could not load the public session list.\n\n" +
                    e.Error.Message + "\n\n" +
                    "Dolphin NetPlay Launcher identifies itself as its own application and does not send Dolphin's X-Is-Dolphin header.",
                    "Public NetPlay Sessions",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            List<PublicNetPlaySession> loaded = e.Result as List<PublicNetPlaySession>;
            allSessions.Clear();
            if (loaded != null)
                allSessions.AddRange(loaded);

            DiagnosticsLog.Write("SESSIONS", "Public session refresh completed: " +
                allSessions.Count.ToString() + " sessions.");
            PopulateFiltered();
        }

        private List<PublicNetPlaySession> FetchSessions(string baseUrl)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 on .NET Framework 4.x

            string url = baseUrl.TrimEnd('/') + "/v0/list";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "DolphinNetPlayLauncher/" + Program.AppVersion;
            request.Accept = "application/json";
            // Deliberately DO NOT send X-Is-Dolphin. Dolphin NetPlay Launcher is a third-party frontend and
            // should identify itself honestly while we test whether the public list
            // endpoint accepts third-party readers.

            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                json = reader.ReadToEnd();
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object raw = serializer.DeserializeObject(json);
            Dictionary<string, object> root = raw as Dictionary<string, object>;
            if (root == null)
                throw new InvalidDataException("The lobby returned an unexpected response.");

            object statusObject;
            string status = root.TryGetValue("status", out statusObject)
                ? Convert.ToString(statusObject) : "";
            if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The lobby returned status: " +
                    (string.IsNullOrWhiteSpace(status) ? "(missing)" : status));

            List<PublicNetPlaySession> result = new List<PublicNetPlaySession>();
            object sessionsObject;
            if (!root.TryGetValue("sessions", out sessionsObject))
                return result;

            object[] entries = sessionsObject as object[];
            if (entries == null)
            {
                System.Collections.ArrayList list = sessionsObject as System.Collections.ArrayList;
                if (list != null)
                    entries = list.ToArray();
            }

            if (entries == null)
                return result;

            foreach (object entryObject in entries)
            {
                Dictionary<string, object> entry = entryObject as Dictionary<string, object>;
                if (entry == null)
                    continue;

                PublicNetPlaySession s = new PublicNetPlaySession();
                s.Name = GetString(entry, "name");
                s.Region = GetString(entry, "region");
                s.Game = GetString(entry, "game");
                s.Method = GetString(entry, "method");
                s.ServerId = GetString(entry, "server_id");
                s.Version = GetString(entry, "version");
                s.HasPassword = GetBool(entry, "password");
                s.PlayerCount = GetInt(entry, "player_count");
                s.Port = GetInt(entry, "port");
                s.InGame = GetBool(entry, "in_game");

                if (!string.IsNullOrWhiteSpace(s.ServerId) && !string.IsNullOrWhiteSpace(s.Method))
                    result.Add(s);
            }

            result.Sort(delegate(PublicNetPlaySession a, PublicNetPlaySession b)
            {
                if (a.InGame != b.InGame)
                    return a.InGame ? 1 : -1;
                int gameCompare = string.Compare(a.Game, b.Game, StringComparison.CurrentCultureIgnoreCase);
                if (gameCompare != 0) return gameCompare;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            object value;
            if (!d.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToInt32(value); } catch { return 0; }
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object value;
            if (!d.TryGetValue(key, out value) || value == null) return false;
            try { return Convert.ToBoolean(value); } catch { return false; }
        }

        private void PopulateFiltered()
        {
            string q = (filterBox.Text ?? "").Trim();
            sessionList.BeginUpdate();
            sessionList.Items.Clear();

            foreach (PublicNetPlaySession s in allSessions)
            {
                if (sameVersionCheck.Checked &&
                    !string.IsNullOrWhiteSpace(localDolphinVersion) &&
                    !string.Equals(localDolphinVersion, s.Version, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(q))
                {
                    string haystack = (s.Name + "\n" + s.Game + "\n" + s.Region + "\n" +
                        s.Version + "\n" + s.Method).ToLowerInvariant();
                    if (!haystack.Contains(q.ToLowerInvariant()))
                        continue;
                }
                sessionList.Items.Add(s);
            }

            sessionList.EndUpdate();

            if (sessionList.Items.Count > 0)
                sessionList.SelectedIndex = 0;

            statusLabel.Text = sessionList.Items.Count.ToString() +
                (sessionList.Items.Count == 1 ? " session shown" : " sessions shown");
            UpdateDetails();
        }

        private void DrawSessionItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= sessionList.Items.Count)
                return;

            PublicNetPlaySession s = sessionList.Items[e.Index] as PublicNetPlaySession;
            if (s == null)
                return;

            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? AppTheme.ThemeSelected(settings) : AppTheme.Field(settings);
            Color fore = AppTheme.Fore(settings);
            Color dim = AppTheme.IsDark(settings) ? Color.FromArgb(188, 193, 205) : Color.FromArgb(90, 94, 104);

            if (selected && AccentVisuals.Animated(settings))
            {
                // Match the Games list controller cursor. Native owner-drawn ListBoxes
                // flicker if continuously animated, so use the launcher's animated
                // gradient as a stable snapshot whenever this row naturally redraws.
                using (LinearGradientBrush b = AccentVisuals.CreateGradient(e.Bounds, 255))
                    e.Graphics.FillRectangle(b, e.Bounds);
                fore = Color.White;
                dim = Color.FromArgb(232, 236, 245);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(back))
                    e.Graphics.FillRectangle(b, e.Bounds);
            }

            Rectangle titleRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 6, e.Bounds.Width - 20, 22);
            Rectangle gameRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 29, e.Bounds.Width - 20, 20);
            Rectangle metaRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 51, e.Bounds.Width - 20, 18);

            string title = string.IsNullOrWhiteSpace(s.Name) ? "(Unnamed session)" : s.Name;
            string game = string.IsNullOrWhiteSpace(s.Game) ? "Unknown game" : s.Game;
            string meta = RegionName(s.Region) + " • " + s.PlayerCount.ToString() +
                (s.PlayerCount == 1 ? " player" : " players") + " • " +
                (s.InGame ? "In Game" : "Waiting") +
                (s.HasPassword ? " • Password" : "");

            Font bold = sessionBoldFont ?? sessionList.Font;
            TextRenderer.DrawText(e.Graphics, title, bold, titleRect, fore,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, game, sessionList.Font, gameRect, fore,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, meta, sessionList.Font, metaRect, dim,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (Pen p = new Pen(AppTheme.Border(settings)))
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            e.DrawFocusRectangle();
        }

        private void UpdateDetails()
        {
            PublicNetPlaySession s = sessionList.SelectedItem as PublicNetPlaySession;
            joinButton.Enabled = s != null;
            if (s == null)
            {
                detailName.Text = "";
                detailGame.Text = "";
                detailMeta.Text = "";
                return;
            }

            detailName.Text = string.IsNullOrWhiteSpace(s.Name) ? "(Unnamed session)" : s.Name;
            detailGame.Text = string.IsNullOrWhiteSpace(s.Game) ? "Unknown game" : s.Game;

            string versionNote = s.Version;
            if (!string.IsNullOrWhiteSpace(localDolphinVersion) &&
                !string.IsNullOrWhiteSpace(s.Version) &&
                !string.Equals(localDolphinVersion, s.Version, StringComparison.OrdinalIgnoreCase))
            {
                versionNote += "  •  Your Dolphin: " + localDolphinVersion;
            }

            detailMeta.Text = RegionName(s.Region) + " • " +
                (string.Equals(s.Method, "traversal", StringComparison.OrdinalIgnoreCase) ? "Traversal" : "Direct") +
                " • " + s.PlayerCount.ToString() + (s.PlayerCount == 1 ? " player" : " players") +
                " • " + (s.InGame ? "In Game" : "Waiting") +
                (s.HasPassword ? " • Password required" : "") +
                (string.IsNullOrWhiteSpace(versionNote) ? "" : " • " + versionNote);
        }

        private void JoinSelected()
        {
            PublicNetPlaySession s = sessionList.SelectedItem as PublicNetPlaySession;
            if (s == null)
                return;

            string resolved = s.ServerId;
            if (s.HasPassword)
            {
                using (NetPlayPasswordForm passwordForm = new NetPlayPasswordForm(settings, s.Name))
                {
                    if (passwordForm.ShowDialog(this) != DialogResult.OK)
                        return;

                    string decrypted;
                    if (!TryDecryptServerId(s.ServerId, passwordForm.Password, out decrypted))
                    {
                        MessageBox.Show(
                            this,
                            "That password could not decrypt the session address/code.",
                            "Public NetPlay Sessions",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                    resolved = decrypted;
                }
            }

            SelectedSession = s;
            ResolvedServerId = resolved;
            DiagnosticsLog.Write("SESSIONS", "Public session selected for Join: method " +
                s.Method + "; target redacted; password " + (s.HasPassword ? "yes" : "no") + ".");

            if (embeddedMode)
            {
                if (SessionChosen != null) SessionChosen(this, EventArgs.Empty);
            }
            else
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        public void JoinSelectedFromController()
        {
            JoinSelected();
        }

        private void EnsureSessionSelectionVisible(int selectedIndex)
        {
            if (selectedIndex < 0 || selectedIndex >= sessionList.Items.Count)
                return;

            int top = Math.Max(0, sessionList.TopIndex);
            int visibleRows = Math.Max(1, sessionList.ClientSize.Height / Math.Max(1, sessionList.ItemHeight));
            int lastVisible = Math.Min(sessionList.Items.Count - 1, top + visibleRows - 1);

            if (selectedIndex < top)
            {
                sessionList.TopIndex = selectedIndex;
            }
            else if (selectedIndex > lastVisible)
            {
                sessionList.TopIndex = Math.Max(0, selectedIndex - visibleRows + 1);
            }
        }

        public bool HandleControllerNavigation(ControllerAction action)
        {
            // B/Cancel is a navigation shortcut, not an immediate close: move to Hide.
            if (embeddedMode && action == ControllerAction.Cancel)
            {
                closeButton.Focus();
                return true;
            }

            Control active = GetControllerActiveControl();
            if (active == null)
                return false;

            // The Sessions panel is spatial rather than one long tab chain. The session
            // list is the central surface: Up/Down browse sessions, Left escapes toward
            // filtering, Right jumps directly to Use for Join. B always jumps to Hide.
            if (active == filterBox)
            {
                if (action == ControllerAction.Right) { refreshButton.Focus(); return true; }
                if (action == ControllerAction.Down) { sameVersionCheck.Focus(); return true; }
                if (action == ControllerAction.Accept) { filterBox.Focus(); return true; }
                return false;
            }

            if (active == refreshButton)
            {
                if (action == ControllerAction.Left) { filterBox.Focus(); return true; }
                if (action == ControllerAction.Down)
                {
                    sessionList.Focus();
                    if (sessionList.Items.Count > 0 && sessionList.SelectedIndex < 0) sessionList.SelectedIndex = 0;
                    return true;
                }
                if (action == ControllerAction.Accept) { refreshButton.PerformClick(); return true; }
                return false;
            }

            if (active == sameVersionCheck)
            {
                if (action == ControllerAction.Up) { filterBox.Focus(); return true; }
                if (action == ControllerAction.Right) { refreshButton.Focus(); return true; }
                if (action == ControllerAction.Down)
                {
                    sessionList.Focus();
                    if (sessionList.Items.Count > 0 && sessionList.SelectedIndex < 0) sessionList.SelectedIndex = 0;
                    return true;
                }
                if (action == ControllerAction.Accept)
                {
                    sameVersionCheck.Checked = !sameVersionCheck.Checked;
                    return true;
                }
                return false;
            }

            if (active == sessionList)
            {
                if (action == ControllerAction.Left)
                {
                    sameVersionCheck.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    if (joinButton.Enabled) joinButton.Focus();
                    else closeButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    if (sessionList.Items.Count > 0)
                    {
                        int current = sessionList.SelectedIndex < 0 ? 0 : sessionList.SelectedIndex;
                        sessionList.SelectedIndex = Math.Max(0, current - 1);
                        EnsureSessionSelectionVisible(sessionList.SelectedIndex);
                    }
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    if (sessionList.Items.Count > 0)
                    {
                        int current = sessionList.SelectedIndex < 0 ? 0 : sessionList.SelectedIndex;
                        sessionList.SelectedIndex = Math.Min(sessionList.Items.Count - 1, current + 1);
                        EnsureSessionSelectionVisible(sessionList.SelectedIndex);
                    }
                    return true;
                }
                if (action == ControllerAction.PreviousTab || action == ControllerAction.NextTab)
                {
                    if (sessionList.Items.Count > 0)
                    {
                        int page = Math.Max(1, sessionList.ClientSize.Height / Math.Max(1, sessionList.ItemHeight) - 1);
                        int delta = action == ControllerAction.PreviousTab ? -page : page;
                        int current = sessionList.SelectedIndex < 0 ? 0 : sessionList.SelectedIndex;
                        int next = Math.Max(0, Math.Min(sessionList.Items.Count - 1, current + delta));
                        sessionList.SelectedIndex = next;
                        EnsureSessionSelectionVisible(next);
                    }
                    return true;
                }
                if (action == ControllerAction.Accept)
                {
                    JoinSelected();
                    return true;
                }
                return false;
            }

            if (active == joinButton)
            {
                if (action == ControllerAction.Left || action == ControllerAction.Up)
                {
                    sessionList.Focus();
                    if (sessionList.Items.Count > 0 && sessionList.SelectedIndex < 0) sessionList.SelectedIndex = 0;
                    return true;
                }
                if (action == ControllerAction.Right) { closeButton.Focus(); return true; }
                if (action == ControllerAction.Accept) { if (joinButton.Enabled) joinButton.PerformClick(); return true; }
                return false;
            }

            if (active == closeButton)
            {
                if (action == ControllerAction.Left) { if (joinButton.Enabled) joinButton.Focus(); else sessionList.Focus(); return true; }
                if (action == ControllerAction.Up)
                {
                    sessionList.Focus();
                    if (sessionList.Items.Count > 0 && sessionList.SelectedIndex < 0) sessionList.SelectedIndex = 0;
                    return true;
                }
                if (action == ControllerAction.Accept) { closeButton.PerformClick(); return true; }
                return false;
            }

            return false;
        }

        private static string RegionName(string code)
        {
            if (string.Equals(code, "EA", StringComparison.OrdinalIgnoreCase)) return "East Asia";
            if (string.Equals(code, "CN", StringComparison.OrdinalIgnoreCase)) return "China";
            if (string.Equals(code, "EU", StringComparison.OrdinalIgnoreCase)) return "Europe";
            if (string.Equals(code, "NA", StringComparison.OrdinalIgnoreCase)) return "North America";
            if (string.Equals(code, "SA", StringComparison.OrdinalIgnoreCase)) return "South America";
            if (string.Equals(code, "OC", StringComparison.OrdinalIgnoreCase)) return "Oceania";
            if (string.Equals(code, "AF", StringComparison.OrdinalIgnoreCase)) return "Africa";
            return string.IsNullOrWhiteSpace(code) ? "Unknown region" : code;
        }

        private static bool TryDecryptServerId(string encrypted, string password, out string decodedId)
        {
            decodedId = "";
            if (string.IsNullOrEmpty(encrypted) || string.IsNullOrEmpty(password) ||
                (encrypted.Length % 2) != 0)
                return false;

            try
            {
                byte[] decoded = new byte[encrypted.Length / 2];
                for (int i = 0; i < encrypted.Length; i += 2)
                {
                    int high = encrypted[i] - 'A';
                    int low = encrypted[i + 1] - 'A';
                    if (high < 0 || high > 15 || low < 0 || low > 15)
                        return false;
                    decoded[i / 2] = (byte)((high << 4) | low);
                }

                byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (passwordBytes.Length == 0 || decoded.Length < 2)
                    return false;

                for (int i = 0; i < decoded.Length; i++)
                {
                    decoded[i] = (byte)(decoded[i] - (byte)i);
                    decoded[i] = (byte)(decoded[i] ^ passwordBytes[i % passwordBytes.Length]);
                }

                byte expected = decoded[decoded.Length - 1];
                int sum = 0;
                for (int i = 0; i < decoded.Length - 1; i++)
                    sum = (sum + decoded[i]) & 0xFF;

                if ((byte)sum != expected)
                    return false;

                decodedId = Encoding.UTF8.GetString(decoded, 0, decoded.Length - 1);
                return !string.IsNullOrWhiteSpace(decodedId);
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class LauncherForm : Form
    {
        private readonly RadioButton hostRadio = new AdventureRadioButton();
        private readonly RadioButton joinRadio = new AdventureRadioButton();
        private readonly RadioButton traversalRadio = new AdventureRadioButton();
        private readonly RadioButton directRadio = new AdventureRadioButton();
        private readonly SelectorPanel modeSelectorPanel = new SelectorPanel();
        private readonly SelectorPanel connectionSelectorPanel = new SelectorPanel();
        private readonly TextBox nickBox = new TextBox();
        private readonly TextBox targetBox = new TextBox();
        private readonly NumericUpDown portBox = new NumericUpDown();
        private readonly Label targetLabel = new AdventureLabel();
        private readonly Label portLabel = new AdventureLabel();
        private readonly Label connectionHeading = new AdventureLabel();
        private readonly Button pasteButton = new AdventureButton();
        private readonly Button goButton = new AdventureButton();
        private readonly Button cancelButton = new AdventureButton();
        private readonly Button dolphinUpdateButton = new AdventureButton();
        private readonly Button dolphinOptionsButton = new AdventureButton();
        private readonly Button dolphinChangeButton = new AdventureButton();
        private readonly System.Windows.Forms.Timer accentUiTimer =
            new System.Windows.Forms.Timer();
        private Control lastDolphinControllerControl;

        private readonly TableLayoutPanel rootLayout = new TableLayoutPanel();
        private readonly AnimatedThemePanel mainPanel = new AnimatedThemePanel();
        private readonly AnimatedThemePanel libraryPanel = new AnimatedThemePanel();
        private readonly AnimatedThemePanel sessionsPanel = new AnimatedThemePanel();
        private PublicSessionsForm embeddedSessionsBrowser;
        private bool sessionsVisible;
        private bool sidePanelSwapInProgress;
        // Side-panel focus can change programmatically without a controller navigation
        // tick. Notify the controller cursor once the destination control is established.
        public event EventHandler ControllerFocusSettled;

        private void NotifyControllerFocusSettled()
        {
            EventHandler handler = ControllerFocusSettled;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private readonly CheckBox publicHostCheck = new CheckBox();
        private readonly AdventureGroupBox publicHostGroup = new AdventureGroupBox();
        private readonly TextBox publicSessionNameBox = new TextBox();
        private readonly ThemedComboBox publicRegionBox = new ThemedComboBox();
        private readonly TextBox publicPasswordBox = new TextBox();

        private readonly ListBox gameList = new ListBox();
        private readonly FlowLayoutPanel gameGrid = new FlowLayoutPanel();
        private readonly Button libraryGridButton = new AdventureButton();
        private readonly Button libraryListButton = new AdventureButton();
        private readonly Button libraryHideButton = new AdventureButton();
        private readonly ThemedComboBox libraryColumnsBox = new ThemedComboBox();
        private readonly ToolTip libraryToolTip = new ToolTip();
        private bool libraryGridView = true;
        private bool mousePrefersGrid = true;
        private int libraryGridColumns = 3;
        private string selectedGridPath;
        private string lastStyledGridPath;
        private readonly Dictionary<string, Control> libraryGridTiles =
            new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private Font libraryGridRegularFont;
        private Font libraryGridBoldFont;
        // RC39: theme changes can replace the shared grid-title Font pair while paint
        // messages are already queued. Keep retired Font objects alive until the form
        // itself is disposed instead of invalidating a GDI Font handle mid-paint.
        private readonly List<Font> retiredLibraryGridFonts = new List<Font>();
        private readonly TextBox gameSearch = new TextBox();
        private readonly Button libraryBrowseButton = new AdventureButton();
        private readonly Button libraryUseButton = new AdventureButton();
        private readonly PictureBox libraryCover = new PictureBox();
        private readonly Label libraryCoverTitle = new AdventureLabel();
        private readonly Label libraryCoverMeta = new AdventureLabel();
        private readonly ControllerPromptBar controllerPromptBar = new ControllerPromptBar();
        private readonly RoundedImageBox launcherLogo = new RoundedImageBox();
        private readonly PictureBox gameBanner = new PictureBox();
        private readonly Label gameLabel = new AdventureLabel();
        private readonly Label gameMetaLabel = new AdventureLabel();
        private readonly ThemedInfoBadge joinGameInfoIcon = new ThemedInfoBadge();
        private readonly Button gamesButton = new AdventureButton();
        private readonly Button sessionsButton = new AdventureButton();
        private readonly Button clearGameButton = new AdventureButton();
        private readonly List<string> allGames;
        private bool libraryLoaded;
        private bool libraryRefreshStarted;
        private bool libraryLoading;
        private readonly Label libraryLoadingLabel = new AdventureLabel();
        private readonly Panel libraryEmptyPanel = new Panel();
        private readonly Label libraryEmptyLabel = new AdventureLabel();
        private readonly Button librarySetupButton = new AdventureButton();
        private readonly DolphinPaths dolphinPaths;
        private readonly DnlSettings settings;
        private readonly string dolphinVersion;
        private readonly Dictionary<string, GameInfo> libraryInfoCache =
            new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, LibraryMetadataRecord> libraryMetadataCache;
        private bool libraryMetadataDirty;
        private readonly System.Windows.Forms.Timer libraryPreviewTimer = new System.Windows.Forms.Timer();
        private string selectedGamePath;
        private string pendingLibraryGamePath;

        private const int TraversalRoomCodeMaxLength = 8;
        private const int DirectIpv4MaxLength = 15;
        private const int DirectIpv6MaxLength = 45;

        private string traversalValue;
        private string directValue;
        private JoinConnection previousJoinConnection;

        public event EventHandler CheckUpdateRequested;
        public event EventHandler ChangeDolphinRequested;
        public event EventHandler OptionsRequested;

        public LaunchMode Mode { get { return joinRadio.Checked ? LaunchMode.Join : LaunchMode.Host; } }
        public JoinConnection JoinType { get { return directRadio.Checked ? JoinConnection.Direct : JoinConnection.Traversal; } }
        public string Nickname { get { return nickBox.Text; } }
        public string Target { get { return targetBox.Text; } }
        public int Port { get { return (int)portBox.Value; } }
        public string SelectedGamePath { get { return selectedGamePath; } }
        public bool ShowInServerBrowser { get { return publicHostCheck.Checked; } }
        public string PublicSessionName { get { return publicSessionNameBox.Text.Trim(); } }
        public string PublicSessionRegion
        {
            get
            {
                if (publicRegionBox.SelectedItem == null) return "";
                string item = publicRegionBox.SelectedItem.ToString();
                int open = item.LastIndexOf('(');
                int close = item.LastIndexOf(')');
                return open >= 0 && close > open ? item.Substring(open + 1, close - open - 1) : item;
            }
        }
        public string PublicSessionPassword { get { return publicPasswordBox.Text; } }

        public LauncherForm(
            string gameName, string gameId, int revision, string version, string updateTrack,
            string nickname, string roomCode, string directIp, int directPort,
            bool lastWasDirect, bool startInJoinMode,
            List<string> libraryGames, string initialGamePath, bool startLibraryOpen,
            bool showControllerPrompts, string controllerPromptStyle, string controllerGamesButton,
            DolphinPaths dolphinPaths, DnlSettings settings)
        {
            allGames = libraryGames ?? new List<string>();
            libraryLoaded = libraryGames != null;
            this.dolphinPaths = dolphinPaths;
            this.settings = settings;
            this.dolphinVersion = version ?? "";
            UiSoundManager.Configure(settings);
            selectedGamePath = initialGamePath;
            libraryGridView = settings == null || !string.Equals(settings.LibraryView, "List", StringComparison.OrdinalIgnoreCase);
            mousePrefersGrid = libraryGridView;
            libraryGridColumns = settings != null ? Math.Max(3, Math.Min(5, settings.LibraryGridColumns)) : 3;

            Text = Program.AppDisplayName;
            // Composite the launcher form into a back buffer before presenting it.
            // This reduces exposed rectangular child-control backgrounds while the
            // right-side Games/Sessions surface is being replaced.
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 690);
            MinimumSize = SizeFromClientSize(new Size(600, 690));
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            TopMost = true;
            Font = new Font("Segoe UI", 9F);

            rootLayout.Dock = DockStyle.Fill;
            rootLayout.RowCount = 1;
            rootLayout.ColumnCount = 2;
            rootLayout.Margin = new Padding(0);
            rootLayout.Padding = new Padding(0);
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0F));
            Controls.Add(rootLayout);

            mainPanel.Dock = DockStyle.Fill;
            mainPanel.Margin = new Padding(0);
            mainPanel.MinimumSize = new Size(580, 620);
            rootLayout.Controls.Add(mainPanel, 0, 0);

            libraryPanel.Dock = DockStyle.Fill;
            libraryPanel.BackgroundOriginX = 600;
            // RC34: Games is intentionally an opaque/card-heavy side surface. Earlier
            // e4-e9 attempts to expose the live animated canvas here caused flashing,
            // compositing regressions, broken hierarchy, or severe sluggishness. User
            // testing confirms the accepted opaque Library UI hides nearly all backdrop
            // motion anyway, so do not spend a second 30 Hz paint loop behind it.
            libraryPanel.AnimationAllowed = false;
            libraryPanel.Margin = new Padding(0);
            libraryPanel.Visible = false;
            rootLayout.Controls.Add(libraryPanel, 1, 0);

            sessionsPanel.Dock = DockStyle.Fill;
            sessionsPanel.BackgroundOriginX = 600;
            // Sessions shares the same side-panel rendering cost and accepted opaque
            // presentation. Keep its themed surface static; only mainPanel owns the live
            // animated backdrop. Do not re-open the rejected transparency experiments.
            sessionsPanel.AnimationAllowed = false;
            sessionsPanel.Margin = new Padding(0);
            sessionsPanel.Visible = false;
            rootLayout.Controls.Add(sessionsPanel, 1, 0);

            Panel header = new Panel();
            header.Location = new Point(0, 0);
            header.Size = new Size(580, 142);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            // One shallow transparent structural layer is cheap enough and removes the
            // old giant header slab. Avoid nested transparent controls: e4 proved that
            // they force expensive parent compositing and make the whole UI sluggish.
            header.BackColor = Color.Transparent;
            mainPanel.Controls.Add(header);

            launcherLogo.Location = new Point(16, 12);
            launcherLogo.Size = new Size(70, 70);
            launcherLogo.ImageInset = 2;
            launcherLogo.CornerRadius = 11F;
            ApplyThemeLogo();
            header.Controls.Add(launcherLogo);

            Label title = new AdventureLabel();
            title.Text = "Dolphin NetPlay";
            title.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(98, 12);
            header.Controls.Add(title);

            gameBanner.Location = new Point(100, 42);
            gameBanner.Size = new Size(126, 42);
            gameBanner.SizeMode = PictureBoxSizeMode.Zoom;
            gameBanner.BackColor = Color.FromArgb(238, 241, 245);
            gameBanner.Visible = false;
            header.Controls.Add(gameBanner);

            // Keep selected-game text underneath the banner rather than beside it.
            // That gives long titles the full header width and prevents Clear/Games
            // from squeezing the title into an unreadably narrow strip.
            gameLabel.Text = gameName;
            gameLabel.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            gameLabel.AutoEllipsis = true;
            gameLabel.Location = new Point(100, 88);
            gameLabel.Size = new Size(228, 22);
            gameLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Controls.Add(gameLabel);

            gameMetaLabel.Text = BuildMeta(gameId, revision);
            gameMetaLabel.ForeColor = Color.DimGray;
            gameMetaLabel.AutoSize = false;
            gameMetaLabel.Location = new Point(100, 110);
            gameMetaLabel.Size = new Size(228, 18);
            gameMetaLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Controls.Add(gameMetaLabel);

            UpdateSelectedGameBanner();

            clearGameButton.Text = "Clear";
            clearGameButton.Location = new Point(340, 50);
            clearGameButton.Size = new Size(55, 30);
            clearGameButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            clearGameButton.Enabled = !string.IsNullOrWhiteSpace(selectedGamePath);
            clearGameButton.Click += delegate { ClearSelectedGame(); };
            header.Controls.Add(clearGameButton);

            gamesButton.Text = "Games...";
            gamesButton.Location = new Point(401, 50);
            gamesButton.Size = new Size(72, 30);
            gamesButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gamesButton.Click += delegate { ToggleLibrary(); };
            header.Controls.Add(gamesButton);

            sessionsButton.Text = "Sessions...";
            sessionsButton.Location = new Point(479, 50);
            sessionsButton.Size = new Size(89, 30);
            sessionsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sessionsButton.Click += delegate { ToggleSessionsPanel(); };
            header.Controls.Add(sessionsButton);

            joinGameInfoIcon.Text = "!";
            joinGameInfoIcon.Location = new Point(540, 84);
            joinGameInfoIcon.Size = new Size(28, 28);
            joinGameInfoIcon.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            joinGameInfoIcon.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            joinGameInfoIcon.Cursor = Cursors.Help;
            joinGameInfoIcon.Visible = false;
            ToolTip joinGameInfoTip = new ToolTip();
            joinGameInfoTip.AutoPopDelay = 12000;
            joinGameInfoTip.InitialDelay = 0;
            joinGameInfoTip.ReshowDelay = 100;

            string joinGameInfoText =
                "The selected game is not used when joining.\n" +
                "The host determines which game the NetPlay session uses.\n\n" +
                "Click Clear if you want to remove the selected game.";

            joinGameInfoIcon.MouseEnter += delegate
            {
                // Show the help above the icon rather than under the mouse pointer.
                // This is friendlier to users with large Windows cursor sizes.
                joinGameInfoTip.Hide(joinGameInfoIcon);
                joinGameInfoTip.Show(
                    joinGameInfoText,
                    joinGameInfoIcon,
                    joinGameInfoIcon.Width + 8,
                    -72,
                    12000);
            };
            joinGameInfoIcon.MouseLeave += delegate
            {
                joinGameInfoTip.Hide(joinGameInfoIcon);
            };

            header.Controls.Add(joinGameInfoIcon);

            System.Windows.Forms.Timer joinGameInfoBlinkTimer = new System.Windows.Forms.Timer();
            joinGameInfoBlinkTimer.Interval = 650;
            bool joinGameInfoBlinkOn = true;
            joinGameInfoBlinkTimer.Tick += delegate
            {
                if (!joinGameInfoIcon.Visible)
                {
                    joinGameInfoBlinkOn = true;
                    joinGameInfoIcon.BlinkOn = true;
                    return;
                }

                joinGameInfoBlinkOn = !joinGameInfoBlinkOn;
                joinGameInfoIcon.BlinkOn = joinGameInfoBlinkOn;
            };
            joinGameInfoBlinkTimer.Start();

            header.Resize += delegate
            {
                int rightEdge = clearGameButton.Left - 12;
                int width = Math.Max(180, rightEdge - gameLabel.Left);
                gameLabel.Width = width;
                gameMetaLabel.Width = width;
            };

            Label modeHeading = new AdventureLabel();
            modeHeading.Text = "MODE";
            modeHeading.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            modeHeading.ForeColor = Color.FromArgb(95, 100, 110);
            modeHeading.Location = new Point(24, 157);
            modeHeading.AutoSize = true;
            mainPanel.Controls.Add(modeHeading);

            modeSelectorPanel.Location = new Point(20, 178);
            modeSelectorPanel.Size = new Size(540, 48);
            modeSelectorPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            mainPanel.Controls.Add(modeSelectorPanel);

            hostRadio.Text = "Host";
            hostRadio.Appearance = System.Windows.Forms.Appearance.Button;
            hostRadio.TextAlign = ContentAlignment.MiddleCenter;
            hostRadio.FlatStyle = FlatStyle.Flat;
            hostRadio.FlatAppearance.BorderSize = 0;
            hostRadio.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            hostRadio.Location = new Point(6, 6);
            hostRadio.Size = new Size(260, 36);
            hostRadio.TabStop = true;
            hostRadio.Checked = true;
            modeSelectorPanel.Controls.Add(hostRadio);

            joinRadio.Text = "Join";
            joinRadio.Appearance = System.Windows.Forms.Appearance.Button;
            joinRadio.TextAlign = ContentAlignment.MiddleCenter;
            joinRadio.FlatStyle = FlatStyle.Flat;
            joinRadio.FlatAppearance.BorderSize = 0;
            joinRadio.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            joinRadio.Location = new Point(274, 6);
            joinRadio.Size = new Size(260, 36);
            joinRadio.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            modeSelectorPanel.Controls.Add(joinRadio);

            Label nickLabel = new AdventureLabel();
            nickLabel.Text = "Nickname";
            nickLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            nickLabel.ForeColor = Color.FromArgb(80, 85, 95);
            nickLabel.AutoSize = true;
            nickLabel.Location = new Point(24, 247);
            mainPanel.Controls.Add(nickLabel);

            nickBox.Location = new Point(125, 243);
            nickBox.Size = new Size(390, 26);
            nickBox.Text = Program.RemoveUnsafeIniCharacters(nickname);
            nickBox.TextChanged += delegate { EnforceIniSafeText(nickBox); };
            mainPanel.Controls.Add(nickBox);

            // 0.10.12e12: CONNECTION now mirrors MODE directly on the main canvas.
            // The old empty GroupBox supplied an unnecessary border that crossed the
            // CONNECTION heading. Keeping the heading + selector as direct siblings
            // removes that border without introducing transparency or new painting.
            connectionHeading.Text = "CONNECTION";
            connectionHeading.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            connectionHeading.ForeColor = Color.FromArgb(95, 100, 110);
            connectionHeading.Location = new Point(24, 281);
            connectionHeading.AutoSize = true;
            mainPanel.Controls.Add(connectionHeading);

            connectionSelectorPanel.Location = new Point(20, 302);
            connectionSelectorPanel.Size = new Size(540, 48);
            connectionSelectorPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            mainPanel.Controls.Add(connectionSelectorPanel);

            traversalRadio.Text = "Traversal";
            traversalRadio.Appearance = System.Windows.Forms.Appearance.Button;
            traversalRadio.TextAlign = ContentAlignment.MiddleCenter;
            traversalRadio.FlatStyle = FlatStyle.Flat;
            traversalRadio.FlatAppearance.BorderSize = 0;
            traversalRadio.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            traversalRadio.Location = new Point(6, 6);
            traversalRadio.Size = new Size(260, 36);
            connectionSelectorPanel.Controls.Add(traversalRadio);

            directRadio.Text = "Direct IP";
            directRadio.Appearance = System.Windows.Forms.Appearance.Button;
            directRadio.TextAlign = ContentAlignment.MiddleCenter;
            directRadio.FlatStyle = FlatStyle.Flat;
            directRadio.FlatAppearance.BorderSize = 0;
            directRadio.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            directRadio.Location = new Point(274, 6);
            directRadio.Size = new Size(260, 36);
            directRadio.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            connectionSelectorPanel.Controls.Add(directRadio);

            publicHostGroup.RaisedSection = true;
            publicHostGroup.Text = "PUBLIC HOSTING";
            publicHostGroup.Location = new Point(20, 280);
            publicHostGroup.Size = new Size(540, 154);
            publicHostGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            mainPanel.Controls.Add(publicHostGroup);

            publicHostCheck.Text = "Show in Server Browser";
            publicHostCheck.AutoSize = true;
            publicHostCheck.Location = new Point(14, 23);
            // Let the label portion sit on the raised PUBLIC HOSTING card instead
            // of looking like a separate dark badge. This is a tiny native child
            // transparency only; no launcher/background rendering path is changed.
            publicHostCheck.BackColor = Color.Transparent;
            publicHostCheck.Tag = "BackdropTransparent";
            // A fresh launcher UI starts private regardless of stale Dolphin NetPlay
            // server-browser settings from prior manual sessions.
            publicHostCheck.Checked = false;
            publicHostCheck.CheckedChanged += delegate { UpdatePublicHostUi(); UpdateGameState(); };
            publicHostGroup.Controls.Add(publicHostCheck);

            Label publicNameLabel = new AdventureLabel();
            publicNameLabel.Text = "Session Name";
            publicNameLabel.Location = new Point(14, 58);
            publicNameLabel.AutoSize = true;
            publicHostGroup.Controls.Add(publicNameLabel);

            publicSessionNameBox.Location = new Point(112, 54);
            publicSessionNameBox.Size = new Size(205, 24);
            publicSessionNameBox.Text = "";
            publicSessionNameBox.TextChanged += delegate { EnforceIniSafeText(publicSessionNameBox); UpdateGameState(); };
            publicHostGroup.Controls.Add(publicSessionNameBox);

            Label publicRegionLabel = new AdventureLabel();
            publicRegionLabel.Text = "Region";
            publicRegionLabel.Location = new Point(330, 58);
            publicRegionLabel.AutoSize = true;
            publicHostGroup.Controls.Add(publicRegionLabel);

            publicRegionBox.Location = new Point(385, 54);
            publicRegionBox.Size = new Size(140, 26);
            publicRegionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            publicRegionBox.Items.AddRange(new object[]
            {
                "East Asia (EA)", "China (CN)", "Europe (EU)", "North America (NA)",
                "South America (SA)", "Oceania (OC)", "Africa (AF)"
            });
            string savedRegion = "NA";
            for (int i = 0; i < publicRegionBox.Items.Count; i++)
            {
                if (publicRegionBox.Items[i].ToString().EndsWith("(" + savedRegion + ")", StringComparison.OrdinalIgnoreCase))
                {
                    publicRegionBox.SelectedIndex = i;
                    break;
                }
            }
            if (publicRegionBox.SelectedIndex < 0) publicRegionBox.SelectedIndex = 3;
            publicRegionBox.SelectedIndexChanged += delegate { UpdateGameState(); };
            publicHostGroup.Controls.Add(publicRegionBox);

            Label publicPasswordLabel = new AdventureLabel();
            publicPasswordLabel.Text = "Password";
            publicPasswordLabel.Location = new Point(14, 96);
            publicPasswordLabel.AutoSize = true;
            publicHostGroup.Controls.Add(publicPasswordLabel);

            publicPasswordBox.Location = new Point(112, 92);
            publicPasswordBox.Size = new Size(205, 24);
            publicPasswordBox.UseSystemPasswordChar = true;
            publicPasswordBox.Text = "";
            publicPasswordBox.TextChanged += delegate { EnforceIniSafeText(publicPasswordBox); };
            publicHostGroup.Controls.Add(publicPasswordBox);

            Label publicPasswordHint = new AdventureLabel();
            publicPasswordHint.Text = "Optional • stored by Dolphin";
            publicPasswordHint.Location = new Point(330, 96);
            publicPasswordHint.Size = new Size(195, 20);
            publicPasswordHint.ForeColor = Color.DimGray;
            publicHostGroup.Controls.Add(publicPasswordHint);

            targetLabel.Location = new Point(24, 372);
            targetLabel.AutoSize = true;
            mainPanel.Controls.Add(targetLabel);

            targetBox.Location = new Point(125, 368);
            targetBox.Size = new Size(245, 24);
            targetBox.Font = new Font("Consolas", 10F);
            mainPanel.Controls.Add(targetBox);
            targetBox.TextChanged += delegate
            {
                EnforceIniSafeText(targetBox);
                if (directRadio.Checked)
                {
                    int desired = GetDirectAddressMaxLength(targetBox.Text);
                    if (targetBox.MaxLength != desired)
                        targetBox.MaxLength = desired;
                }
                UpdateGameState();
            };

            pasteButton.Text = "Paste";
            pasteButton.FlatStyle = FlatStyle.System;
            pasteButton.Location = new Point(380, 367);
            pasteButton.Size = new Size(75, 27);
            pasteButton.Click += delegate
            {
                try
                {
                    string clip = Clipboard.GetText().Trim();
                    int max = directRadio.Checked
                        ? GetDirectAddressMaxLength(clip)
                        : TraversalRoomCodeMaxLength;
                    targetBox.MaxLength = max;
                    targetBox.Text = ClampTargetText(clip, max);
                }
                catch { }

                UpdateGameState();
                if (!string.IsNullOrWhiteSpace(targetBox.Text) && goButton.Enabled)
                    goButton.Focus();
            };
            mainPanel.Controls.Add(pasteButton);

            portLabel.Text = "Port:";
            portLabel.Location = new Point(24, 414);
            portLabel.AutoSize = true;
            mainPanel.Controls.Add(portLabel);

            portBox.Location = new Point(125, 410);
            portBox.Size = new Size(95, 24);
            portBox.Minimum = 1;
            portBox.Maximum = 65535;
            portBox.Value = directPort;
            mainPanel.Controls.Add(portBox);
            EnableSoftRoundedEntry(nickBox);
            EnableSoftRoundedEntry(targetBox);
            EnableSoftRoundedEntry(portBox);
            EnableSoftRoundedEntry(publicSessionNameBox);
            EnableSoftRoundedEntry(publicRegionBox);
            EnableSoftRoundedEntry(publicPasswordBox);
            EnableSoftRoundedEntry(gameSearch);

            AdventureGroupBox dolphin = new AdventureGroupBox();
            dolphin.RaisedSection = true;
            dolphin.Text = "Dolphin";
            dolphin.Location = new Point(20, 456);
            dolphin.Size = new Size(540, 94);
            dolphin.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            mainPanel.Controls.Add(dolphin);

            Label versionLabel = new AdventureLabel();
            versionLabel.Text = version + "   •   Update track: " + updateTrack;
            versionLabel.AutoSize = true;
            versionLabel.BackColor = Color.Transparent;
            versionLabel.Location = new Point(18, 20);
            dolphin.Controls.Add(versionLabel);

            Label dolphinPathLabel = new AdventureLabel();
            string dolphinDirectory = dolphinPaths != null && !string.IsNullOrWhiteSpace(dolphinPaths.DolphinExe)
                ? Path.GetDirectoryName(dolphinPaths.DolphinExe)
                : "";
            dolphinPathLabel.Text = "Folder: " + dolphinDirectory;
            dolphinPathLabel.Location = new Point(18, 41);
            dolphinPathLabel.Size = new Size(504, 18);
            dolphinPathLabel.AutoEllipsis = true;
            dolphinPathLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            dolphinPathLabel.Font = new Font("Segoe UI", 8.5F);
            dolphinPathLabel.AutoSize = false;
            ToolTip dolphinPathTip = new ToolTip();
            dolphinPathTip.SetToolTip(dolphinPathLabel, dolphinDirectory);
            dolphin.Controls.Add(dolphinPathLabel);

            dolphinUpdateButton.Text = "Check for Update";
            dolphinUpdateButton.Location = new Point(18, 62);
            dolphinUpdateButton.Size = new Size(156, 27);
            dolphinUpdateButton.Click += delegate { if (CheckUpdateRequested != null) CheckUpdateRequested(this, EventArgs.Empty); };
            dolphin.Controls.Add(dolphinUpdateButton);

            dolphinOptionsButton.Text = "Options...";
            dolphinOptionsButton.Location = new Point(192, 62);
            dolphinOptionsButton.Size = new Size(156, 27);
            dolphinOptionsButton.Click += delegate { if (OptionsRequested != null) OptionsRequested(this, EventArgs.Empty); };
            dolphin.Controls.Add(dolphinOptionsButton);

            dolphinChangeButton.Text = "Change Dolphin...";
            dolphinChangeButton.Location = new Point(366, 62);
            dolphinChangeButton.Size = new Size(156, 27);
            dolphinChangeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            dolphinChangeButton.Click += delegate { if (ChangeDolphinRequested != null) ChangeDolphinRequested(this, EventArgs.Empty); };
            dolphin.Controls.Add(dolphinChangeButton);

            lastDolphinControllerControl = dolphinOptionsButton;

            controllerPromptBar.Location = new Point(18, 642);
            controllerPromptBar.Size = new Size(556, 34);
            controllerPromptBar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            controllerPromptBar.Visible = showControllerPrompts;
            controllerPromptBar.PromptStyle = controllerPromptStyle;
            controllerPromptBar.GamesButton = controllerGamesButton;
            controllerPromptBar.LibraryMode = false;
            mainPanel.Controls.Add(controllerPromptBar);

            goButton.Text = "▶  Host";
            goButton.Location = new Point(350, 594);
            goButton.Size = new Size(134, 42);
            goButton.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            goButton.UseVisualStyleBackColor = false;
            goButton.BackColor = Color.FromArgb(52, 112, 200);
            goButton.ForeColor = Color.White;
            goButton.FlatStyle = FlatStyle.Flat;
            goButton.FlatAppearance.BorderColor = Color.FromArgb(35, 80, 155);
            goButton.FlatAppearance.BorderSize = 2;
            goButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 130, 220);
            goButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 92, 170);
            goButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            goButton.DialogResult = DialogResult.OK;
            PaintEventHandler drawPrimaryAccent = delegate(object sender, PaintEventArgs e)
            {
                if (!goButton.Enabled ||
                    !IsPrimaryActionReady() ||
                    !AccentVisuals.Animated(settings))
                    return;

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(3, 3,
                    Math.Max(1, goButton.ClientSize.Width - 7),
                    Math.Max(1, goButton.ClientSize.Height - 7));

                using (GraphicsPath path = CreateRoundedRect(r, 7F))
                using (LinearGradientBrush brush = AccentVisuals.CreateGradient(r, 255))
                using (Pen glow = new Pen(Color.FromArgb(75, AccentVisuals.CurrentColor()), 5F))
                using (Pen border = new Pen(brush, 3F))
                {
                    glow.LineJoin = LineJoin.Round;
                    border.LineJoin = LineJoin.Round;
                    e.Graphics.DrawPath(glow, path);
                    e.Graphics.DrawPath(border, path);
                }
            };
            goButton.Paint += drawPrimaryAccent;
            AdventureButton adventureGoButton = goButton as AdventureButton;
            if (adventureGoButton != null)
                adventureGoButton.AdventureOverlayPaint = delegate(PaintEventArgs e) { drawPrimaryAccent(goButton, e); };
            mainPanel.Controls.Add(goButton);
            AcceptButton = goButton;

            // Animated accent is deliberately limited to active/selected UI: the primary
            // Host/Join action and selected library item. It is not a whole-app RGB theme.
            accentUiTimer.Interval = 50;
            accentUiTimer.Tick += delegate
            {
                if (!AccentVisuals.Animated(settings))
                    return;

                // Only touch the primary action's border while it is actually usable.
                // Setting FlatAppearance.BorderColor itself invalidates a WinForms button,
                // so 0.10.5d was accidentally repainting the animated accent even though
                // the explicit Invalidate() call was correctly gated.
                if (goButton.Enabled && IsPrimaryActionReady())
                {
                    // Assigning FlatAppearance.BorderColor already invalidates the button.
                    // Do not immediately invalidate it a second time on the same tick.
                    goButton.FlatAppearance.BorderColor = AccentVisuals.CurrentColor();
                }

                // Grid tiles are custom WinForms controls and animate cleanly, but walking
                // every tile while Games is hidden is pure background work. Only animate
                // the grid while that surface is actually visible and in grid mode.
                if (libraryPanel.Visible && libraryGridView && gameGrid.Visible &&
                    !string.IsNullOrWhiteSpace(selectedGridPath))
                    UpdateGridSelectionBorder();

                // IMPORTANT: do NOT continuously invalidate the native owner-drawn ListBox.
                // Repainting a Win32 ListBox at 20 FPS causes visible fluorescent-style flicker.
                // List view still gets the animated-gradient visual on selection, but it is
                // rendered as a stable gradient snapshot and only redraws on normal list events.
            };
            if (AccentVisuals.Animated(settings))
                accentUiTimer.Start();
            FormClosed += delegate { accentUiTimer.Stop(); };

            cancelButton.Text = "Cancel";
            cancelButton.Location = new Point(492, 594);
            cancelButton.Size = new Size(78, 42);
            cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancelButton.DialogResult = DialogResult.Cancel;
            mainPanel.Controls.Add(cancelButton);
            CancelButton = cancelButton;

            // The primary actions and controller prompts intentionally occupy separate
            // rows. The prompt strip gets the full content width, so narrow windows no
            // longer hide prompts behind Host/Join or Cancel.
            Action updateBottomLayout = delegate
            {
                int sideMargin = 18;
                controllerPromptBar.Left = sideMargin;
                controllerPromptBar.Width = Math.Max(300, mainPanel.ClientSize.Width - (sideMargin * 2));

                goButton.BringToFront();
                cancelButton.BringToFront();
                controllerPromptBar.BringToFront();
            };
            mainPanel.Resize += delegate { updateBottomLayout(); };
            updateBottomLayout();

            BuildLibraryPanel();

            traversalValue = ClampTargetText(roomCode, TraversalRoomCodeMaxLength);
            directValue = ClampTargetText(
                directIp, GetDirectAddressMaxLength(directIp));
            previousJoinConnection = lastWasDirect ? JoinConnection.Direct : JoinConnection.Traversal;

            hostRadio.CheckedChanged += delegate { UpdateMainMode(); };
            joinRadio.CheckedChanged += delegate { UpdateMainMode(); };
            traversalRadio.CheckedChanged += delegate { if (traversalRadio.Checked) SwitchJoinMode(JoinConnection.Traversal); };
            directRadio.CheckedChanged += delegate { if (directRadio.Checked) SwitchJoinMode(JoinConnection.Direct); };

            if (lastWasDirect) directRadio.Checked = true;
            else traversalRadio.Checked = true;

            if (startInJoinMode) joinRadio.Checked = true;
            else hostRadio.Checked = true;

            UpdateMainMode();
            UpdateGameState();

            if (!lastWasDirect)
            {
                try
                {
                    string clip = Clipboard.GetText().Trim();
                    if (Regex.IsMatch(clip, "^[A-Za-z0-9_-]{1,8}$"))
                    {
                        traversalValue = clip;
                        targetBox.Text = clip;
                    }
                }
                catch { }
            }

            Shown += delegate
            {
                ApplyMinimumClientArea();
                if (ClientSize.Height < 690)
                    ClientSize = new Size(Math.Max(600, ClientSize.Width), 690);

                if (startLibraryOpen)
                    SetLibraryVisible(true);
                else
                    gamesButton.Focus();
            };
        }

        public void ApplyMinimumClientArea()
        {
            // MinimumSize is an OUTER-window size. Our layout coordinates are client-area
            // coordinates, so convert the required 600x690 client area to the correct
            // outer size for the current DPI/theme/title-bar metrics.
            MinimumSize = SizeFromClientSize(new Size(600, 690));
        }

        public void SetSavedWindowSize(int savedWidth, int savedClientHeight)
        {
            ApplyMinimumClientArea();

            int clientHeight = Math.Max(690, savedClientHeight);

            // Keep the normal launcher at its intended compact width. The library
            // expands the window only while it is actually visible.
            Size outer = SizeFromClientSize(new Size(600, clientHeight));
            Size = new Size(Math.Max(MinimumSize.Width, outer.Width),
                            Math.Max(MinimumSize.Height, outer.Height));
            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);

            // The compact top-row buttons can receive their final size/theme after the
            // initial disabled-state paint. Re-run state styling and repaint them once
            // after the saved-window theme/font pass so Clear never starts as a clipped
            // or partially-painted sliver.
            UpdateGameState();
            clearGameButton.Invalidate();
            gamesButton.Invalidate();
            sessionsButton.Invalidate();
            clearGameButton.Update();
            gamesButton.Update();
            sessionsButton.Update();

        }

        public void ApplyThemeLogo()
        {
            try
            {
                string suffix = AppTheme.IsAdventure(settings) ? "adventure"
                    : (AppTheme.IsOled(settings) ? "oled"
                    : (AppTheme.IsGameCubeIndigo(settings) ? "indigo"
                    : (AppTheme.IsGameCubeSpice(settings) ? "spice"
                    : (AppTheme.IsDark(settings) ? "dark" : "light"))));
                string themed = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "DolphinNetPlayLauncher-icon-" + suffix + ".png");
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DolphinNetPlayLauncher-icon.png");
                string path = File.Exists(themed) ? themed : fallback;
                if (!File.Exists(path)) return;
                Image old = launcherLogo.Image;
                using (Image loaded = Image.FromFile(path))
                    launcherLogo.Image = new Bitmap(loaded);
                if (old != null) old.Dispose();
                launcherLogo.Invalidate();
            }
            catch { }
        }

        private void BuildLibraryPanel()
        {
            RecreateLibraryGridFonts();
            Disposed += delegate
            {
                try { if (libraryGridRegularFont != null) libraryGridRegularFont.Dispose(); } catch { }
                try { if (libraryGridBoldFont != null) libraryGridBoldFont.Dispose(); } catch { }
                libraryGridRegularFont = null;
                libraryGridBoldFont = null;

                foreach (Font retiredFont in retiredLibraryGridFonts)
                {
                    try { if (retiredFont != null) retiredFont.Dispose(); } catch { }
                }
                retiredLibraryGridFonts.Clear();
            };

            libraryPanel.Padding = new Padding(0);
            libraryPanel.BackColor = Color.FromArgb(248, 248, 248);
            libraryPanel.Visible = false;
            sessionsPanel.Padding = new Padding(0);
            sessionsPanel.BackColor = Color.FromArgb(248, 248, 248);
            sessionsPanel.Visible = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.Padding = new Padding(12);
            layout.Margin = new Padding(0);
            // Keep only the single large library structural layer transparent so the
            // animated canvas is exposed. Child rows/cards remain normal opaque controls
            // to avoid the recursive transparency/compositing regression from e4.
            layout.BackColor = Color.Transparent;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F)); // title / hide
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // search
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190F)); // selected cover preview
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F)); // selected game info
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // game list
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));  // spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F)); // bottom buttons
            libraryPanel.Controls.Add(layout);

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);
            layout.Controls.Add(header, 0, 0);

            Label title = new AdventureLabel();
            title.Text = "Games";
            title.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            title.Location = new Point(0, 3);
            title.AutoSize = true;
            header.Controls.Add(title);

            libraryColumnsBox.DropDownStyle = ComboBoxStyle.DropDownList;
            libraryColumnsBox.FlatStyle = FlatStyle.Flat;
            libraryColumnsBox.Items.AddRange(new object[] { "3 cols", "4 cols", "5 cols" });
            libraryColumnsBox.SelectedIndex = Math.Max(0, Math.Min(2, libraryGridColumns - 3));
            libraryColumnsBox.Size = new Size(72, 28);
            libraryColumnsBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryColumnsBox.TabStop = false;
            libraryColumnsBox.SelectedIndexChanged += delegate
            {
                libraryGridColumns = Math.Max(3, libraryColumnsBox.SelectedIndex + 3);
                if (settings != null) settings.LibraryGridColumns = libraryGridColumns;
                if (libraryPanel.Visible && libraryGridView)
                    ApplyLibraryPanelWidth();
            };
            header.Controls.Add(libraryColumnsBox);

            libraryGridButton.Text = "▦";
            libraryGridButton.Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold);
            libraryGridButton.Size = new Size(34, 28);
            libraryGridButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryGridButton.TabStop = false;
            libraryGridButton.Click += delegate
            {
                SetLibraryGridView(true);
                mousePrefersGrid = true;
                if (settings != null) settings.LibraryView = "Grid";
                ApplyLibraryPanelWidth();
            };
            libraryToolTip.SetToolTip(libraryGridButton, "Grid view");
            header.Controls.Add(libraryGridButton);

            libraryListButton.Text = "☰";
            libraryListButton.Font = new Font("Segoe UI Symbol", 10F, FontStyle.Bold);
            libraryListButton.Size = new Size(34, 28);
            libraryListButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryListButton.TabStop = false;
            libraryListButton.Click += delegate
            {
                SetLibraryGridView(false);
                mousePrefersGrid = false;
                if (settings != null) settings.LibraryView = "List";
                ApplyLibraryPanelWidth();
            };
            libraryToolTip.SetToolTip(libraryListButton, "List view");
            header.Controls.Add(libraryListButton);

            libraryHideButton.Text = "Hide";
            libraryHideButton.Size = new Size(70, 28);
            libraryHideButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryHideButton.Location = new Point(Math.Max(0, header.ClientSize.Width - 70), 0);
            libraryHideButton.TabStop = false;
            libraryHideButton.Click += delegate
            {
                SetLibraryVisible(false);
                gamesButton.Focus();
            };
            header.Controls.Add(libraryHideButton);
            header.Resize += delegate
            {
                libraryHideButton.Left = Math.Max(0, header.ClientSize.Width - libraryHideButton.Width);
                libraryListButton.Left = Math.Max(0, libraryHideButton.Left - libraryListButton.Width - 6);
                libraryGridButton.Left = Math.Max(0, libraryListButton.Left - libraryGridButton.Width - 3);
                libraryColumnsBox.Left = Math.Max(0, libraryGridButton.Left - libraryColumnsBox.Width - 6);
                libraryHideButton.Top = 0;
                libraryListButton.Top = 0;
                libraryGridButton.Top = 0;
                libraryColumnsBox.Top = 0;
            };

            Panel searchRow = new Panel();
            searchRow.Dock = DockStyle.Fill;
            searchRow.Margin = new Padding(0);
            layout.Controls.Add(searchRow, 0, 1);

            Label searchLabel = new AdventureLabel();
            searchLabel.Text = "Search:";
            searchLabel.Location = new Point(0, 7);
            searchLabel.AutoSize = true;
            searchRow.Controls.Add(searchLabel);

            gameSearch.Location = new Point(58, 3);
            gameSearch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gameSearch.Width = Math.Max(100, searchRow.ClientSize.Width - 58);
            gameSearch.TabStop = false;
            gameSearch.TextChanged += delegate { PopulateLibrary(); };
            searchRow.Controls.Add(gameSearch);
            searchRow.Resize += delegate
            {
                gameSearch.Width = Math.Max(100, searchRow.ClientSize.Width - 58);
            };

            bool darkLibrary = AppTheme.IsDark(settings);
            bool adventureLibrary = AppTheme.IsAdventure(settings);
            bool oledLibrary = AppTheme.IsOled(settings);
            bool cubeLibrary = AppTheme.IsGameCube(settings);

            ThemedDepthPanel preview = new ThemedDepthPanel();
            preview.Dock = DockStyle.Fill;
            preview.Margin = new Padding(0, 4, 0, 4);
            preview.Padding = new Padding(4, 4, 7, 7);
            preview.CornerRadius = 9F;
            preview.DepthPixels = 3;
            preview.AdventureStyle = adventureLibrary;
            preview.TintedThemeStyle = cubeLibrary;
            preview.DarkThemeStyle = darkLibrary && !adventureLibrary && !cubeLibrary;
            preview.BackColor = cubeLibrary ? AppTheme.Surface(settings)
                : (adventureLibrary ? Color.FromArgb(14, 34, 72)
                : (oledLibrary ? Color.FromArgb(2, 2, 4)
                : (darkLibrary ? Color.FromArgb(39, 42, 47) : Color.FromArgb(244, 246, 249))));
            layout.Controls.Add(preview, 0, 2);

            libraryCover.SizeMode = PictureBoxSizeMode.Zoom;
            libraryCover.Dock = DockStyle.Fill;
            libraryCover.Margin = new Padding(0);
            libraryCover.BackColor = AppTheme.ArtworkWell(settings);
            preview.Controls.Add(libraryCover);

            Panel coverInfo = new Panel();
            coverInfo.Dock = DockStyle.Fill;
            coverInfo.Margin = new Padding(0);
            layout.Controls.Add(coverInfo, 0, 3);

            libraryCoverTitle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            libraryCoverTitle.AutoEllipsis = true;
            libraryCoverTitle.Location = new Point(0, 2);
            libraryCoverTitle.Size = new Size(360, 20);
            libraryCoverTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            coverInfo.Controls.Add(libraryCoverTitle);

            libraryCoverMeta.ForeColor = Color.DimGray;
            libraryCoverMeta.AutoEllipsis = true;
            libraryCoverMeta.Location = new Point(0, 22);
            libraryCoverMeta.Size = new Size(360, 18);
            libraryCoverMeta.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            coverInfo.Controls.Add(libraryCoverMeta);

            Panel libraryContent = new Panel();
            libraryContent.Dock = DockStyle.Fill;
            libraryContent.Margin = new Padding(0);
            layout.Controls.Add(libraryContent, 0, 4);

            gameList.Dock = DockStyle.Fill;
            gameList.DrawMode = DrawMode.OwnerDrawFixed;
            gameList.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= gameList.Items.Count) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                bool dark = AppTheme.IsDark(settings);
                bool adventure = AppTheme.IsAdventure(settings);
                bool oled = AppTheme.IsOled(settings);
                bool cube = AppTheme.IsGameCube(settings);
                Color fore = adventure ? Color.FromArgb(248, 252, 255) : (selected ? Color.White : gameList.ForeColor);

                if (selected && AccentVisuals.Animated(settings))
                {
                    // ListBox is a native Win32 control; continuously animating owner-draw
                    // causes flicker. Use the same multi-color gradient, but as a stable
                    // snapshot that redraws only when the list normally needs painting.
                    using (LinearGradientBrush b = AccentVisuals.CreateGradient(e.Bounds, 255))
                        e.Graphics.FillRectangle(b, e.Bounds);
                }
                else
                {
                    Color back = selected
                        ? (cube ? AppTheme.ThemeSelected(settings)
                        : (adventure ? Color.FromArgb(62, 132, 196) : (oled ? Color.FromArgb(36, 82, 132) : (dark ? Color.FromArgb(64, 116, 177) : SystemColors.Highlight))))
                        : gameList.BackColor;
                    using (SolidBrush b = new SolidBrush(back))
                        e.Graphics.FillRectangle(b, e.Bounds);
                }

                Rectangle textBounds = new Rectangle(
                    e.Bounds.X + 3, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 6), e.Bounds.Height);
                TextFormatFlags listFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                if (AppTheme.IsAdventure(settings))
                {
                    string itemText = gameList.Items[e.Index].ToString();
                    Point[] shadowOffsets = new Point[]
                    {
                        new Point(-1, 0), new Point(1, 0),
                        new Point(0, -1), new Point(0, 1),
                        new Point(1, 1), new Point(2, 2)
                    };
                    foreach (Point off in shadowOffsets)
                    {
                        Rectangle shadowBounds = textBounds;
                        shadowBounds.Offset(off.X, off.Y);
                        TextRenderer.DrawText(e.Graphics, itemText,
                            gameList.Font, shadowBounds, Color.FromArgb(4, 8, 18), listFlags);
                    }
                }
                TextRenderer.DrawText(e.Graphics, gameList.Items[e.Index].ToString(),
                    gameList.Font, textBounds, fore, listFlags);
                e.DrawFocusRectangle();
            };
            gameList.Margin = new Padding(0);
            gameList.Font = new Font("Segoe UI", 10F);
            // OwnerDrawFixed does not automatically grow ItemHeight to match a larger font.
            // Without this, 10 pt Segoe UI gets painted into the old compact ListBox rows
            // and the titles visually overlap/scrunch together.
            gameList.ItemHeight = Math.Max(22, gameList.Font.Height + 6);
            gameList.IntegralHeight = false;
            gameList.HorizontalScrollbar = true;
            gameList.TabStop = false;
            gameList.DoubleClick += delegate
            {
                StageCurrentLibraryGame();
                CommitPendingLibraryGame();
            };
            gameList.MouseClick += delegate
            {
                LibraryItem item = gameList.SelectedItem as LibraryItem;
                selectedGridPath = item != null ? item.Path : null;
                UpdateGridSelectionBorder();
                StageCurrentLibraryGame();
            };
            // Loading full-size cover/banner artwork on every D-pad step can block the
            // WinForms UI thread long enough to make controller motion look sub-60 Hz.
            // Update the lightweight selection immediately, but debounce the expensive
            // preview artwork until navigation has been idle briefly.
            libraryPreviewTimer.Interval = 90;
            libraryPreviewTimer.Tick += delegate
            {
                libraryPreviewTimer.Stop();
                if (!IsDisposed && libraryPanel.Visible)
                    UpdateLibraryPreview();
            };
            FormClosed += delegate
            {
                try { libraryPreviewTimer.Stop(); libraryPreviewTimer.Dispose(); } catch { }
            };
            gameList.SelectedIndexChanged += delegate { QueueLibraryPreviewUpdate(); };
            libraryContent.Controls.Add(gameList);

            gameGrid.Dock = DockStyle.Fill;
            gameGrid.AutoScroll = true;
            gameGrid.WrapContents = true;
            gameGrid.Padding = new Padding(2);
            gameGrid.BackColor = Color.Transparent;
            libraryContent.Controls.Add(gameGrid);
            gameGrid.BringToFront();

            libraryLoadingLabel.Text = "Loading game library...";
            libraryLoadingLabel.Dock = DockStyle.Fill;
            libraryLoadingLabel.TextAlign = ContentAlignment.MiddleCenter;
            libraryLoadingLabel.Font = new Font("Segoe UI", 10F, FontStyle.Italic);
            libraryLoadingLabel.Visible = false;
            libraryContent.Controls.Add(libraryLoadingLabel);

            libraryEmptyPanel.Dock = DockStyle.Fill;
            libraryEmptyPanel.Visible = false;
            libraryEmptyPanel.Padding = new Padding(14);
            libraryEmptyPanel.BackColor = AppTheme.IsGameCube(settings)
                ? AppTheme.Field(settings)
                : (AppTheme.IsDark(settings) ? Color.FromArgb(34, 36, 40) : Color.FromArgb(248, 248, 248));

            TableLayoutPanel emptyLayout = new TableLayoutPanel();
            emptyLayout.Dock = DockStyle.Fill;
            emptyLayout.ColumnCount = 1;
            emptyLayout.RowCount = 3;
            emptyLayout.Margin = new Padding(0);
            emptyLayout.Padding = new Padding(0);
            emptyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            libraryEmptyPanel.Controls.Add(emptyLayout);

            libraryEmptyLabel.AutoSize = false;
            libraryEmptyLabel.Dock = DockStyle.Fill;
            libraryEmptyLabel.TextAlign = ContentAlignment.BottomCenter;
            libraryEmptyLabel.Font = new Font("Segoe UI", 10F);
            libraryEmptyLabel.Margin = new Padding(8, 0, 8, 8);
            libraryEmptyLabel.Text =
                "No games were found in Dolphin's configured game folders.";
            emptyLayout.Controls.Add(libraryEmptyLabel, 0, 0);

            Label libraryEmptyHelp = new AdventureLabel();
            libraryEmptyHelp.AutoSize = false;
            libraryEmptyHelp.Dock = DockStyle.Fill;
            libraryEmptyHelp.TextAlign = ContentAlignment.TopCenter;
            libraryEmptyHelp.Margin = new Padding(8, 0, 8, 8);
            libraryEmptyHelp.Text =
                "Set up your GameCube/Wii library in Dolphin, then reopen Dolphin NetPlay Launcher.";
            emptyLayout.Controls.Add(libraryEmptyHelp, 0, 1);

            Panel setupButtonRow = new Panel();
            setupButtonRow.Dock = DockStyle.Fill;
            setupButtonRow.Margin = new Padding(0);
            emptyLayout.Controls.Add(setupButtonRow, 0, 2);

            librarySetupButton.Text = "Open Dolphin to Set Up Library";
            librarySetupButton.Size = new Size(220, 36);
            librarySetupButton.Anchor = AnchorStyles.None;
            librarySetupButton.Click += delegate
            {
                try
                {
                    Process.Start(dolphinPaths.DolphinExe);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Could not start Dolphin.\n\n" + ex.Message,
                        "Dolphin NetPlay Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            };
            setupButtonRow.Controls.Add(librarySetupButton);
            setupButtonRow.Resize += delegate
            {
                librarySetupButton.Left = Math.Max(0,
                    (setupButtonRow.ClientSize.Width - librarySetupButton.Width) / 2);
                librarySetupButton.Top = Math.Max(0,
                    (setupButtonRow.ClientSize.Height - librarySetupButton.Height) / 2);
            };

            libraryContent.Controls.Add(libraryEmptyPanel);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.Margin = new Padding(0);
            layout.Controls.Add(footer, 0, 6);

            libraryBrowseButton.Text = "Browse...";
            libraryBrowseButton.Size = new Size(92, 30);
            libraryBrowseButton.Location = new Point(0, 4);
            libraryBrowseButton.TabStop = false;
            libraryBrowseButton.Click += delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Choose a GameCube or Wii game";
                    dlg.Filter = "Dolphin game files|*.iso;*.gcm;*.gcz;*.rvz;*.wia;*.wbfs;*.ciso;*.dol;*.elf|All files|*.*";
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        selectedGamePath = dlg.FileName;
                        gameLabel.Text = Path.GetFileNameWithoutExtension(selectedGamePath);
                        gameMetaLabel.Text = "";
                        UpdateGameState();
                        SetLibraryVisible(false);
                    }
                }
            };
            footer.Controls.Add(libraryBrowseButton);

            libraryUseButton.Text = "Use Game";
            libraryUseButton.Size = new Size(100, 30);
            libraryUseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryUseButton.Location = new Point(Math.Max(0, footer.ClientSize.Width - 100), 4);
            libraryUseButton.TabStop = false;
            libraryUseButton.Enabled = false;
            libraryUseButton.Click += delegate { CommitPendingLibraryGame(); };
            footer.Controls.Add(libraryUseButton);
            footer.Resize += delegate
            {
                libraryUseButton.Left = Math.Max(0, footer.ClientSize.Width - libraryUseButton.Width);
            };

            if (libraryLoaded)
                PopulateLibrary();
            else
                SetLibraryGridView(libraryGridView);
        }

        private void EnsureLibraryLoaded()
        {
            if (libraryLoaded)
            {
                BeginLibraryRefreshInBackground();
                return;
            }

            // Persistent cache makes the library appear immediately on normal launches.
            // It is keyed to Dolphin's configured ISO roots. A background rescan then
            // refreshes the cache so games added/removed outside the launcher are picked up.
            List<string> cached = Program.LoadCachedDolphinGames(dolphinPaths);
            if (cached != null)
            {
                allGames.Clear();
                allGames.AddRange(cached);
                libraryLoaded = true;
                libraryLoading = false;
                libraryLoadingLabel.Visible = false;
                PopulateLibrary();
                BeginLibraryRefreshInBackground();
                return;
            }

            // First-ever load (or changed Dolphin game paths): never freeze the UI.
            // Open the panel immediately and scan in the background.
            libraryLoading = true;
            libraryEmptyPanel.Visible = false;
            libraryLoadingLabel.Visible = true;
            libraryLoadingLabel.BringToFront();
            BeginLibraryRefreshInBackground();
        }

        private void BeginLibraryRefreshInBackground()
        {
            if (libraryRefreshStarted)
                return;

            libraryRefreshStarted = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> discovered = null;
                try
                {
                    discovered = Program.DiscoverDolphinGames(dolphinPaths);
                    Program.SaveCachedDolphinGames(dolphinPaths, discovered);
                }
                catch
                {
                    discovered = null;
                }

                if (IsDisposed || !IsHandleCreated)
                    return;

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;

                        if (discovered != null)
                        {
                            bool changed = allGames.Count != discovered.Count;
                            if (!changed)
                            {
                                for (int i = 0; i < allGames.Count; i++)
                                {
                                    if (!string.Equals(allGames[i], discovered[i], StringComparison.OrdinalIgnoreCase))
                                    {
                                        changed = true;
                                        break;
                                    }
                                }
                            }

                            libraryLoaded = true;
                            libraryLoading = false;
                            libraryLoadingLabel.Visible = false;

                            if (changed)
                            {
                                allGames.Clear();
                                allGames.AddRange(discovered);
                                PopulateLibrary();
                            }
                            else
                            {
                                UpdateEmptyLibraryState();
                            }
                        }
                        else
                        {
                            libraryLoading = false;
                            libraryLoadingLabel.Text = "Could not load game library.";
                            libraryLoadingLabel.Visible = !libraryLoaded;
                            if (libraryLoadingLabel.Visible)
                                libraryLoadingLabel.BringToFront();
                        }
                    });
                }
                catch
                {
                    // Form closed during the background refresh.
                }
            });
        }

        private void PopulateLibrary()
        {
            string q = gameSearch.Text.Trim();
            gameList.BeginUpdate();
            gameList.Items.Clear();
            foreach (string path in allGames)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (q.Length == 0 || name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    gameList.Items.Add(new LibraryItem(name, path));
            }
            gameList.EndUpdate();

            if (gameList.Items.Count > 0 && gameList.SelectedIndex < 0)
                gameList.SelectedIndex = 0;

            RebuildLibraryGrid();
            SetLibraryGridView(libraryGridView);
            UpdateLibraryPreview();
            UpdateEmptyLibraryState();
        }

        private void UpdateEmptyLibraryState()
        {
            bool showEmpty = libraryLoaded && !libraryLoading && allGames.Count == 0;

            libraryEmptyPanel.Visible = showEmpty;
            gameList.Visible = !showEmpty && !libraryGridView;
            gameGrid.Visible = !showEmpty && libraryGridView;

            if (showEmpty)
                libraryEmptyPanel.BringToFront();
        }

        private void SetLibraryGridView(bool grid)
        {
            libraryGridView = grid;
            bool emptyLibrary = libraryLoaded && !libraryLoading && allGames.Count == 0;
            gameGrid.Visible = grid && !emptyLibrary;
            gameList.Visible = !grid && !emptyLibrary;
            libraryColumnsBox.Visible = grid;

            bool dark = AppTheme.IsDark(settings);
            bool adventure = AppTheme.IsAdventure(settings);
            bool oled = AppTheme.IsOled(settings);
            bool cube = AppTheme.IsGameCube(settings);
            Color selectedBack = cube ? AppTheme.ThemeSelected(settings)
                : (adventure ? Color.FromArgb(54, 126, 191) : (oled ? Color.FromArgb(30, 68, 110) : (dark ? Color.FromArgb(43, 73, 108) : Color.FromArgb(220, 233, 249))));
            Color idleBack = cube ? AppTheme.ThemeButton(settings)
                : (adventure ? Color.FromArgb(38, 73, 130) : (oled ? Color.FromArgb(12, 13, 16) : (dark ? Color.FromArgb(52, 56, 62) : SystemColors.Control)));
            Color selectedFore = (dark || adventure) ? Color.FromArgb(235, 242, 252) : SystemColors.ControlText;
            Color idleFore = (dark || adventure) ? Color.FromArgb(220, 224, 230) : SystemColors.ControlText;

            libraryGridButton.UseVisualStyleBackColor = !(dark || adventure || cube);
            libraryListButton.UseVisualStyleBackColor = !(dark || adventure || cube);
            libraryGridButton.FlatStyle = (dark || adventure || cube) ? FlatStyle.Flat : FlatStyle.Standard;
            libraryListButton.FlatStyle = (dark || adventure || cube) ? FlatStyle.Flat : FlatStyle.Standard;

            libraryGridButton.BackColor = grid ? selectedBack : idleBack;
            libraryListButton.BackColor = !grid ? selectedBack : idleBack;
            libraryGridButton.ForeColor = grid ? selectedFore : idleFore;
            libraryListButton.ForeColor = !grid ? selectedFore : idleFore;

            if (dark || adventure || cube)
            {
                Color viewBorder = cube ? AppTheme.Border(settings) : (adventure ? Color.FromArgb(86, 137, 203) : (oled ? Color.FromArgb(52, 56, 64) : Color.FromArgb(78, 83, 91)));
                libraryGridButton.FlatAppearance.BorderColor = viewBorder;
                libraryListButton.FlatAppearance.BorderColor = viewBorder;
            }

            if (settings != null)
                settings.LibraryView = grid ? "Grid" : "List";

            if (grid) gameGrid.BringToFront(); else gameList.BringToFront();

            if (libraryLoading && libraryLoadingLabel.Visible)
                libraryLoadingLabel.BringToFront();
        }

        private void RebuildLibraryGrid()
        {
            gameGrid.SuspendLayout();

            // A rebuild replaces the tile controls. Dispose the previous controls so
            // their artwork bitmaps and native handles are released immediately rather
            // than waiting for a later GC/finalizer pass.
            Control[] oldTiles = new Control[gameGrid.Controls.Count];
            gameGrid.Controls.CopyTo(oldTiles, 0);
            gameGrid.Controls.Clear();
            foreach (Control oldTile in oldTiles)
            {
                try { oldTile.Dispose(); } catch { }
            }
            libraryGridTiles.Clear();
            lastStyledGridPath = null;

            foreach (object obj in gameList.Items)
            {
                LibraryItem item = obj as LibraryItem;
                if (item == null) continue;

                ThemedDepthPanel tile = new ThemedDepthPanel();
                tile.Size = new Size(104, 178);
                tile.Margin = new Padding(1);
                tile.Padding = new Padding(0);
                tile.CornerRadius = 7F;
                tile.DepthPixels = 2;
                tile.DrawInnerPlate = true;
                tile.InnerPlateBounds = new Rectangle(3, 129, 98, 45);
                bool darkTile = AppTheme.IsDark(settings);
                bool adventureTile = AppTheme.IsAdventure(settings);
                bool oledTile = AppTheme.IsOled(settings);
                bool cubeTile = AppTheme.IsGameCube(settings);
                tile.AdventureStyle = adventureTile;
                tile.TintedThemeStyle = cubeTile;
                tile.DarkThemeStyle = darkTile && !adventureTile && !cubeTile;
                tile.BackColor = cubeTile ? AppTheme.Surface(settings)
                    : (adventureTile ? Color.FromArgb(24, 52, 100)
                    : (oledTile ? Color.FromArgb(10, 10, 12) : (darkTile ? Color.FromArgb(52, 55, 61) : Color.FromArgb(238, 238, 238))));
                tile.InnerPlateBackColor = cubeTile ? AdventurePaint.Lighten(AppTheme.Surface(settings), 8)
                    : (adventureTile ? Color.FromArgb(28, 61, 112)
                    : (oledTile ? Color.FromArgb(15, 15, 18) : (darkTile ? Color.FromArgb(46, 49, 54) : Color.FromArgb(248, 248, 248))));
                tile.Cursor = Cursors.Hand;
                tile.Tag = item;

                PictureBox cover = new PictureBox();
                cover.Location = new Point(3, 3);
                cover.Size = new Size(98, 124);
                cover.SizeMode = PictureBoxSizeMode.Zoom;
                cover.BackColor = AppTheme.ArtworkWell(settings);
                cover.Tag = item;
                tile.Controls.Add(cover);

                Label label = new AdventureLabel();
                label.Text = item.Name;
                label.Location = new Point(5, 132);
                label.Size = new Size(94, 39);
                label.TextAlign = ContentAlignment.TopCenter;
                label.AutoEllipsis = true;
                label.Font = libraryGridRegularFont ?? AppFonts.Create(settings, 8.25F, FontStyle.Regular);
                label.BackColor = cubeTile ? AdventurePaint.Lighten(AppTheme.Surface(settings), 8)
                    : (adventureTile ? Color.FromArgb(18, 42, 84)
                    : (oledTile ? Color.FromArgb(15, 15, 18) : (darkTile ? Color.FromArgb(46, 49, 54) : Color.FromArgb(248, 248, 248))));
                label.ForeColor = cubeTile ? AppTheme.Fore(settings)
                    : (adventureTile ? Color.FromArgb(246, 250, 255) : (darkTile ? Color.FromArgb(235, 237, 240) : Color.FromArgb(45, 45, 45)));
                tile.Controls.Add(label);

                GameInfo info = GetLibraryGameInfo(item.Path);
                if (info != null && !string.IsNullOrWhiteSpace(info.Title))
                    label.Text = info.Title;

                string cp = info != null ? FindDolphinCover(item.Path, info.GameId) : null;
                if (!string.IsNullOrWhiteSpace(cp) && File.Exists(cp))
                {
                    try { using (Image im = Image.FromFile(cp)) cover.Image = new Bitmap(im); }
                    catch { }
                }

                // If this Dolphin install has no downloaded GameCovers, reuse the same
                // native banner Dolphin displays in its own game list.
                if (cover.Image == null)
                {
                    try
                    {
                        using (Image banner = Program.TryLoadBannerFromGameListCache(dolphinPaths, item.Path))
                        {
                            if (banner != null)
                                cover.Image = CreateBannerCard(banner, cover.Width, cover.Height);
                        }
                    }
                    catch { }
                }

                if (cover.Image == null)
                    cover.Image = CreateFallbackCover(label.Text);

                cover.Disposed += delegate
                {
                    if (cover.Image != null)
                    {
                        Image oldImage = cover.Image;
                        cover.Image = null;
                        oldImage.Dispose();
                    }
                };

                EventHandler choose = delegate
                {
                    for (int n = 0; n < gameList.Items.Count; n++)
                    {
                        LibraryItem li = gameList.Items[n] as LibraryItem;
                        if (li != null && string.Equals(li.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            gameList.SelectedIndex = n;
                            break;
                        }
                    }

                    selectedGridPath = item.Path;
                    UpdateGridSelectionBorder();
                    StageCurrentLibraryGame();
                };

                EventHandler chooseAndCommit = delegate
                {
                    choose(null, EventArgs.Empty);
                    CommitPendingLibraryGame();
                };

                tile.Click += choose; cover.Click += choose; label.Click += choose;
                tile.DoubleClick += chooseAndCommit; cover.DoubleClick += chooseAndCommit; label.DoubleClick += chooseAndCommit;
                gameGrid.Controls.Add(tile);
                libraryGridTiles[item.Path] = tile;
            }
            gameGrid.ResumeLayout();
            UpdateGridSelectionBorder(true);
            FlushLibraryMetadataCache();
        }

        private Image CreateFallbackCover(string title)
        {
            Bitmap bmp = new Bitmap(196, 248);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(226, 231, 238));

                using (SolidBrush band = new SolidBrush(Color.FromArgb(205, 214, 226)))
                    g.FillRectangle(band, 0, 0, bmp.Width, 42);

                using (Font small = new Font("Segoe UI", 12F, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(82, 94, 110)))
                {
                    string caption = "NO COVER";
                    SizeF sz = g.MeasureString(caption, small);
                    g.DrawString(caption, small, b, (bmp.Width - sz.Width) / 2F, 10F);
                }

                string display = string.IsNullOrWhiteSpace(title) ? "Game" : title.Trim();
                if (display.Length > 32) display = display.Substring(0, 29) + "...";

                using (Font titleFont = new Font("Segoe UI", 17F, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(65, 72, 82)))
                {
                    RectangleF area = new RectangleF(15, 68, bmp.Width - 30, 145);
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisWord;
                    g.DrawString(display, titleFont, b, area, sf);
                    sf.Dispose();
                }

                using (Pen p = new Pen(Color.FromArgb(185, 194, 207), 3F))
                    g.DrawRectangle(p, 1, 1, bmp.Width - 3, bmp.Height - 3);
            }
            return bmp;
        }

        private void UpdateGridSelectionBorder()
        {
            UpdateGridSelectionBorder(false);
        }

        private void UpdateGridSelectionBorder(bool forceAll)
        {
            // RC31: selection changes and the 20 FPS animated accent no longer restyle
            // every game tile. Only the previously-selected tile and current tile can
            // change during ordinary navigation; a full pass is reserved for rebuilds
            // and explicit theme refreshes.
            if (forceAll)
            {
                foreach (Control control in gameGrid.Controls)
                {
                    LibraryItem item = control.Tag as LibraryItem;
                    bool selected = item != null &&
                        !string.IsNullOrWhiteSpace(selectedGridPath) &&
                        string.Equals(item.Path, selectedGridPath, StringComparison.OrdinalIgnoreCase);
                    ApplyGridTileSelectionStyle(control, selected);
                }
                lastStyledGridPath = selectedGridPath;
                return;
            }

            if (!string.IsNullOrWhiteSpace(lastStyledGridPath) &&
                !string.Equals(lastStyledGridPath, selectedGridPath, StringComparison.OrdinalIgnoreCase))
            {
                Control previous;
                if (libraryGridTiles.TryGetValue(lastStyledGridPath, out previous))
                    ApplyGridTileSelectionStyle(previous, false);
            }

            if (!string.IsNullOrWhiteSpace(selectedGridPath))
            {
                Control current;
                if (libraryGridTiles.TryGetValue(selectedGridPath, out current))
                    ApplyGridTileSelectionStyle(current, true);
            }

            lastStyledGridPath = selectedGridPath;
        }

        private void RecreateLibraryGridFonts()
        {
            Font oldRegular = libraryGridRegularFont;
            Font oldBold = libraryGridBoldFont;

            libraryGridRegularFont = AppFonts.Create(settings, 8.25F, FontStyle.Regular);
            libraryGridBoldFont = AppFonts.Create(settings, 8.25F, FontStyle.Bold);

            // Do not dispose the old shared fonts until every existing title label has
            // been moved to the new pair by the caller. BuildLibraryPanel has no old
            // tile labels yet, so disposing immediately is safe there.
            if (gameGrid.Controls.Count == 0)
            {
                try { if (oldRegular != null) oldRegular.Dispose(); } catch { }
                try { if (oldBold != null) oldBold.Dispose(); } catch { }
            }
        }

        private void DisposePreviousLibraryGridFonts(Font oldRegular, Font oldBold)
        {
            // RC39: despite the historical method name, do not dispose replaced shared
            // title fonts while the form is alive. WinForms can have a WM_PAINT queued
            // for an AdventureLabel that still observes the old Font object; disposing
            // its native GDI handle first can make Font.Height/GetHeight throw
            // ArgumentException ("Parameter is not valid"). Theme changes are rare, so
            // retaining at most a few retired fonts until form disposal is both safer and
            // negligible compared with the old per-navigation Font allocation problem.
            RetireLibraryGridFont(oldRegular);
            RetireLibraryGridFont(oldBold);
        }

        private void RetireLibraryGridFont(Font font)
        {
            if (font == null ||
                object.ReferenceEquals(font, libraryGridRegularFont) ||
                object.ReferenceEquals(font, libraryGridBoldFont))
                return;

            foreach (Font existing in retiredLibraryGridFonts)
            {
                if (object.ReferenceEquals(existing, font))
                    return;
            }
            retiredLibraryGridFonts.Add(font);
        }

        private void ApplyGridTileSelectionStyle(Control control, bool selected)
        {
            Panel tile = control as Panel;
            if (tile == null || tile.Controls.Count < 1)
                return;

            Label titleLabel = null;
            foreach (Control child in tile.Controls)
            {
                titleLabel = child as Label;
                if (titleLabel != null) break;
            }

            bool dark = AppTheme.IsDark(settings);
            bool adventure = AppTheme.IsAdventure(settings);
            bool cube = AppTheme.IsGameCube(settings);
            Color animatedAccent = AccentVisuals.CurrentColor();
            Color idleTileBack = cube ? AppTheme.Surface(settings)
                : (adventure ? Color.FromArgb(24, 52, 100)
                : (dark ? Color.FromArgb(52, 55, 61) : Color.FromArgb(238, 238, 238)));
            Color selectedTileBack = cube ? AppTheme.ThemeSelected(settings)
                : (adventure ? Color.FromArgb(42, 91, 151)
                : (dark ? Color.FromArgb(64, 116, 177) : SystemColors.Highlight));
            tile.BackColor = selected
                ? (AccentVisuals.Animated(settings) ? animatedAccent : selectedTileBack)
                : idleTileBack;

            ThemedDepthPanel depthTile = tile as ThemedDepthPanel;
            if (depthTile != null)
            {
                depthTile.AdventureStyle = adventure;
                depthTile.TintedThemeStyle = cube;
                depthTile.DarkThemeStyle = dark && !adventure && !cube;
            }

            if (titleLabel != null)
            {
                Color idleTitleBack = cube ? AdventurePaint.Lighten(AppTheme.Surface(settings), 8)
                    : (adventure ? Color.FromArgb(28, 61, 112)
                    : (dark ? Color.FromArgb(46, 49, 54) : Color.FromArgb(248, 248, 248)));
                Color selectedTitleBack = cube ? AdventurePaint.Lighten(AppTheme.ThemeSelected(settings), 8)
                    : (adventure ? Color.FromArgb(38, 82, 142)
                    : (dark ? Color.FromArgb(43, 73, 108) : Color.FromArgb(229, 239, 252)));
                titleLabel.BackColor = selected ? selectedTitleBack : idleTitleBack;
                titleLabel.ForeColor = selected
                    ? (AccentVisuals.Animated(settings)
                        ? (dark || adventure ? Color.White : animatedAccent)
                        : (dark || adventure ? Color.FromArgb(245, 250, 255) : SystemColors.Highlight))
                    : (dark || adventure ? Color.FromArgb(235, 242, 252) : Color.FromArgb(45, 45, 45));

                if (depthTile != null)
                {
                    depthTile.InnerPlateBackColor = titleLabel.BackColor;
                    depthTile.Invalidate();
                }

                Font desiredFont = selected ? libraryGridBoldFont : libraryGridRegularFont;
                if (desiredFont != null && !object.ReferenceEquals(titleLabel.Font, desiredFont))
                    titleLabel.Font = desiredFont;
            }
        }

        private void EnsureEmbeddedSessionsBrowser()
        {
            if (embeddedSessionsBrowser != null && !embeddedSessionsBrowser.IsDisposed)
                return;

            string indexServer = Program.ReadIni(
                dolphinPaths != null ? dolphinPaths.DolphinIni : "",
                "NetPlay",
                "IndexServer",
                "https://lobby.dolphin-emu.org");

            embeddedSessionsBrowser = new PublicSessionsForm(
                settings, indexServer, dolphinVersion, true);
            embeddedSessionsBrowser.TopLevel = false;
            embeddedSessionsBrowser.FormBorderStyle = FormBorderStyle.None;
            embeddedSessionsBrowser.Dock = DockStyle.Fill;
            embeddedSessionsBrowser.SessionChosen += delegate { UseEmbeddedSessionForJoin(); };
            embeddedSessionsBrowser.HideRequested += delegate { SetSessionsVisible(false, false); sessionsButton.Focus(); };
            sessionsPanel.Controls.Clear();
            sessionsPanel.Controls.Add(embeddedSessionsBrowser);
            embeddedSessionsBrowser.Show();
        }

        private void UseEmbeddedSessionForJoin()
        {
            if (embeddedSessionsBrowser == null ||
                embeddedSessionsBrowser.SelectedSession == null ||
                string.IsNullOrWhiteSpace(embeddedSessionsBrowser.ResolvedServerId))
                return;

            PublicNetPlaySession session = embeddedSessionsBrowser.SelectedSession;
            joinRadio.Checked = true;

            if (string.Equals(session.Method, "traversal", StringComparison.OrdinalIgnoreCase))
            {
                traversalRadio.Checked = true;
                targetBox.Text = embeddedSessionsBrowser.ResolvedServerId.Trim();
            }
            else
            {
                directRadio.Checked = true;
                targetBox.Text = embeddedSessionsBrowser.ResolvedServerId.Trim();
                if (session.Port >= 1 && session.Port <= 65535)
                    portBox.Value = session.Port;
            }

            UpdateMainMode();
            SetSessionsVisible(false);
            goButton.Focus();
            UiSoundManager.PlayConfirm(settings);
        }

        private void ToggleSessionsPanel()
        {
            SetSessionsVisible(!sessionsVisible);
            if (sessionsVisible && embeddedSessionsBrowser != null)
                embeddedSessionsBrowser.FocusSessionsFromController();
        }

        public void ToggleSessionsFromController()
        {
            ToggleSessionsPanel();
            if (!sessionsVisible && sessionsButton.Visible && sessionsButton.Enabled)
                sessionsButton.Focus();
            NotifyControllerFocusSettled();
        }

        private void SetSessionsVisible(bool visible, bool playPanelSound = true)
        {
            if (sessionsVisible == visible) return;

            const int baseClientWidth = 600;
            const int baseClientHeight = 690;
            const int panelWidth = 470;
            int clientHeight = Math.Max(baseClientHeight, ClientSize.Height);

            // 0.10.12e11: restore the simple 0.10.12b-era side-panel behavior.
            // Do the geometry once while layout is suspended; do not hide content,
            // snapshot it, fade it, or run a second transition state machine.
            SuspendLayout();
            rootLayout.SuspendLayout();
            sessionsPanel.SuspendLayout();
            try
            {
                if (visible)
                {
                    if (libraryPanel.Visible)
                    {
                        sidePanelSwapInProgress = true;
                        try { SetLibraryVisible(false); }
                        finally { sidePanelSwapInProgress = false; }
                    }

                    EnsureEmbeddedSessionsBrowser();
                    rootLayout.ColumnStyles[1].SizeType = SizeType.Absolute;
                    rootLayout.ColumnStyles[1].Width = panelWidth;
                    MinimumSize = SizeFromClientSize(new Size(baseClientWidth + panelWidth, baseClientHeight));

                    Size desiredOuter = SizeFromClientSize(new Size(baseClientWidth + panelWidth, clientHeight));
                    Rectangle work = Screen.FromControl(this).WorkingArea;
                    Width = Math.Min(work.Width, Math.Max(MinimumSize.Width, desiredOuter.Width));
                    if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
                    if (Left < work.Left) Left = work.Left;

                    sessionsPanel.Visible = true;
                    sessionsVisible = true;
                    controllerPromptBar.SessionsMode = true;
                    controllerPromptBar.Invalidate();
                    embeddedSessionsBrowser.RefreshSessions();
                    if (playPanelSound) UiSoundManager.PlaySessionsOpen(settings);
                    embeddedSessionsBrowser.FocusSessionsFromController();
                }
                else
                {
                    if (playPanelSound && !sidePanelSwapInProgress)
                        UiSoundManager.PlaySessionsClose(settings);

                    sessionsPanel.Visible = false;
                    sessionsVisible = false;
                    controllerPromptBar.SessionsMode = false;
                    controllerPromptBar.Invalidate();

                    if (!sidePanelSwapInProgress)
                    {
                        rootLayout.ColumnStyles[1].Width = 0F;
                        MinimumSize = SizeFromClientSize(new Size(baseClientWidth, baseClientHeight));
                        Size desiredOuter = SizeFromClientSize(new Size(baseClientWidth, clientHeight));
                        Size = new Size(Math.Max(MinimumSize.Width, desiredOuter.Width),
                                        Math.Max(MinimumSize.Height, desiredOuter.Height));
                    }
                }
            }
            finally
            {
                sessionsPanel.ResumeLayout(false);
                rootLayout.ResumeLayout(true);
                ResumeLayout(true);
            }
        }

        private static void ApplySoftRoundedRegion(Control control, int radius)
        {
            if (control == null || control.Width < 4 || control.Height < 4) return;
            Rectangle r = new Rectangle(0, 0, control.Width, control.Height);
            using (GraphicsPath path = CreateRoundedRect(r, Math.Max(2, radius)))
            {
                Region old = control.Region;
                control.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        internal static void EnableSoftRoundedEntry(Control control)
        {
            if (control == null) return;
            ApplySoftRoundedRegion(control, 7);
            control.Resize += delegate { ApplySoftRoundedRegion(control, 7); };
        }

        private void ClearSelectedGame()
        {
            selectedGamePath = null;
            pendingLibraryGamePath = null;
            selectedGridPath = null;
            UpdateGridSelectionBorder();

            gameLabel.Text = "No game selected";
            gameMetaLabel.Text = "Choose a game to host";
            clearGameButton.Enabled = false;

            // Remove the selected-game banner and restore the normal text position.
            Image old = gameBanner.Image;
            gameBanner.Image = null;
            if (old != null) old.Dispose();
            gameBanner.Visible = false;
            gameLabel.Location = new Point(100, 62);
            gameMetaLabel.Location = new Point(100, 84);

            // The library may remain open; clear its staged selection as well so the
            // UI does not imply that a game is still active.
            if (gameList.SelectedIndex >= 0)
                gameList.ClearSelected();
            UpdateLibraryPreview();

            UpdateGameState();

            // Keep controller/keyboard flow useful after clearing.
            if (gamesButton.CanFocus)
                gamesButton.Focus();
        }

        private void UpdateSelectedGameBanner()
        {
            Image old = gameBanner.Image;
            gameBanner.Image = null;
            if (old != null) old.Dispose();

            if (string.IsNullOrWhiteSpace(selectedGamePath) || dolphinPaths == null)
            {
                gameBanner.Visible = false;
                gameLabel.Location = new Point(100, 62);
                gameMetaLabel.Location = new Point(100, 84);
                return;
            }

            try
            {
                Image banner = Program.TryLoadBannerFromGameListCache(dolphinPaths, selectedGamePath);
                if (banner != null)
                {
                    gameBanner.Image = banner;
                    gameBanner.Visible = true;
                    gameLabel.Location = new Point(100, 88);
                    gameMetaLabel.Location = new Point(100, 110);
                    return;
                }
            }
            catch { }

            gameBanner.Visible = false;
            gameLabel.Left = 100;
            gameMetaLabel.Left = 100;
        }

        private void QueueLibraryPreviewUpdate()
        {
            if (!libraryPanel.Visible)
                return;

            libraryPreviewTimer.Stop();
            libraryPreviewTimer.Start();
        }

        private void UpdateLibraryPreview()
        {
            LibraryItem item = gameList.SelectedItem as LibraryItem;
            if (item == null)
            {
                SetLibraryCoverImage(null);
                libraryCoverTitle.Text = "";
                libraryCoverMeta.Text = "";
                return;
            }

            libraryCoverTitle.Text = item.Name;
            libraryCoverMeta.Text = "";

            GameInfo info = GetLibraryGameInfo(item.Path);
            if (info != null)
            {
                if (!string.IsNullOrWhiteSpace(info.Title))
                    libraryCoverTitle.Text = info.Title;

                List<string> meta = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.GameId)) meta.Add(info.GameId);
                if (info.Revision != 0) meta.Add("Revision " + info.Revision);
                libraryCoverMeta.Text = string.Join("  •  ", meta.ToArray());

                string coverPath = FindDolphinCover(item.Path, info.GameId);
                SetLibraryArtwork(item.Path, coverPath);
            }
            else
            {
                SetLibraryArtwork(item.Path, null);
                libraryCoverMeta.Text = "No game metadata available";
            }
        }

        private GameInfo GetLibraryGameInfo(string path)
        {
            GameInfo cached;
            if (libraryInfoCache.TryGetValue(path, out cached))
                return cached;

            if (libraryMetadataCache == null)
                libraryMetadataCache = Program.LoadLibraryMetadataCache();

            FileInfo fileInfo = null;
            try
            {
                if (File.Exists(path))
                    fileInfo = new FileInfo(path);
            }
            catch { }

            LibraryMetadataRecord record;
            if (fileInfo != null &&
                libraryMetadataCache.TryGetValue(path, out record) &&
                record != null &&
                record.WriteTicks == fileInfo.LastWriteTimeUtc.Ticks &&
                record.Length == fileInfo.Length)
            {
                GameInfo fromCache = new GameInfo
                {
                    Title = record.Title,
                    GameId = record.GameId,
                    Revision = record.Revision
                };

                List<string> bits = new List<string>();
                if (!string.IsNullOrWhiteSpace(fromCache.GameId)) bits.Add(fromCache.GameId);
                if (fromCache.Revision != 0) bits.Add("Revision " + fromCache.Revision);
                fromCache.NetPlayName = bits.Count == 0
                    ? fromCache.Title
                    : fromCache.Title + " (" + string.Join(", ", bits.ToArray()) + ")";

                libraryInfoCache[path] = fromCache;
                return fromCache;
            }

            GameInfo info = null;
            try
            {
                if (dolphinPaths != null && fileInfo != null)
                    info = Program.ResolveGameInfoForLibrary(dolphinPaths, path);
            }
            catch { }

            libraryInfoCache[path] = info;

            if (info != null && fileInfo != null)
            {
                libraryMetadataCache[path] = new LibraryMetadataRecord
                {
                    WriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                    Length = fileInfo.Length,
                    Title = info.Title ?? "",
                    GameId = info.GameId ?? "",
                    Revision = info.Revision
                };
                libraryMetadataDirty = true;
            }

            return info;
        }

        private void FlushLibraryMetadataCache()
        {
            if (!libraryMetadataDirty || libraryMetadataCache == null)
                return;

            Program.SaveLibraryMetadataCache(libraryMetadataCache);
            libraryMetadataDirty = false;
        }

        private string FindDolphinCover(string romPath, string gameId)
        {
            // Match Dolphin's own priority: a custom cover beside the ROM first,
            // then the downloaded GameTDB cover cache.
            try
            {
                if (!string.IsNullOrWhiteSpace(romPath))
                {
                    string dir = Path.GetDirectoryName(romPath);
                    string name = Path.GetFileNameWithoutExtension(romPath);

                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        string custom = Path.Combine(dir, name + ".cover.png");
                        if (File.Exists(custom)) return custom;

                        string folderCover = Path.Combine(dir, "cover.png");
                        if (File.Exists(folderCover)) return folderCover;
                    }
                }
            }
            catch { }

            if (dolphinPaths == null || string.IsNullOrWhiteSpace(dolphinPaths.GameCoversDir) ||
                !Directory.Exists(dolphinPaths.GameCoversDir) || string.IsNullOrWhiteSpace(gameId))
                return null;

            // Dolphin's downloaded covers are PNG files named with the GameTDB ID.
            // For normal GameCube/Wii discs this is the same six-character ID exposed
            // by DolphinTool, so use that directly and keep a case-insensitive fallback.
            string exact = Path.Combine(dolphinPaths.GameCoversDir, gameId + ".png");
            if (File.Exists(exact)) return exact;

            try
            {
                foreach (string file in Directory.GetFiles(dolphinPaths.GameCoversDir, "*.png"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (stem.Equals(gameId, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch { }

            return null;
        }

        private void SetLibraryArtwork(string romPath, string coverPath)
        {
            Image old = libraryCover.Image;
            libraryCover.Image = null;
            if (old != null) old.Dispose();

            // First choice: normal box art / custom cover.
            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
            {
                try
                {
                    using (Image source = Image.FromFile(coverPath))
                        libraryCover.Image = new Bitmap(source);
                    libraryCover.BackColor = AppTheme.ArtworkWell(settings);
                    return;
                }
                catch { }
            }

            // Second choice: Dolphin's own cached banner. This is important on installs
            // where GameCovers is empty but Dolphin still has the native disc banner.
            try
            {
                Image banner = Program.TryLoadBannerFromGameListCache(dolphinPaths, romPath);
                if (banner != null)
                {
                    libraryCover.Image = CreateBannerCard(banner, libraryCover.Width, libraryCover.Height);
                    banner.Dispose();
                    libraryCover.BackColor = AppTheme.ArtworkWell(settings);
                    return;
                }
            }
            catch { }

            libraryCover.BackColor = AppTheme.ArtworkWell(settings);
        }

        private Bitmap CreateBannerCard(Image banner, int width, int height)
        {
            width = Math.Max(32, width);
            height = Math.Max(32, height);

            Bitmap bmp = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Color cardBack = AppTheme.ArtworkWell(settings);
                g.Clear(cardBack);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                int margin = Math.Max(6, width / 16);
                int availableWidth = Math.Max(1, width - margin * 2);
                int maxBannerHeight = Math.Max(1, height / 2);

                double scale = Math.Min(
                    (double)availableWidth / Math.Max(1, banner.Width),
                    (double)maxBannerHeight / Math.Max(1, banner.Height));

                int drawWidth = Math.Max(1, (int)Math.Round(banner.Width * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(banner.Height * scale));
                int x = (width - drawWidth) / 2;
                int y = (height - drawHeight) / 2;

                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
                    g.FillRectangle(shadow, x + 2, y + 2, drawWidth, drawHeight);

                g.DrawImage(banner, new Rectangle(x, y, drawWidth, drawHeight));

                // The outer preview/tile frame owns the faux-depth border now.
                // Keeping the generated banner bitmap borderless avoids a visible
                // "frame resize" when the library panel first opens at its final width.
            }

            return bmp;
        }

        private void SetLibraryCoverImage(string path)
        {
            Image old = libraryCover.Image;
            libraryCover.Image = null;
            if (old != null) old.Dispose();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                libraryCover.BackColor = AppTheme.ArtworkWell(settings);
                return;
            }

            try
            {
                // Clone the image so Dolphin can still replace/update its cached file
                // while the launcher is open.
                using (Image source = Image.FromFile(path))
                    libraryCover.Image = new Bitmap(source);

                // Keep the preview visually integrated with the library. PictureBox
                // Zoom preserves the cover's aspect ratio, so any letterboxed space
                // should use the same soft neutral background as an empty preview
                // rather than a high-contrast black field.
                libraryCover.BackColor = AppTheme.ArtworkWell(settings);
            }
            catch
            {
                libraryCover.BackColor = AppTheme.ArtworkWell(settings);
            }
        }

        private void StageCurrentLibraryGame()
        {
            LibraryItem item = gameList.SelectedItem as LibraryItem;
            if (item == null) return;

            pendingLibraryGamePath = item.Path;
            libraryUseButton.Enabled = true;
            libraryUseButton.Focus();
        }

        private void CommitPendingLibraryGame()
        {
            if (string.IsNullOrWhiteSpace(pendingLibraryGamePath))
                return;

            LibraryItem item = null;
            foreach (object obj in gameList.Items)
            {
                LibraryItem candidate = obj as LibraryItem;
                if (candidate != null &&
                    string.Equals(candidate.Path, pendingLibraryGamePath, StringComparison.OrdinalIgnoreCase))
                {
                    item = candidate;
                    break;
                }
            }

            selectedGamePath = pendingLibraryGamePath;
            pendingLibraryGamePath = null;

            GameInfo selectedInfo = GetLibraryGameInfo(selectedGamePath);
            gameLabel.Text = selectedInfo != null && !string.IsNullOrWhiteSpace(selectedInfo.Title)
                ? selectedInfo.Title
                : (item != null ? item.Name : Path.GetFileNameWithoutExtension(selectedGamePath));
            gameMetaLabel.Text = selectedInfo != null
                ? BuildMeta(selectedInfo.GameId, selectedInfo.Revision)
                : "Selected game";
            UpdateSelectedGameBanner();
            clearGameButton.Enabled = !string.IsNullOrWhiteSpace(selectedGamePath);
            UpdateGameState();

            libraryUseButton.Enabled = false;
            SetLibraryVisible(false);
            gamesButton.Focus();
        }

        public void StageLibrarySelectionFromController()
        {
            if (!libraryPanel.Visible) return;
            StageCurrentLibraryGame();
        }

        public void FocusLibraryFooterFromController(bool useGame)
        {
            if (!libraryPanel.Visible) return;
            if (useGame)
            {
                if (libraryUseButton.Enabled)
                    libraryUseButton.Focus();
                else
                    libraryBrowseButton.Focus();
            }
            else
            {
                libraryBrowseButton.Focus();
            }
        }

        public bool IsLibraryListFocused
        {
            get { return gameList.ContainsFocus; }
        }

        public bool IsLibraryGridFocused
        {
            get { return libraryPanel.Visible && libraryGridView && gameGrid.ContainsFocus; }
        }

        public bool IsLibraryHeaderFocused
        {
            get
            {
                if (!libraryPanel.Visible) return false;
                Control active = ControllerNavigation.GetDeepActiveControl(this);
                return active == libraryColumnsBox ||
                       active == libraryGridButton ||
                       active == libraryListButton ||
                       active == libraryHideButton;
            }
        }

        public void FocusLibraryHeaderFromController()
        {
            if (!libraryPanel.Visible) return;

            // Back from the game grid enters the library toolbar at Hide. This keeps
            // the "back" direction intuitive while making Grid/List/columns reachable
            // by moving left.
            if (libraryHideButton.Visible && libraryHideButton.Enabled)
                libraryHideButton.Focus();
        }

        public void MoveLibraryHeaderFromController(int direction)
        {
            if (!libraryPanel.Visible) return;

            Control active = ControllerNavigation.GetDeepActiveControl(this);
            Control[] controls = libraryGridView
                ? new Control[] { libraryColumnsBox, libraryGridButton, libraryListButton, libraryHideButton }
                : new Control[] { libraryGridButton, libraryListButton, libraryHideButton };

            int current = -1;
            for (int i = 0; i < controls.Length; i++)
            {
                if (controls[i] == active)
                {
                    current = i;
                    break;
                }
            }

            if (current < 0)
                current = controls.Length - 1;

            int next = Math.Max(0, Math.Min(controls.Length - 1, current + direction));
            controls[next].Focus();
        }

        public void ActivateLibraryHeaderFromController()
        {
            Control active = ControllerNavigation.GetDeepActiveControl(this);

            ComboBox combo = active as ComboBox;
            if (combo == libraryColumnsBox)
            {
                combo.DroppedDown = true;
                return;
            }

            Button button = active as Button;
            if (button != null)
                button.PerformClick();
        }

        public Control SelectedGridTile
        {
            get
            {
                if (string.IsNullOrWhiteSpace(selectedGridPath)) return null;

                Control control;
                if (!libraryGridTiles.TryGetValue(selectedGridPath, out control) ||
                    control == null || control.IsDisposed)
                    return null;

                // Never let the form-level animated cursor target a scrolled-off tile.
                Rectangle tileScreen = control.RectangleToScreen(control.ClientRectangle);
                Rectangle gridScreen = gameGrid.RectangleToScreen(gameGrid.ClientRectangle);
                if (!gridScreen.IntersectsWith(tileScreen))
                    return null;

                return control;
            }
        }

        public void FocusLibraryContentFromController()
        {
            if (!libraryPanel.Visible) return;
            if (libraryGridView)
            {
                if (string.IsNullOrWhiteSpace(selectedGridPath) && gameList.Items.Count > 0)
                {
                    LibraryItem first = gameList.Items[0] as LibraryItem;
                    if (first != null) selectedGridPath = first.Path;
                }
                UpdateGridSelectionBorder();
                gameGrid.Focus();
                EnsureSelectedGridTileVisible();
            }
            else
            {
                gameList.Focus();
            }
        }

        public void MoveLibraryGridFromController(int dx, int dy)
        {
            if (!libraryPanel.Visible || !libraryGridView || gameList.Items.Count == 0) return;

            // gameList.SelectedIndex is kept in sync with the grid selection, so
            // controller movement does not need to rescan every LibraryItem by path.
            int current = Math.Max(0, gameList.SelectedIndex);

            int columns = Math.Max(1, libraryGridColumns);
            int next = current;
            if (dx < 0 && current % columns > 0) next = current - 1;
            if (dx > 0 && current % columns < columns - 1 && current + 1 < gameList.Items.Count) next = current + 1;
            if (dy < 0) next = Math.Max(0, current - columns);
            if (dy > 0) next = Math.Min(gameList.Items.Count - 1, current + columns);

            LibraryItem item = gameList.Items[next] as LibraryItem;
            if (item == null) return;
            selectedGridPath = item.Path;
            gameList.SelectedIndex = next;
            UpdateGridSelectionBorder();
            EnsureSelectedGridTileVisible();
        }

        public void PageLibraryGridFromController(int direction)
        {
            if (!libraryPanel.Visible || !libraryGridView || gameList.Items.Count == 0) return;
            int rows = Math.Max(1, gameGrid.ClientSize.Height / 180);
            MoveLibraryGridByIndex(direction * Math.Max(1, rows * Math.Max(1, libraryGridColumns)));
        }

        private void MoveLibraryGridByIndex(int delta)
        {
            int current = Math.Max(0, gameList.SelectedIndex);
            int next = Math.Max(0, Math.Min(gameList.Items.Count - 1, current + delta));
            LibraryItem item = gameList.Items[next] as LibraryItem;
            if (item == null) return;
            selectedGridPath = item.Path;
            gameList.SelectedIndex = next;
            UpdateGridSelectionBorder();
            EnsureSelectedGridTileVisible();
        }

        private void EnsureSelectedGridTileVisible()
        {
            if (string.IsNullOrWhiteSpace(selectedGridPath)) return;

            Control selected;
            if (!libraryGridTiles.TryGetValue(selectedGridPath, out selected) ||
                selected == null || selected.IsDisposed)
                return;

            // Tile geometry is stable during ordinary controller navigation. Forcing a
            // complete FlowLayoutPanel layout on every move is unnecessary and can make
            // a populated Games grid feel much slower than the cursor's configured Hz.

            // IMPORTANT: do NOT use ScrollControlIntoView here.
            // FlowLayoutPanel tends to align the requested child near the top-left,
            // which made it feel like the games were moving underneath a stationary
            // cursor. Instead, leave the grid completely still until the selected
            // cover reaches an edge, then scroll only the minimum distance necessary.
            Rectangle tileScreen = selected.RectangleToScreen(selected.ClientRectangle);
            Rectangle viewScreen = gameGrid.RectangleToScreen(gameGrid.ClientRectangle);

            int currentY = -gameGrid.AutoScrollPosition.Y;
            int targetY = currentY;

            // Leave enough room for the 11px animated glow as well as the cover.
            const int edgePad = 16;

            int safeTop = viewScreen.Top + edgePad;
            int safeBottom = viewScreen.Bottom - edgePad;

            if (tileScreen.Top < safeTop)
            {
                int delta = safeTop - tileScreen.Top;
                targetY = Math.Max(0, currentY - delta);
            }
            else if (tileScreen.Bottom > safeBottom)
            {
                int delta = tileScreen.Bottom - safeBottom;
                targetY = Math.Max(0, currentY + delta);
            }

            if (targetY != currentY)
            {
                gameGrid.AutoScrollPosition = new Point(0, targetY);
                // AutoScrollPosition already schedules the required layout/repaint.
                // Avoid synchronous PerformLayout()+Update() on the controller input tick.
                gameGrid.Invalidate();
            }
        }

        public void StageLibraryGridSelectionFromController()
        {
            if (!libraryPanel.Visible || !libraryGridView) return;
            if (gameList.SelectedIndex < 0 && gameList.Items.Count > 0) gameList.SelectedIndex = 0;
            LibraryItem item = gameList.SelectedItem as LibraryItem;
            if (item == null) return;
            selectedGridPath = item.Path;
            UpdateGridSelectionBorder();
            StageCurrentLibraryGame();
        }

        public void RefreshThemeVisuals()
        {
            Font oldGridRegularFont = libraryGridRegularFont;
            Font oldGridBoldFont = libraryGridBoldFont;
            libraryGridRegularFont = AppFonts.Create(settings, 8.25F, FontStyle.Regular);
            libraryGridBoldFont = AppFonts.Create(settings, 8.25F, FontStyle.Bold);

            // AppTheme.Apply handles the normal control tree. The library also has
            // custom-created tile/title/artwork colors that are assigned when the
            // tiles are built, so explicitly refresh those without forcing a library
            // rescan or recreating artwork.
            gameGrid.BackColor = AppTheme.Surface(settings);
            libraryEmptyPanel.BackColor = AppTheme.Surface(settings);
            gameList.BackColor = AppTheme.Field(settings);
            gameList.ForeColor = AppTheme.Fore(settings);
            libraryCover.BackColor = AppTheme.ArtworkWell(settings);

            foreach (Control control in gameGrid.Controls)
            {
                ThemedDepthPanel tile = control as ThemedDepthPanel;
                if (tile == null)
                    continue;

                bool dark = AppTheme.IsDark(settings);
                bool adventure = AppTheme.IsAdventure(settings);
                bool oled = AppTheme.IsOled(settings);
                bool cube = AppTheme.IsGameCube(settings);

                tile.AdventureStyle = adventure;
                tile.TintedThemeStyle = cube;
                tile.DarkThemeStyle = dark && !adventure && !cube;

                foreach (Control child in tile.Controls)
                {
                    PictureBox cover = child as PictureBox;
                    if (cover != null)
                        cover.BackColor = AppTheme.ArtworkWell(settings);

                    AdventureLabel title = child as AdventureLabel;
                    if (title != null)
                    {
                        title.AdventureStyle = adventure;
                        title.BackColor = AppTheme.Surface(settings);
                        title.ForeColor = AppTheme.Fore(settings);
                    }
                }
            }

            // Force every already-created grid title plate through the current palette
            // before selection styling. These controls survive theme changes, so relying
            // on their construction-time colors leaves the previous theme visible until
            // restart.
            foreach (Control control in gameGrid.Controls)
            {
                ThemedDepthPanel tile = control as ThemedDepthPanel;
                if (tile == null) continue;
                foreach (Control child in tile.Controls)
                {
                    Label title = child as Label;
                    if (title == null) continue;
                    bool dark = AppTheme.IsDark(settings);
                    bool adventure = AppTheme.IsAdventure(settings);
                    bool cube = AppTheme.IsGameCube(settings);
                    bool oled = AppTheme.IsOled(settings);
                    Color plate = cube ? AdventurePaint.Lighten(AppTheme.Surface(settings), 8)
                        : (adventure ? Color.FromArgb(28, 61, 112)
                        : (oled ? Color.FromArgb(15, 15, 18)
                        : (dark ? Color.FromArgb(46, 49, 54) : Color.FromArgb(248, 248, 248))));
                    title.BackColor = plate;
                    tile.InnerPlateBackColor = plate;
                    title.Invalidate();
                    tile.Invalidate();
                }
            }

            // These methods re-apply selected/idle tile/title colors, Grid/List button
            // colors, columns visibility/chrome, and preview state using the NEW theme.
            UpdateGridSelectionBorder(true);
            DisposePreviousLibraryGridFonts(oldGridRegularFont, oldGridBoldFont);
            SetLibraryGridView(libraryGridView);
            UpdateLibraryPreview();
            UpdateSelectorVisuals();
            UpdateGameState();

            libraryColumnsBox.Invalidate();
            gameList.Invalidate();
            gameGrid.Invalidate(true);
            libraryPanel.Invalidate(true);
            mainPanel.Invalidate(true);
        }

        public void ApplyControllerPromptSettings(bool visible, string style, string gamesButton)
        {
            controllerPromptBar.Visible = visible;
            controllerPromptBar.PromptStyle = style;
            controllerPromptBar.GamesButton = gamesButton;
            controllerPromptBar.Invalidate();
        }

        public void ToggleLibraryFromController()
        {
            if (!libraryPanel.Visible && sessionsVisible)
            {
                sidePanelSwapInProgress = true;
                try { SetSessionsVisible(false); }
                finally { sidePanelSwapInProgress = false; }
            }

            if (libraryPanel.Visible)
            {
                SetLibraryVisible(false);
                gamesButton.Focus();
            }
            else
            {
                // Set the desired view before the panel is resized/revealed so opening
                // performs one width/layout/focus pass instead of doing those twice.
                SetLibraryGridView(mousePrefersGrid);
                SetLibraryVisible(true);
                NotifyControllerFocusSettled();
            }
        }

        public void CollapseLibraryFromController()
        {
            if (libraryPanel.Visible)
            {
                SetLibraryVisible(false);
                gamesButton.Focus();
            }
        }

        private void ToggleLibrary()
        {
            if (!libraryPanel.Visible && sessionsVisible)
            {
                sidePanelSwapInProgress = true;
                try { SetSessionsVisible(false); }
                finally { sidePanelSwapInProgress = false; }
            }

            bool opening = !libraryPanel.Visible;
            if (opening)
            {
                // Resolve the saved Grid/List choice before SetLibraryVisible computes
                // the panel width. SetLibraryVisible owns the single focus pass.
                SetLibraryGridView(mousePrefersGrid);
            }
            SetLibraryVisible(opening);
        }

        private int GetLibraryPanelWidth()
        {
            if (!libraryGridView)
                return 400;

            // The library column contains 12px TableLayout padding on each side,
            // plus the FlowLayoutPanel's own padding and a vertical scrollbar.
            // Include all of that explicitly so "3 cols" really fits 3 complete
            // 106px tile slots rather than wrapping the last tile onto the next row.
            const int tileSlot = 106;      // 104px tile + 1px margin each side
            const int outerPadding = 24;   // TableLayoutPanel left/right padding
            const int flowPadding = 4;     // FlowLayoutPanel left/right padding
            const int scrollbar = 20;      // scrollbar + a little DPI/layout tolerance
            const int breathingRoom = 6;

            return (libraryGridColumns * tileSlot) +
                   outerPadding + flowPadding + scrollbar + breathingRoom;
        }

        private void ApplyLibraryPanelWidth()
        {
            if (!libraryPanel.Visible)
                return;

            int panelWidth = GetLibraryPanelWidth();
            rootLayout.ColumnStyles[1].SizeType = SizeType.Absolute;
            rootLayout.ColumnStyles[1].Width = panelWidth;

            // MinimumSize is an OUTER size; define it from the required CLIENT area.
            // This preserves the 690px vertical client minimum established in 0.9.4.
            MinimumSize = SizeFromClientSize(new Size(600 + panelWidth, 690));

            int clientHeight = Math.Max(690, ClientSize.Height);
            Size desiredOuter = SizeFromClientSize(new Size(600 + panelWidth, clientHeight));
            Rectangle work = Screen.FromControl(this).WorkingArea;

            Width = Math.Min(work.Width, Math.Max(MinimumSize.Width, desiredOuter.Width));
            if (Right > work.Right)
                Left = Math.Max(work.Left, work.Right - Width);
            if (Left < work.Left)
                Left = work.Left;
        }

        private void SetLibraryVisible(bool visible)
        {
            if (libraryPanel.Visible == visible) return;

            const int baseClientWidth = 600;
            const int baseClientHeight = 690;
            int panelWidth = GetLibraryPanelWidth();
            int clientHeight = Math.Max(baseClientHeight, ClientSize.Height);

            // 0.10.12e11: return to the clean pre-fade panel reveal used around
            // 0.10.12b. One suspended-layout geometry change, then reveal content.
            // This also makes controller focus deterministic: there is no delayed
            // fade completion that can steal focus several inputs after Games opens.
            bool focusLibraryAfterLayout = false;
            SuspendLayout();
            rootLayout.SuspendLayout();
            libraryPanel.SuspendLayout();
            gameGrid.SuspendLayout();
            try
            {
                if (visible)
                {
                    EnsureLibraryLoaded();
                    pendingLibraryGamePath = null;
                    libraryUseButton.Enabled = false;

                    rootLayout.ColumnStyles[1].SizeType = SizeType.Absolute;
                    rootLayout.ColumnStyles[1].Width = panelWidth;
                    MinimumSize = SizeFromClientSize(new Size(baseClientWidth + panelWidth, baseClientHeight));

                    Size desiredOuter = SizeFromClientSize(new Size(baseClientWidth + panelWidth, clientHeight));
                    Rectangle work = Screen.FromControl(this).WorkingArea;
                    Width = Math.Min(work.Width, Math.Max(MinimumSize.Width, desiredOuter.Width));
                    if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
                    if (Left < work.Left) Left = work.Left;
                    libraryPanel.Visible = true;
                    controllerPromptBar.LibraryMode = true;
                    controllerPromptBar.Invalidate();

                    // Focus after ResumeLayout. Doing it here would force the
                    // FlowLayoutPanel to lay itself out while the entire side-panel tree
                    // is suspended, only to be laid out again during ResumeLayout.
                    focusLibraryAfterLayout = true;
                }
                else
                {
                    libraryPreviewTimer.Stop();
                    libraryPanel.Visible = false;
                    controllerPromptBar.LibraryMode = false;
                    controllerPromptBar.Invalidate();

                    if (!sidePanelSwapInProgress)
                    {
                        rootLayout.ColumnStyles[1].Width = 0F;
                        MinimumSize = SizeFromClientSize(new Size(baseClientWidth, baseClientHeight));
                        Size desiredOuter = SizeFromClientSize(new Size(baseClientWidth, clientHeight));
                        Size = new Size(Math.Max(MinimumSize.Width, desiredOuter.Width),
                                        Math.Max(MinimumSize.Height, desiredOuter.Height));
                    }
                }
            }
            finally
            {
                gameGrid.ResumeLayout(false);
                libraryPanel.ResumeLayout(false);

                // RC37: do not force a full root-layout pass while the parent form is still
                // suspended. RC36 timing showed this caused an expensive root pass and then
                // another expensive form pass immediately afterward. Resume the root without
                // laying it out yet; the form's final ResumeLayout(true) sizes the docked root
                // and lets the child layout occur once against the final client geometry.
                rootLayout.ResumeLayout(false);
                ResumeLayout(true);
            }

            if (focusLibraryAfterLayout)
                FocusLibraryContentFromController();
        }

        public bool IsLibraryVisible
        {
            get { return libraryPanel.Visible; }
        }

        private static GraphicsPath CreateRoundedRect(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private bool IsPrimaryActionReady()
        {
            bool hasGame = !string.IsNullOrWhiteSpace(selectedGamePath);
            if (joinRadio.Checked)
                return !string.IsNullOrWhiteSpace(targetBox.Text);

            if (!hasGame)
                return false;

            if (publicHostCheck.Checked)
                return !string.IsNullOrWhiteSpace(publicSessionNameBox.Text) &&
                       !string.IsNullOrWhiteSpace(PublicSessionRegion);

            return true;
        }

        private void UpdateGameState()
        {
            bool hasGame = !string.IsNullOrWhiteSpace(selectedGamePath);

            // A selected game can be left over from hosting, Steam, or library browsing.
            // Make it explicit that Join ignores that selection.
            joinGameInfoIcon.Visible = joinRadio.Checked && hasGame;

            // Hosting needs a selected game so Dolphin knows what to host.
            // Joining does not: the host determines the game in Dolphin NetPlay.
            bool ready = IsPrimaryActionReady();

            goButton.Enabled = ready;

            // Fixed launcher colors make readiness obvious and independent of Windows accent settings.
            if (ready)
            {
                goButton.BackColor = Color.FromArgb(52, 112, 200);
                goButton.ForeColor = Color.White;
                goButton.FlatAppearance.BorderColor = AccentVisuals.Animated(settings)
                    ? AccentVisuals.CurrentColor()
                    : Color.FromArgb(35, 80, 155);
            }
            else
            {
                bool dark = AppTheme.IsDark(settings);
                goButton.BackColor = dark
                    ? Color.FromArgb(48, 51, 57)
                    : Color.FromArgb(218, 221, 226);
                goButton.ForeColor = dark
                    ? Color.FromArgb(132, 137, 145)
                    : Color.FromArgb(120, 125, 135);
                goButton.FlatAppearance.BorderColor = dark
                    ? Color.FromArgb(76, 80, 88)
                    : Color.FromArgb(185, 190, 198);
            }

            // Force one immediate repaint so the animated border appears/disappears exactly
            // when the action becomes ready/not-ready, rather than waiting for the timer.
            goButton.Invalidate();
        }

        public void FocusPasteFromController()
        {
            if (joinRadio.Checked && pasteButton.Visible && pasteButton.Enabled)
                pasteButton.Focus();
        }

        public void FocusPrimaryActionFromController()
        {
            if (goButton.Visible && goButton.Enabled)
            {
                goButton.Focus();
                return;
            }

            Control cancel = CancelButton as Control;
            if (cancel != null && cancel.Visible && cancel.Enabled)
                cancel.Focus();
        }

        public void FocusClearFromController()
        {
            if (clearGameButton.Visible && clearGameButton.Enabled)
                clearGameButton.Focus();
        }

        public bool IsSessionsVisible
        {
            get { return sessionsVisible; }
        }

        public bool HandleSessionsControllerNavigation(ControllerAction action)
        {
            if (!sessionsVisible || embeddedSessionsBrowser == null)
                return false;

            if (action == ControllerAction.Cancel)
            {
                embeddedSessionsBrowser.FocusHideFromController();
                return true;
            }

            return embeddedSessionsBrowser.HandleControllerNavigation(action);
        }

        public bool HandleMainControllerNavigation(ControllerAction action)
        {
            if (sessionsVisible && embeddedSessionsBrowser != null)
            {
                if (action == ControllerAction.Cancel)
                {
                    embeddedSessionsBrowser.FocusHideFromController();
                    return true;
                }
                return embeddedSessionsBrowser.HandleControllerNavigation(action);
            }

            if (libraryPanel.Visible)
                return false;

            Control active = ControllerNavigation.GetDeepActiveControl(this);
            if (active == null)
                return false;

            // Top game controls are one horizontal row:
            // Clear (when available) -> Games -> Sessions.
            if (active == clearGameButton || active == gamesButton || active == sessionsButton)
            {
                if (action == ControllerAction.Left)
                {
                    if (active == sessionsButton)
                        gamesButton.Focus();
                    else if (active == gamesButton && clearGameButton.Enabled)
                        clearGameButton.Focus();
                    else if (active == clearGameButton)
                        clearGameButton.Focus();
                    else
                        gamesButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    if (active == clearGameButton)
                        gamesButton.Focus();
                    else
                        sessionsButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    FocusSelectedMode();
                    return true;
                }
                return false;
            }

            // Host/Join is one logical selector row. Left/Right changes the choice,
            // while Up/Down moves between sections.
            if (active == hostRadio || active == joinRadio)
            {
                if (action == ControllerAction.Left)
                {
                    hostRadio.Checked = true;
                    hostRadio.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    joinRadio.Checked = true;
                    joinRadio.Focus();
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    gamesButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    nickBox.Focus();
                    return true;
                }
                return false;
            }

            if (active == nickBox)
            {
                if (action == ControllerAction.Up)
                {
                    FocusSelectedMode();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    if (joinRadio.Checked)
                        FocusSelectedJoinType();
                    else
                        publicHostCheck.Focus();
                    return true;
                }
                return false;
            }

            if (active == publicHostCheck)
            {
                if (action == ControllerAction.Up) { nickBox.Focus(); return true; }
                if (action == ControllerAction.Down)
                {
                    if (publicHostCheck.Checked) publicSessionNameBox.Focus();
                    else FocusDolphinTool(lastDolphinControllerControl);
                    return true;
                }
                if (action == ControllerAction.Accept)
                {
                    publicHostCheck.Checked = !publicHostCheck.Checked;
                    return true;
                }
                return false;
            }

            if (active == publicRegionBox && publicRegionBox.DroppedDown)
            {
                if (action == ControllerAction.Up || action == ControllerAction.Down)
                {
                    if (publicRegionBox.Items.Count > 0)
                    {
                        int delta = action == ControllerAction.Up ? -1 : 1;
                        int current = publicRegionBox.SelectedIndex < 0 ? 0 : publicRegionBox.SelectedIndex;
                        publicRegionBox.SelectedIndex = Math.Max(0,
                            Math.Min(publicRegionBox.Items.Count - 1, current + delta));
                    }
                    return true;
                }
                if (action == ControllerAction.Accept || action == ControllerAction.Cancel)
                {
                    publicRegionBox.DroppedDown = false;
                    publicRegionBox.Focus();
                    return true;
                }
                return true;
            }

            if (active == publicSessionNameBox || active == publicRegionBox)
            {
                if (active == publicRegionBox && action == ControllerAction.Accept)
                {
                    publicRegionBox.DroppedDown = true;
                    return true;
                }
                if (action == ControllerAction.Left) { publicSessionNameBox.Focus(); return true; }
                if (action == ControllerAction.Right) { publicRegionBox.Focus(); return true; }
                if (action == ControllerAction.Up) { publicHostCheck.Focus(); return true; }
                if (action == ControllerAction.Down) { publicPasswordBox.Focus(); return true; }
                return false;
            }

            if (active == publicPasswordBox)
            {
                if (action == ControllerAction.Up) { publicSessionNameBox.Focus(); return true; }
                if (action == ControllerAction.Down) { FocusDolphinTool(lastDolphinControllerControl); return true; }
                return false;
            }

            // Traversal/Direct is also one logical selector row.
            if (active == traversalRadio || active == directRadio)
            {
                if (action == ControllerAction.Left)
                {
                    traversalRadio.Checked = true;
                    traversalRadio.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    directRadio.Checked = true;
                    directRadio.Focus();
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    nickBox.Focus();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    targetBox.Focus();
                    return true;
                }
                return false;
            }

            // Room/IP + Paste is one row.
            if (active == targetBox || active == pasteButton)
            {
                if (action == ControllerAction.Left)
                {
                    targetBox.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    if (pasteButton.Visible && pasteButton.Enabled) pasteButton.Focus();
                    else targetBox.Focus();
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    FocusSelectedJoinType();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    if (directRadio.Checked && portBox.Visible)
                        portBox.Focus();
                    else
                        FocusDolphinTool(lastDolphinControllerControl);
                    return true;
                }
                return false;
            }

            if (active == portBox)
            {
                if (action == ControllerAction.Up)
                {
                    targetBox.Focus();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    FocusDolphinTool(lastDolphinControllerControl);
                    return true;
                }
                // Let generic Left/Right adjustment continue to change the port value.
                return false;
            }

            // Dolphin utilities are a horizontal tool row.
            if (active == dolphinUpdateButton || active == dolphinOptionsButton || active == dolphinChangeButton)
            {
                if (action == ControllerAction.Left)
                {
                    if (active == dolphinChangeButton) FocusDolphinTool(dolphinOptionsButton);
                    else FocusDolphinTool(dolphinUpdateButton);
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    if (active == dolphinUpdateButton) FocusDolphinTool(dolphinOptionsButton);
                    else FocusDolphinTool(dolphinChangeButton);
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    FocusControlAboveDolphin();
                    return true;
                }
                if (action == ControllerAction.Down)
                {
                    if (goButton.Enabled)
                        goButton.Focus();
                    else
                        cancelButton.Focus();
                    return true;
                }
                return false;
            }

            // Final actions are one horizontal row.
            if (active == goButton || active == cancelButton)
            {
                if (action == ControllerAction.Left)
                {
                    if (goButton.Enabled) goButton.Focus();
                    else cancelButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Right)
                {
                    cancelButton.Focus();
                    return true;
                }
                if (action == ControllerAction.Up)
                {
                    FocusDolphinTool(lastDolphinControllerControl);
                    return true;
                }
                return false;
            }

            return false;
        }

        private void FocusSelectedMode()
        {
            if (joinRadio.Checked)
                joinRadio.Focus();
            else
                hostRadio.Focus();
        }

        private void FocusSelectedJoinType()
        {
            if (directRadio.Checked)
                directRadio.Focus();
            else
                traversalRadio.Focus();
        }

        private void FocusDolphinTool(Control control)
        {
            if (control != dolphinUpdateButton &&
                control != dolphinOptionsButton &&
                control != dolphinChangeButton)
                control = dolphinOptionsButton;

            lastDolphinControllerControl = control;
            control.Focus();
        }

        private void FocusControlAboveDolphin()
        {
            if (!joinRadio.Checked)
            {
                nickBox.Focus();
                return;
            }

            if (directRadio.Checked && portBox.Visible)
            {
                portBox.Focus();
                return;
            }

            targetBox.Focus();
        }

        private static string BuildMeta(string gameId, int revision)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return "";
            string meta = gameId;
            if (revision != 0) meta += "  •  Revision " + revision;
            return meta;
        }

        private void UpdateSelectorVisuals()
        {
            bool dark = AppTheme.IsDark(settings);
            bool adventure = AppTheme.IsAdventure(settings);
            bool oled = AppTheme.IsOled(settings);
            bool cube = AppTheme.IsGameCube(settings);
            RadioButton[] selectors = new RadioButton[] { hostRadio, joinRadio, traversalRadio, directRadio };
            foreach (RadioButton rb in selectors)
            {
                bool selected = rb.Checked;
                if (adventure)
                {
                    rb.BackColor = selected ? Color.FromArgb(54, 126, 191) : Color.FromArgb(25, 53, 100);
                    rb.ForeColor = Color.FromArgb(248, 252, 255);
                    rb.FlatAppearance.MouseOverBackColor = selected
                        ? Color.FromArgb(69, 151, 210)
                        : Color.FromArgb(35, 72, 128);
                    rb.FlatAppearance.CheckedBackColor = Color.FromArgb(54, 126, 191);
                }
                else if (cube)
                {
                    rb.BackColor = selected ? AppTheme.ThemeSelected(settings) : AppTheme.ThemeButton(settings);
                    rb.ForeColor = AppTheme.Fore(settings);
                    rb.FlatAppearance.MouseOverBackColor = AdventurePaint.Lighten(rb.BackColor, 16);
                    rb.FlatAppearance.CheckedBackColor = AppTheme.ThemeSelected(settings);
                }
                else if (oled)
                {
                    rb.BackColor = selected ? Color.FromArgb(30, 68, 110) : Color.FromArgb(18, 20, 23);
                    rb.ForeColor = selected ? Color.FromArgb(240, 246, 255) : Color.FromArgb(220, 224, 230);
                    rb.FlatAppearance.MouseOverBackColor = selected
                        ? Color.FromArgb(39, 82, 130)
                        : Color.FromArgb(28, 30, 34);
                    rb.FlatAppearance.CheckedBackColor = Color.FromArgb(30, 68, 110);
                }
                else if (dark)
                {
                    rb.BackColor = selected ? Color.FromArgb(43, 73, 108) : Color.FromArgb(43, 46, 52);
                    rb.ForeColor = selected ? Color.FromArgb(235, 242, 252) : Color.FromArgb(220, 224, 230);
                    rb.FlatAppearance.MouseOverBackColor = selected
                        ? Color.FromArgb(50, 83, 122)
                        : Color.FromArgb(55, 59, 66);
                    rb.FlatAppearance.CheckedBackColor = Color.FromArgb(43, 73, 108);
                }
                else
                {
                    rb.BackColor = selected ? Color.FromArgb(224, 237, 252) : Color.FromArgb(244, 246, 249);
                    rb.ForeColor = selected ? SystemColors.Highlight : Color.FromArgb(55, 60, 70);
                    rb.FlatAppearance.MouseOverBackColor = selected
                        ? Color.FromArgb(218, 233, 250)
                        : Color.FromArgb(235, 238, 243);
                    rb.FlatAppearance.CheckedBackColor = Color.FromArgb(224, 237, 252);
                }
            }

            modeSelectorPanel.Invalidate();
            connectionSelectorPanel.Invalidate();
        }

        private void UpdatePublicHostUi()
        {
            bool enabled = publicHostCheck.Checked;
            publicSessionNameBox.Enabled = enabled;
            publicRegionBox.Enabled = enabled;
            publicPasswordBox.Enabled = enabled;
        }

        private void UpdateMainMode()
        {
            bool join = joinRadio.Checked;
            UpdateSelectorVisuals();
            connectionHeading.Visible = join;
            connectionSelectorPanel.Visible = join;
            publicHostGroup.Visible = !join;
            UpdatePublicHostUi();
            targetLabel.Visible = join;
            targetBox.Visible = join;
            pasteButton.Visible = join;
            portLabel.Visible = join && directRadio.Checked;
            portBox.Visible = join && directRadio.Checked;
            goButton.Text = join ? "▶  Join" : "▶  Host";

            controllerPromptBar.ShowPasteShortcut = join;
            controllerPromptBar.PrimaryAction = join ? "Join" : "Host";
            controllerPromptBar.Invalidate();

            UpdateGameState();
        }

        private static int GetDirectAddressMaxLength(string value)
        {
            // Typical IPv4 addresses max out at 15 characters (255.255.255.255), which
            // gives users the clear limit they expect. If ':' appears, expand to 45 so
            // full textual IPv6 / IPv4-mapped IPv6 forms still fit.
            return !string.IsNullOrEmpty(value) && value.IndexOf(':') >= 0
                ? DirectIpv6MaxLength
                : DirectIpv4MaxLength;
        }

        private static void EnforceIniSafeText(TextBox box)
        {
            string safe = Program.RemoveUnsafeIniCharacters(box.Text);
            if (safe == box.Text)
                return;

            int caret = Math.Min(box.SelectionStart, safe.Length);
            box.Text = safe;
            box.SelectionStart = caret;
        }

        private static string ClampTargetText(string value, int maxLength)
        {
            string text = value ?? "";
            if (text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength);
        }

        private void SwitchJoinMode(JoinConnection next)
        {
            UpdateSelectorVisuals();
            if (previousJoinConnection == JoinConnection.Traversal)
                traversalValue = ClampTargetText(targetBox.Text, TraversalRoomCodeMaxLength);
            else
                directValue = ClampTargetText(
                    targetBox.Text, GetDirectAddressMaxLength(targetBox.Text));

            previousJoinConnection = next;
            if (next == JoinConnection.Direct)
            {
                targetLabel.Text = "IP address:";
                int directMax = GetDirectAddressMaxLength(directValue);
                targetBox.MaxLength = directMax;
                targetBox.Text = ClampTargetText(directValue, directMax);
                portLabel.Visible = joinRadio.Checked;
                portBox.Visible = joinRadio.Checked;
            }
            else
            {
                targetLabel.Text = "Room code:";
                targetBox.MaxLength = TraversalRoomCodeMaxLength;
                targetBox.Text = ClampTargetText(traversalValue, TraversalRoomCodeMaxLength);
                portLabel.Visible = false;
                portBox.Visible = false;
            }
            targetBox.SelectionStart = targetBox.Text.Length;
            UpdateGameState();
        }

        // RC19 diagnostic-only lifecycle probes.  These overrides bracket WinForms'
        // own Form closing/closed event dispatch and disposal so a hung returned
        // launcher can show whether the stall occurs inside base lifecycle handling.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm OnFormClosing BEFORE base. CloseReason=" + e.CloseReason + "; Cancel=" + e.Cancel + "; DialogResult=" + DialogResult + ".");
            base.OnFormClosing(e);
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm OnFormClosing AFTER base. CloseReason=" + e.CloseReason + "; Cancel=" + e.Cancel + "; DialogResult=" + DialogResult + ".");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm OnFormClosed BEFORE base. CloseReason=" + e.CloseReason + "; DialogResult=" + DialogResult + ".");
            base.OnFormClosed(e);
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm OnFormClosed AFTER base. CloseReason=" + e.CloseReason + "; DialogResult=" + DialogResult + ".");
        }

        protected override void Dispose(bool disposing)
        {
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm Dispose BEFORE base. disposing=" + disposing + "; IsHandleCreated=" + IsHandleCreated + "; IsDisposed=" + IsDisposed + ".");
            base.Dispose(disposing);
            DiagnosticsLog.Write("SHUTDOWN", "LauncherForm Dispose AFTER base. disposing=" + disposing + "; IsHandleCreated=" + IsHandleCreated + "; IsDisposed=" + IsDisposed + ".");
        }

        private sealed class LibraryItem
        {
            public readonly string Name;
            public readonly string Path;
            public LibraryItem(string name, string path) { Name = name; Path = path; }
            public override string ToString() { return Name; }
        }
    }

    internal sealed class OptionsForm : Form
    {
        private readonly DnlSettings settings;
        private readonly DolphinPaths paths;
        private readonly string romPath;
        private readonly string dolphinVersion;
        private readonly string updateTrack;
        private readonly SdlControllerManager controllerManager;

        private readonly TextBox dolphinExeBox = new TextBox();
        private readonly ComboBox appearanceBox = new ComboBox();
        private readonly CheckBox animatedThemeBackgroundBox = new CheckBox();
        private readonly ComboBox interfaceStyleBox = new ComboBox();
        private readonly Label interfaceStylePreview = new AdventureLabel();
        private readonly Label interfaceStyleStatus = new AdventureLabel();
        private readonly ComboBox accentStyleBox = new ComboBox();
        private readonly CheckBox interfaceSoundsBox = new CheckBox();
        private readonly ComboBox soundStyleBox = new ComboBox();
        private readonly ComboBox libraryViewBox = new ComboBox();
        private readonly NumericUpDown libraryColumnsOptionBox = new NumericUpDown();
        private readonly CheckBox autoCloseBox = new CheckBox();
        private readonly NumericUpDown graceBox = new NumericUpDown();
        private readonly NumericUpDown updateGraceBox = new NumericUpDown();
        private readonly CheckBox warningBox = new CheckBox();
        private readonly CheckBox failedJoinReturnBox = new CheckBox();
        private readonly CheckBox returnAfterDolphinCloseBox = new CheckBox();
        private readonly CheckBox rememberModeBox = new CheckBox();
        private readonly ListView pathList = new ListView();
        private readonly CheckBox openLibraryStandaloneBox = new CheckBox();
        private readonly CheckBox openLibrarySteamBox = new CheckBox();
        private readonly CheckBox controllerPromptsBox = new CheckBox();
        private readonly ComboBox promptStyleBox = new ComboBox();
        private readonly ComboBox gamesButtonBox = new ComboBox();
        private readonly CheckBox controllerEnabledBox = new CheckBox();
        private readonly CheckBox leftStickBox = new CheckBox();
        private readonly ComboBox controllerPollingBox = new ComboBox();
        private readonly Label controllerPollingDetectedLabel = new AdventureLabel();
        private readonly ComboBox controllerBox = new ComboBox();
        private readonly Label controllerStatusLabel = new AdventureLabel();
        private readonly Label controllerInputLabel = new AdventureLabel();
        private readonly CheckBox highlightAutoBox = new CheckBox();
        private readonly NumericUpDown highlightHzBox = new NumericUpDown();
        private readonly Label detectedRefreshLabel = new AdventureLabel();
        private readonly System.Windows.Forms.Timer controllerUiTimer = new System.Windows.Forms.Timer();

        public bool SettingsImportRequested { get; private set; }

        public OptionsForm(DnlSettings settings, DolphinPaths paths, string romPath, string dolphinVersion, string updateTrack, SdlControllerManager controllerManager)
        {
            this.settings = settings;
            this.paths = paths;
            this.romPath = romPath;
            this.dolphinVersion = dolphinVersion;
            this.updateTrack = updateTrack;
            this.controllerManager = controllerManager;

            Text = "Dolphin NetPlay Launcher Options";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 620);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            // Options is intentionally only modestly resizable. Unlike the main launcher,
            // none of the settings pages benefit from huge desktop-sized dimensions, and
            // the existing tab layouts were designed around the 720x620 client area.
            // Keep enough growth room for longer paths/help text without allowing controls
            // to be crushed vertically or stretched across an entire monitor.
            MinimumSize = SizeFromClientSize(new Size(720, 620));
            MaximumSize = SizeFromClientSize(new Size(900, 700));
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);

            TabControl tabs = new TabControl();
            tabs.Location = new Point(12, 12);
            tabs.Size = new Size(696, 535);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tabs);

            TabPage general = new TabPage("General");
            TabPage appearance = new TabPage("Appearance");
            TabPage controller = new TabPage("Controller");
            TabPage library = new TabPage("Library");
            TabPage dolphin = new TabPage("Dolphin");
            TabPage diagnostics = new TabPage("Diagnostics");
            TabPage aboutPage = new TabPage("About");
            tabs.TabPages.Add(general);
            tabs.TabPages.Add(appearance);
            tabs.TabPages.Add(controller);
            tabs.TabPages.Add(library);
            tabs.TabPages.Add(dolphin);
            tabs.TabPages.Add(diagnostics);
            tabs.TabPages.Add(aboutPage);

            BuildGeneralTab(general);
            BuildAppearanceTab(appearance);
            BuildControllerTab(controller);
            BuildLibraryOptionsTab(library);
            BuildDolphinTab(dolphin);
            BuildDiagnosticsTab(diagnostics);
            BuildAboutTab(aboutPage);

            ControllerNavigation.Attach(this, controllerManager, settings, tabs);

            Button ok = new AdventureButton();
            ok.Text = "OK";
            ok.Location = new Point(542, 575);
            ok.Size = new Size(75, 30);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { ApplySettings(); };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new AdventureButton();
            cancel.Text = "Cancel";
            cancel.Location = new Point(625, 575);
            cancel.Size = new Size(75, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);

            // Start controller/keyboard focus in the actual options content instead of
            // defaulting to the bottom OK button. BeginInvoke lets the selected tab finish
            // laying out first, so the controller highlight starts in the right place.
            Shown += delegate
            {
                UpdateControllerPollingDetectedLabel();
                BeginInvoke((MethodInvoker)delegate
                {
                    if (tabs.SelectedTab != null)
                        ControllerNavigation.FocusFirstNavigable(tabs.SelectedTab);
                });
            };

            tabs.SelectedIndexChanged += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (tabs.SelectedTab != null)
                        ControllerNavigation.FocusFirstNavigable(tabs.SelectedTab);
                });
            };
        }

        private void BuildGeneralTab(TabPage page)
        {
            page.AutoScroll = true;

            GroupBox behavior = new AdventureGroupBox();
            behavior.Text = "Behavior";
            behavior.Location = new Point(18, 18);
            behavior.Size = new Size(649, 270);
            page.Controls.Add(behavior);

            autoCloseBox.Text = "Automatically close Dolphin when the NetPlay lobby closes";
            autoCloseBox.Location = new Point(18, 30);
            autoCloseBox.AutoSize = true;
            autoCloseBox.Checked = settings.AutoCloseDolphin;
            behavior.Controls.Add(autoCloseBox);

            Label graceLabel = new AdventureLabel();
            graceLabel.Text = "NetPlay close grace period:";
            graceLabel.Location = new Point(37, 67);
            graceLabel.AutoSize = true;
            behavior.Controls.Add(graceLabel);

            graceBox.Location = new Point(205, 63);
            graceBox.Size = new Size(85, 24);
            graceBox.DecimalPlaces = 1;
            graceBox.Increment = 0.5M;
            graceBox.Minimum = 0.5M;
            graceBox.Maximum = 10.0M;
            graceBox.Value = Math.Max(graceBox.Minimum, Math.Min(graceBox.Maximum, settings.NetPlayCloseGraceMs / 1000M));
            behavior.Controls.Add(graceBox);

            Label sec = new AdventureLabel();
            sec.Text = "seconds";
            sec.Location = new Point(298, 67);
            sec.AutoSize = true;
            behavior.Controls.Add(sec);

            Label graceHelp = new AdventureLabel();
            graceHelp.Text = "The launcher waits this long before deciding the NetPlay window is really gone.";
            graceHelp.ForeColor = Color.DimGray;
            graceHelp.Location = new Point(37, 91);
            graceHelp.AutoSize = true;
            behavior.Controls.Add(graceHelp);

            warningBox.Text = "Show the \"Please don't move the mouse\" automation warning";
            warningBox.Location = new Point(18, 126);
            warningBox.AutoSize = true;
            warningBox.Checked = settings.ShowAutomationWarning;
            behavior.Controls.Add(warningBox);

            rememberModeBox.Text = "Remember whether I last chose Host or Join";
            rememberModeBox.Location = new Point(18, 160);
            rememberModeBox.AutoSize = true;
            rememberModeBox.Checked = settings.RememberLastMode;
            behavior.Controls.Add(rememberModeBox);

            failedJoinReturnBox.Text = "Automatically return to Dolphin NetPlay Launcher after a failed Join";
            failedJoinReturnBox.Location = new Point(18, 192);
            failedJoinReturnBox.AutoSize = true;
            failedJoinReturnBox.Checked = settings.AutoReturnAfterFailedJoin;
            behavior.Controls.Add(failedJoinReturnBox);

            returnAfterDolphinCloseBox.Text = "Return to Dolphin NetPlay Launcher when Dolphin closes";
            returnAfterDolphinCloseBox.Location = new Point(18, 224);
            returnAfterDolphinCloseBox.AutoSize = true;
            returnAfterDolphinCloseBox.Checked = settings.ReturnToLauncherAfterDolphinClose;
            behavior.Controls.Add(returnAfterDolphinCloseBox);

            GroupBox standalone = new AdventureGroupBox();
            standalone.Text = "Startup";
            standalone.Location = new Point(18, 302);
            standalone.Size = new Size(649, 104);
            page.Controls.Add(standalone);

            openLibraryStandaloneBox.Text = "Open game library automatically when launched standalone";
            openLibraryStandaloneBox.Location = new Point(18, 28);
            openLibraryStandaloneBox.AutoSize = true;
            openLibraryStandaloneBox.Checked = settings.OpenLibraryOnStandalone;
            standalone.Controls.Add(openLibraryStandaloneBox);

            openLibrarySteamBox.Text = "Open game library automatically when launched from Steam";
            openLibrarySteamBox.Location = new Point(18, 58);
            openLibrarySteamBox.AutoSize = true;
            openLibrarySteamBox.Checked = settings.OpenLibraryOnSteam;
            standalone.Controls.Add(openLibrarySteamBox);

            GroupBox utility = new AdventureGroupBox();
            utility.Text = "Dolphin NetPlay Launcher";
            utility.Location = new Point(18, 420);
            utility.Size = new Size(649, 104);
            page.Controls.Add(utility);

            Button openFolder = new AdventureButton();
            openFolder.Text = "Open Launcher Folder";
            openFolder.Location = new Point(18, 29);
            openFolder.Size = new Size(135, 30);
            openFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { }
            };
            utility.Controls.Add(openFolder);

            Button resetWindow = new AdventureButton();
            resetWindow.Text = "Reset window size";
            resetWindow.Location = new Point(163, 29);
            resetWindow.Size = new Size(125, 30);
            resetWindow.Click += delegate
            {
                settings.WindowWidth = 600;
                settings.WindowHeight = 575;
                MessageBox.Show("Saved main-window size reset.", "Dolphin NetPlay Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            utility.Controls.Add(resetWindow);

            Button reset = new AdventureButton();
            reset.Text = "Reset Options";
            reset.Location = new Point(298, 29);
            reset.Size = new Size(115, 30);
            reset.Click += delegate
            {
                if (MessageBox.Show(
                    "Reset Dolphin NetPlay Launcher options to their defaults?\n\nYour selected Dolphin installation will be kept.",
                    "Reset Launcher Options",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    appearanceBox.SelectedItem = "Adventure Blue";
                    interfaceStyleBox.SelectedItem = "Outfit";
                    accentStyleBox.SelectedIndex = 1;
                    libraryViewBox.SelectedItem = "Grid";
                    libraryColumnsOptionBox.Value = 3;
                    autoCloseBox.Checked = true;
                    graceBox.Value = 2.0M;
                    updateGraceBox.Value = 1.0M;
                    warningBox.Checked = true;
                    failedJoinReturnBox.Checked = false;
                    returnAfterDolphinCloseBox.Checked = false;
                    rememberModeBox.Checked = false;
                    openLibraryStandaloneBox.Checked = false;
                    openLibrarySteamBox.Checked = false;
                    controllerPromptsBox.Checked = true;
                    promptStyleBox.SelectedItem = "Xbox";
                    gamesButtonBox.SelectedIndex = 0;
                    controllerEnabledBox.Checked = true;
                    leftStickBox.Checked = true;
                    controllerPollingBox.SelectedIndex = 2;
                    highlightAutoBox.Checked = true;
                    highlightHzBox.Value = 60;
                    interfaceSoundsBox.Checked = true;
                    animatedThemeBackgroundBox.Checked = true;
                    RefreshSoundStyleChoices("ClassicUI");
                    if (controllerBox.Items.Count > 0) controllerBox.SelectedIndex = 0;
                }
            };
            utility.Controls.Add(reset);

            Button exportSettings = new AdventureButton();
            exportSettings.Text = "Export...";
            exportSettings.Location = new Point(423, 29);
            exportSettings.Size = new Size(95, 30);
            exportSettings.Click += delegate
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "Export Dolphin NetPlay Launcher Settings";
                    dlg.Filter = "Dolphin NetPlay Launcher settings (*.ini)|*.ini|All files (*.*)|*.*";
                    dlg.FileName = "DolphinNetPlayLauncher-settings.ini";
                    dlg.OverwritePrompt = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;

                    DnlSettings snapshot = settings.CloneSettings();
                    ApplySettingsTo(snapshot, false);
                    if (Program.SaveDnlSettingsToFile(snapshot, dlg.FileName, true))
                    {
                        MessageBox.Show(
                            "Settings exported successfully.\n\n" + dlg.FileName,
                            "Export Settings",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                }
            };
            utility.Controls.Add(exportSettings);

            Button importSettings = new AdventureButton();
            importSettings.Text = "Import...";
            importSettings.Location = new Point(528, 29);
            importSettings.Size = new Size(95, 30);
            importSettings.Click += delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Import Dolphin NetPlay Launcher Settings";
                    dlg.Filter = "Dolphin NetPlay Launcher settings (*.ini)|*.ini|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        string[] importedLines = File.ReadAllLines(dlg.FileName);
                        bool looksLikeDnl = false;
                        bool hasCoreOption = false;
                        foreach (string raw in importedLines)
                        {
                            string line = (raw ?? "").Trim();
                            if (line.Equals("# Dolphin NetPlay Launcher settings", StringComparison.OrdinalIgnoreCase))
                                looksLikeDnl = true;
                            if (line.StartsWith("ControllerNavigation=", StringComparison.OrdinalIgnoreCase) ||
                                line.StartsWith("ThemeStyle=", StringComparison.OrdinalIgnoreCase) ||
                                line.StartsWith("DolphinExe=", StringComparison.OrdinalIgnoreCase))
                                hasCoreOption = true;
                        }

                        if (!looksLikeDnl && !hasCoreOption)
                        {
                            MessageBox.Show(
                                "That file does not look like a Dolphin NetPlay Launcher settings file.",
                                "Import Settings",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            return;
                        }

                        if (MessageBox.Show(
                            "Importing replaces the launcher's current settings, including paths and controller preferences.\n\n" +
                            "Dolphin NetPlay Launcher will restart after the import.\n\nContinue?",
                            "Import Settings",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) != DialogResult.Yes)
                            return;

                        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
                        if (File.Exists(configPath))
                        {
                            string backup = Path.Combine(
                                AppDomain.CurrentDomain.BaseDirectory,
                                "config-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".ini");
                            File.Copy(configPath, backup, false);
                        }

                        File.Copy(dlg.FileName, configPath, true);
                        SettingsImportRequested = true;
                        DialogResult = DialogResult.Abort;
                        Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "Could not import the settings file.\n\n" + ex.Message,
                            "Import Settings",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            };
            utility.Controls.Add(importSettings);

            Label about = new AdventureLabel();
            about.Text = Program.AppDisplayName + "  •  Portable utility  •  Unofficial Dolphin community tool";
            about.ForeColor = Color.DimGray;
            about.Location = new Point(18, 75);
            about.AutoSize = true;
            utility.Controls.Add(about);
        }



        private void BuildAppearanceTab(TabPage page)
        {
            page.AutoScroll = true;

            GroupBox theme = new AdventureGroupBox();
            theme.Text = "Theme";
            theme.Location = new Point(18, 18);
            theme.Size = new Size(649, 150);
            page.Controls.Add(theme);

            Label appearanceLabel = new AdventureLabel();
            appearanceLabel.Text = "Theme:";
            appearanceLabel.Location = new Point(18, 31);
            appearanceLabel.AutoSize = true;
            theme.Controls.Add(appearanceLabel);

            appearanceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            appearanceBox.Items.AddRange(new object[] {
                "System", "Light", "Dark", "OLED Black", "Adventure Blue",
                "GameCube Indigo", "GameCube Spice Orange" });
            appearanceBox.Location = new Point(110, 27);
            appearanceBox.Size = new Size(170, 24);
            appearanceBox.SelectedItem = AppTheme.IsAdventure(settings)
                ? "Adventure Blue"
                : (AppTheme.IsOled(settings) ? "OLED Black"
                : (AppTheme.IsGameCubeIndigo(settings) ? "GameCube Indigo"
                : (AppTheme.IsGameCubeSpice(settings) ? "GameCube Spice Orange"
                : (string.Equals(settings.Appearance, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" :
                   string.Equals(settings.Appearance, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "System"))));
            theme.Controls.Add(appearanceBox);

            Label themeHelp = new AdventureLabel();
            themeHelp.Text = "System follows Windows. Animated background uses the same subtle motion across every theme.";
            themeHelp.Location = new Point(18, 68);
            themeHelp.AutoSize = true;
            themeHelp.ForeColor = Color.DimGray;
            theme.Controls.Add(themeHelp);

            animatedThemeBackgroundBox.Text = "Animated background";
            animatedThemeBackgroundBox.Location = new Point(18, 103);
            animatedThemeBackgroundBox.AutoSize = true;
            animatedThemeBackgroundBox.Checked = settings.AnimatedThemeBackground;
            theme.Controls.Add(animatedThemeBackgroundBox);

            GroupBox interfaceStyle = new AdventureGroupBox();
            interfaceStyle.Text = "Interface Font";
            interfaceStyle.Location = new Point(18, 183);
            interfaceStyle.Size = new Size(649, 150);
            page.Controls.Add(interfaceStyle);

            Label styleLabel = new AdventureLabel();
            styleLabel.Text = "Font:";
            styleLabel.Location = new Point(18, 31);
            styleLabel.AutoSize = true;
            interfaceStyle.Controls.Add(styleLabel);

            interfaceStyleBox.DropDownStyle = ComboBoxStyle.DropDownList;
            interfaceStyleBox.Items.AddRange(AppFonts.Choices);
            interfaceStyleBox.Location = new Point(110, 27);
            interfaceStyleBox.Size = new Size(200, 24);
            interfaceStyleBox.SelectedItem = AppFonts.UseClassic(settings) ? "Outfit" : "Default";
            interfaceStyle.Controls.Add(interfaceStyleBox);

            interfaceStylePreview.Name = "InterfaceStylePreview";
            interfaceStylePreview.Text = "Dolphin NetPlay Launcher  •  Host  Join  Games  0123456789";
            interfaceStylePreview.Location = new Point(18, 67);
            interfaceStylePreview.Size = new Size(605, 28);
            interfaceStylePreview.AutoEllipsis = true;
            interfaceStyle.Controls.Add(interfaceStylePreview);

            interfaceStyleStatus.Location = new Point(18, 103);
            interfaceStyleStatus.Size = new Size(605, 34);
            interfaceStyleStatus.ForeColor = Color.DimGray;
            interfaceStyle.Controls.Add(interfaceStyleStatus);

            Action updateStylePreview = delegate
            {
                bool outfit = interfaceStyleBox.SelectedItem != null &&
                    interfaceStyleBox.SelectedItem.ToString() == "Outfit";
                DnlSettings previewSettings = new DnlSettings();
                string previewTheme = appearanceBox.SelectedItem != null ? appearanceBox.SelectedItem.ToString() : "System";
                previewSettings.ThemeStyle = previewTheme == "Adventure Blue" ? "AdventureBlue"
                    : (previewTheme == "OLED Black" ? "OledBlack"
                    : (previewTheme == "GameCube Indigo" ? "GameCubeIndigo"
                    : (previewTheme == "GameCube Spice Orange" ? "GameCubeSpice" : "Default")));
                previewSettings.Appearance =
                    (previewTheme == "Adventure Blue" || previewTheme == "OLED Black" ||
                     previewTheme == "GameCube Indigo" || previewTheme == "GameCube Spice Orange")
                    ? "Dark" : previewTheme;
                previewSettings.InterfaceStyle = outfit ? "Outfit" : "Default";
                interfaceStylePreview.Font = AppFonts.Create(previewSettings, 10F, FontStyle.Bold);
                interfaceStyleStatus.Text = outfit
                    ? (AppFonts.IsAvailable(previewSettings)
                        ? "Uses Outfit. Font choice is independent from the color theme."
                        : "Outfit font files not found. This font will fall back to Default until they are placed in the Fonts folder.")
                    : "Uses the established Segoe UI interface font. Font choice is independent from the color theme.";
                interfaceStylePreview.Invalidate();
            };
            interfaceStyleBox.SelectedIndexChanged += delegate { updateStylePreview(); };
            appearanceBox.SelectedIndexChanged += delegate { updateStylePreview(); };
            updateStylePreview();

            GroupBox accent = new AdventureGroupBox();
            accent.Text = "Accent";
            accent.Location = new Point(18, 348);
            accent.Size = new Size(649, 130);
            page.Controls.Add(accent);

            Label accentLabel = new AdventureLabel();
            accentLabel.Text = "Accent style:";
            accentLabel.Location = new Point(18, 31);
            accentLabel.AutoSize = true;
            accent.Controls.Add(accentLabel);

            accentStyleBox.DropDownStyle = ComboBoxStyle.DropDownList;
            accentStyleBox.Items.AddRange(new object[] { "System Accent", "Animated Gradient" });
            accentStyleBox.Location = new Point(110, 27);
            accentStyleBox.Size = new Size(180, 24);
            accentStyleBox.SelectedIndex =
                string.Equals(settings.AccentStyle, "AnimatedGradient",
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            accent.Controls.Add(accentStyleBox);

            Label accentHelp = new AdventureLabel();
            accentHelp.Text =
                "System Accent keeps the classic Windows-accent look. Animated Gradient uses the\n" +
                "slow purple / blue / cyan / teal motion on active UI only.";
            accentHelp.Location = new Point(18, 66);
            accentHelp.Size = new Size(600, 48);
            accentHelp.ForeColor = Color.DimGray;
            accent.Controls.Add(accentHelp);

            GroupBox sounds = new AdventureGroupBox();
            sounds.Text = "Interface Sounds";
            sounds.Location = new Point(18, 493);
            sounds.Size = new Size(649, 184);
            page.Controls.Add(sounds);

            interfaceSoundsBox.Text = "Enable interface sounds";
            interfaceSoundsBox.Location = new Point(18, 28);
            interfaceSoundsBox.AutoSize = true;
            interfaceSoundsBox.Checked = settings.InterfaceSounds;
            sounds.Controls.Add(interfaceSoundsBox);

            Label soundStyleLabel = new AdventureLabel();
            soundStyleLabel.Text = "Sound style:";
            soundStyleLabel.Location = new Point(18, 67);
            soundStyleLabel.AutoSize = true;
            sounds.Controls.Add(soundStyleLabel);

            soundStyleBox.DropDownStyle = ComboBoxStyle.DropDownList;
            soundStyleBox.Location = new Point(110, 63);
            soundStyleBox.Size = new Size(225, 24);
            sounds.Controls.Add(soundStyleBox);
            RefreshSoundStyleChoices(settings.SoundStyle);
            soundStyleBox.DropDown += delegate { RefreshSoundStyleChoices(GetSelectedSoundStyleId()); };

            Button soundTest = new AdventureButton();
            soundTest.Text = "Test";
            soundTest.Location = new Point(350, 61);
            soundTest.Size = new Size(74, 28);
            soundTest.Click += delegate
            {
                bool oldEnabled = settings.InterfaceSounds;
                string oldStyle = settings.SoundStyle;
                settings.InterfaceSounds = true;
                settings.SoundStyle = GetSelectedSoundStyleId();
                UiSoundManager.PlayConfirm(settings);
                settings.InterfaceSounds = oldEnabled;
                settings.SoundStyle = oldStyle;
            };
            sounds.Controls.Add(soundTest);

            Button openSounds = new AdventureButton();
            openSounds.Text = "Open Sounds Folder";
            openSounds.Location = new Point(18, 103);
            openSounds.Size = new Size(145, 28);
            openSounds.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(UiSoundManager.SoundsFolder);
                    string quotedSoundsFolder = ((char)34).ToString() + UiSoundManager.SoundsFolder + ((char)34).ToString();
                    Process.Start("explorer.exe", quotedSoundsFolder);
                }
                catch { }
            };
            sounds.Controls.Add(openSounds);

            Button refreshSounds = new AdventureButton();
            refreshSounds.Text = "Refresh";
            refreshSounds.Location = new Point(172, 103);
            refreshSounds.Size = new Size(86, 28);
            refreshSounds.Click += delegate
            {
                RefreshSoundStyleChoices(GetSelectedSoundStyleId());
            };
            sounds.Controls.Add(refreshSounds);

            Button validateSounds = new AdventureButton();
            validateSounds.Text = "Validate Folder...";
            validateSounds.Location = new Point(268, 103);
            validateSounds.Size = new Size(125, 28);
            validateSounds.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Choose a Dolphin NetPlay Launcher sound-theme folder to validate.";
                    dlg.SelectedPath = Directory.Exists(UiSoundManager.SoundsFolder)
                        ? UiSoundManager.SoundsFolder : AppDomain.CurrentDomain.BaseDirectory;

                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;

                    MessageBox.Show(
                        UiSoundManager.ValidateStyleFolder(dlg.SelectedPath),
                        "Sound Theme Validation",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    RefreshSoundStyleChoices(GetSelectedSoundStyleId());
                }
            };
            sounds.Controls.Add(validateSounds);

            Label soundsHelp = new AdventureLabel();
            soundsHelp.Text =
                "Custom theme: add a folder under Sounds with all 11 named WAV files.\n" +
                "Folder name becomes the theme name. Validate Folder explains anything missing or unreadable.";
            soundsHelp.Location = new Point(18, 139);
            soundsHelp.Size = new Size(605, 38);
            soundsHelp.ForeColor = Color.DimGray;
            sounds.Controls.Add(soundsHelp);

            GroupBox animation = new AdventureGroupBox();
            animation.Text = "Controller Cursor Animation";
            animation.Location = new Point(18, 692);
            animation.Size = new Size(649, 128);
            page.Controls.Add(animation);

            highlightAutoBox.Text = "Match monitor refresh rate automatically";
            highlightAutoBox.Location = new Point(18, 29);
            highlightAutoBox.AutoSize = true;
            highlightAutoBox.Checked = settings.ControllerHighlightMatchMonitor;
            animation.Controls.Add(highlightAutoBox);

            Label rateLabel = new AdventureLabel();
            rateLabel.Text = "Animation rate:";
            rateLabel.Location = new Point(37, 68);
            rateLabel.AutoSize = true;
            animation.Controls.Add(rateLabel);

            highlightHzBox.Location = new Point(137, 64);
            highlightHzBox.Size = new Size(80, 24);
            highlightHzBox.Minimum = 30;
            highlightHzBox.Maximum = 360;
            highlightHzBox.Increment = 1;
            highlightHzBox.Value = Math.Max(highlightHzBox.Minimum,
                Math.Min(highlightHzBox.Maximum, settings.ControllerHighlightHz));
            animation.Controls.Add(highlightHzBox);

            Label hzLabel = new AdventureLabel();
            hzLabel.Text = "Hz";
            hzLabel.Location = new Point(225, 68);
            hzLabel.AutoSize = true;
            animation.Controls.Add(hzLabel);

            detectedRefreshLabel.Location = new Point(285, 68);
            detectedRefreshLabel.AutoSize = true;
            detectedRefreshLabel.ForeColor = Color.DimGray;
            animation.Controls.Add(detectedRefreshLabel);

            int refreshHz = ControllerSelectionCursor.GetMonitorRefreshRate(this);
            detectedRefreshLabel.Text = "Detected display: " + refreshHz + " Hz";
            highlightHzBox.Enabled = !highlightAutoBox.Checked;
            highlightAutoBox.CheckedChanged += delegate
            {
                highlightHzBox.Enabled = !highlightAutoBox.Checked;
            };
        }

        private string GetSelectedSoundStyleId()
        {
            UiSoundStyleChoice choice = soundStyleBox.SelectedItem as UiSoundStyleChoice;
            return choice != null ? UiSoundManager.NormalizeStyleName(choice.Id) : "Adventure";
        }

        private void RefreshSoundStyleChoices(string preferredId)
        {
            preferredId = UiSoundManager.NormalizeStyleName(preferredId);
            List<UiSoundStyleChoice> choices = UiSoundManager.GetAvailableStyles();
            soundStyleBox.BeginUpdate();
            soundStyleBox.Items.Clear();

            int selected = -1;
            for (int i = 0; i < choices.Count; i++)
            {
                soundStyleBox.Items.Add(choices[i]);
                if (string.Equals(choices[i].Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    selected = i;
            }

            if (selected < 0)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i].Id, "Adventure", StringComparison.OrdinalIgnoreCase))
                    {
                        selected = i;
                        break;
                    }
                }
            }

            soundStyleBox.SelectedIndex = selected >= 0 ? selected : 0;
            soundStyleBox.EndUpdate();
        }

        private void BuildLibraryOptionsTab(TabPage page)
        {
            GroupBox display = new AdventureGroupBox();
            display.Text = "Library Display";
            display.Location = new Point(18, 18);
            display.Size = new Size(649, 145);
            page.Controls.Add(display);

            Label viewLabel = new AdventureLabel();
            viewLabel.Text = "Default view:";
            viewLabel.Location = new Point(18, 32);
            viewLabel.AutoSize = true;
            display.Controls.Add(viewLabel);

            libraryViewBox.DropDownStyle = ComboBoxStyle.DropDownList;
            libraryViewBox.Items.AddRange(new object[] { "Grid", "List" });
            libraryViewBox.Location = new Point(120, 28);
            libraryViewBox.Size = new Size(130, 24);
            libraryViewBox.SelectedItem = settings.LibraryView == "List" ? "List" : "Grid";
            display.Controls.Add(libraryViewBox);

            Label columnsLabel = new AdventureLabel();
            columnsLabel.Text = "Grid columns:";
            columnsLabel.Location = new Point(18, 72);
            columnsLabel.AutoSize = true;
            display.Controls.Add(columnsLabel);

            libraryColumnsOptionBox.Location = new Point(120, 68);
            libraryColumnsOptionBox.Size = new Size(70, 24);
            libraryColumnsOptionBox.Minimum = 3;
            libraryColumnsOptionBox.Maximum = 5;
            libraryColumnsOptionBox.Value = Math.Max(3, Math.Min(5, settings.LibraryGridColumns));
            display.Controls.Add(libraryColumnsOptionBox);

            Label help = new AdventureLabel();
            help.Text = "These are also remembered when you change them directly in the library.";
            help.Location = new Point(18, 108);
            help.AutoSize = true;
            help.ForeColor = Color.DimGray;
            display.Controls.Add(help);
        }

        private void BuildDolphinTab(TabPage page)
        {
            GroupBox install = new AdventureGroupBox();
            install.Text = "Dolphin Installation";
            install.Location = new Point(18, 18);
            install.Size = new Size(649, 145);
            page.Controls.Add(install);

            Label dolphinLabel = new AdventureLabel();
            dolphinLabel.Text = "Dolphin executable:";
            dolphinLabel.Location = new Point(18, 28);
            dolphinLabel.AutoSize = true;
            install.Controls.Add(dolphinLabel);

            dolphinExeBox.Location = new Point(18, 52);
            dolphinExeBox.Size = new Size(505, 24);
            dolphinExeBox.ReadOnly = true;
            dolphinExeBox.Text = settings.DolphinExe ?? "";
            install.Controls.Add(dolphinExeBox);

            Button change = new AdventureButton();
            change.Text = "Change...";
            change.Location = new Point(535, 50);
            change.Size = new Size(92, 28);
            change.Click += delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Locate Dolphin.exe";
                    dlg.Filter = "Dolphin Emulator (Dolphin.exe)|Dolphin.exe";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        dolphinExeBox.Text = dlg.FileName;
                }
            };
            install.Controls.Add(change);

            Label versionInfo = new AdventureLabel();
            versionInfo.Text = "Detected: " + dolphinVersion + "   •   Update track: " + updateTrack;
            versionInfo.Location = new Point(18, 96);
            versionInfo.Size = new Size(600, 24);
            versionInfo.ForeColor = Color.DimGray;
            install.Controls.Add(versionInfo);

            GroupBox timing = new AdventureGroupBox();
            timing.Text = "Updater / Closing";
            timing.Location = new Point(18, 180);
            timing.Size = new Size(649, 132);
            page.Controls.Add(timing);

            Label updateGraceLabel = new AdventureLabel();
            updateGraceLabel.Text = "Update-check close grace period:";
            updateGraceLabel.Location = new Point(18, 32);
            updateGraceLabel.AutoSize = true;
            timing.Controls.Add(updateGraceLabel);

            updateGraceBox.Location = new Point(220, 28);
            updateGraceBox.Size = new Size(85, 24);
            updateGraceBox.DecimalPlaces = 1;
            updateGraceBox.Increment = 0.5M;
            updateGraceBox.Minimum = 0.5M;
            updateGraceBox.Maximum = 5.0M;
            updateGraceBox.Value = Math.Max(updateGraceBox.Minimum,
                Math.Min(updateGraceBox.Maximum, settings.UpdateCloseGraceMs / 1000M));
            timing.Controls.Add(updateGraceBox);

            Label sec = new AdventureLabel();
            sec.Text = "seconds";
            sec.Location = new Point(313, 32);
            sec.AutoSize = true;
            timing.Controls.Add(sec);

            Label help = new AdventureLabel();
            help.Text =
                "How quickly temporary Dolphin closes after an update-check dialog finishes.";
            help.Location = new Point(18, 68);
            help.AutoSize = true;
            help.ForeColor = Color.DimGray;
            timing.Controls.Add(help);
        }

        private void BuildControllerTab(TabPage page)
        {
            page.AutoScroll = true;

            controllerEnabledBox.Text = "Enable controller navigation";
            controllerEnabledBox.Location = new Point(18, 20);
            controllerEnabledBox.AutoSize = true;
            controllerEnabledBox.Checked = settings.ControllerNavigation;
            controllerEnabledBox.TabIndex = 0;
            page.Controls.Add(controllerEnabledBox);

            Label help = new AdventureLabel();
            help.Text = "Uses SDL's gamepad layer so many controller types can share one navigation layout.";
            help.Location = new Point(18, 50);
            help.Size = new Size(645, 38);
            help.ForeColor = Color.DimGray;
            page.Controls.Add(help);

            Label activeLabel = new AdventureLabel();
            activeLabel.Text = "Active controller:";
            activeLabel.Location = new Point(18, 99);
            activeLabel.AutoSize = true;
            page.Controls.Add(activeLabel);

            controllerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            controllerBox.Location = new Point(130, 95);
            controllerBox.Size = new Size(420, 24);
            controllerBox.TabIndex = 1;
            page.Controls.Add(controllerBox);

            Button refresh = new AdventureButton();
            refresh.Text = "Refresh";
            refresh.Location = new Point(560, 94);
            refresh.Size = new Size(92, 27);
            refresh.TabIndex = 2;
            refresh.Click += delegate { RefreshControllerList(); };
            page.Controls.Add(refresh);

            leftStickBox.Text = "Allow left stick navigation";
            leftStickBox.Location = new Point(18, 138);
            leftStickBox.AutoSize = true;
            leftStickBox.Checked = settings.ControllerUseLeftStick;
            leftStickBox.TabIndex = 3;
            page.Controls.Add(leftStickBox);

            GroupBox prompts = new AdventureGroupBox();
            prompts.Text = "Controller Prompts";
            prompts.Location = new Point(18, 166);
            prompts.Size = new Size(634, 118);
            prompts.TabIndex = 4;
            page.Controls.Add(prompts);

            controllerPromptsBox.Text = "Show controller prompts on the launcher";
            controllerPromptsBox.Location = new Point(18, 26);
            controllerPromptsBox.AutoSize = true;
            controllerPromptsBox.Checked = settings.ShowControllerPrompts;
            controllerPromptsBox.TabIndex = 0;
            prompts.Controls.Add(controllerPromptsBox);

            Label styleLabel = new AdventureLabel();
            styleLabel.Text = "Prompt style:";
            styleLabel.Location = new Point(18, 62);
            styleLabel.AutoSize = true;
            prompts.Controls.Add(styleLabel);

            promptStyleBox.DropDownStyle = ComboBoxStyle.DropDownList;
            promptStyleBox.Items.AddRange(new object[] { "Xbox", "PlayStation", "Switch" });
            promptStyleBox.Location = new Point(100, 58);
            promptStyleBox.Size = new Size(140, 24);
            promptStyleBox.SelectedItem = settings.ControllerPromptStyle ?? "Xbox";
            if (promptStyleBox.SelectedIndex < 0) promptStyleBox.SelectedIndex = 0;
            promptStyleBox.TabIndex = 1;
            prompts.Controls.Add(promptStyleBox);

            Label gamesLabel = new AdventureLabel();
            gamesLabel.Text = "Games shortcut:";
            gamesLabel.Location = new Point(270, 62);
            gamesLabel.AutoSize = true;
            prompts.Controls.Add(gamesLabel);

            gamesButtonBox.DropDownStyle = ComboBoxStyle.DropDownList;
            gamesButtonBox.Items.AddRange(new object[] { "North face button", "West face button" });
            gamesButtonBox.Location = new Point(372, 58);
            gamesButtonBox.Size = new Size(190, 24);
            gamesButtonBox.SelectedIndex =
                string.Equals(settings.ControllerGamesButton, "West", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            gamesButtonBox.TabIndex = 2;
            prompts.Controls.Add(gamesButtonBox);

            GroupBox polling = new AdventureGroupBox();
            polling.Text = "Controller Input Polling";
            polling.Location = new Point(18, 300);
            polling.Size = new Size(634, 112);
            polling.TabIndex = 5;
            page.Controls.Add(polling);

            Label pollingLabel = new AdventureLabel();
            pollingLabel.Text = "Polling rate:";
            pollingLabel.Location = new Point(18, 31);
            pollingLabel.AutoSize = true;
            polling.Controls.Add(pollingLabel);

            controllerPollingBox.DropDownStyle = ComboBoxStyle.DropDownList;
            controllerPollingBox.Items.AddRange(new object[]
            {
                "60 Hz (baseline)",
                "120 Hz",
                "Match monitor (max 240 Hz)"
            });
            controllerPollingBox.Location = new Point(105, 27);
            controllerPollingBox.Size = new Size(225, 24);
            string pollingMode = ControllerNavigation.NormalizePollingMode(settings.ControllerPollingMode);
            controllerPollingBox.SelectedIndex = pollingMode == "120" ? 1
                : (pollingMode == "MatchMonitor" ? 2 : 0);
            controllerPollingBox.TabIndex = 0;
            polling.Controls.Add(controllerPollingBox);

            controllerPollingDetectedLabel.Location = new Point(348, 31);
            controllerPollingDetectedLabel.AutoSize = true;
            controllerPollingDetectedLabel.ForeColor = Color.DimGray;
            polling.Controls.Add(controllerPollingDetectedLabel);

            Label pollingHelp = new AdventureLabel();
            pollingHelp.Text = "Affects launcher input detection only. Dolphin gameplay remains controller-passive.";
            pollingHelp.Location = new Point(18, 68);
            pollingHelp.AutoSize = true;
            pollingHelp.ForeColor = Color.DimGray;
            polling.Controls.Add(pollingHelp);

            controllerPollingBox.SelectedIndexChanged += delegate { UpdateControllerPollingDetectedLabel(); };
            UpdateControllerPollingDetectedLabel();

            GroupBox test = new AdventureGroupBox();
            test.Text = "Controller Test";
            test.Location = new Point(18, 428);
            test.Size = new Size(634, 180);
            test.TabIndex = 6;
            page.Controls.Add(test);

            Label statusTitle = new AdventureLabel();
            statusTitle.Text = "Status:";
            statusTitle.Location = new Point(18, 30);
            statusTitle.AutoSize = true;
            test.Controls.Add(statusTitle);

            controllerStatusLabel.Location = new Point(78, 30);
            controllerStatusLabel.Size = new Size(530, 36);
            controllerStatusLabel.Text = "Checking SDL...";
            test.Controls.Add(controllerStatusLabel);

            Label inputTitle = new AdventureLabel();
            inputTitle.Text = "Last input:";
            inputTitle.Location = new Point(18, 78);
            inputTitle.AutoSize = true;
            test.Controls.Add(inputTitle);

            controllerInputLabel.Location = new Point(88, 78);
            controllerInputLabel.Size = new Size(500, 28);
            controllerInputLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            controllerInputLabel.Text = "—";
            test.Controls.Add(controllerInputLabel);

            Label mapping = new AdventureLabel();
            mapping.Text = "Navigation: Up/Down = move   •   Left/Right = change choices/values   •   South = select   •   East = back   •   Games = configurable North/West   •   shoulders = tabs/page";
            mapping.Location = new Point(18, 121);
            mapping.Size = new Size(595, 48);
            mapping.ForeColor = Color.DimGray;
            test.Controls.Add(mapping);

            RefreshControllerList();

            controllerUiTimer.Interval = 100;
            controllerUiTimer.Tick += delegate
            {
                if (controllerManager == null)
                    return;

                controllerStatusLabel.Text = controllerManager.StatusText;
                string input = controllerManager.LastInputText;
                controllerInputLabel.Text = string.IsNullOrWhiteSpace(input) ? "—" : input;
            };
            controllerUiTimer.Start();

            FormClosed += delegate { controllerUiTimer.Stop(); };
        }

        private string GetSelectedControllerPollingMode()
        {
            if (controllerPollingBox.SelectedIndex == 1) return "120";
            if (controllerPollingBox.SelectedIndex == 2) return "MatchMonitor";
            return "60";
        }

        private void UpdateControllerPollingDetectedLabel()
        {
            int detected = ControllerSelectionCursor.GetMonitorRefreshRate(this);
            string mode = GetSelectedControllerPollingMode();
            int target = mode == "120" ? 120
                : (mode == "MatchMonitor" ? Math.Max(30, Math.Min(240, detected)) : 60);
            controllerPollingDetectedLabel.Text = "Detected: " + detected + " Hz  •  Target: " + target + " Hz";
        }

        private void RefreshControllerList()
        {
            if (controllerManager == null)
                return;

            string desired = settings.ControllerPreference ?? "Auto";
            controllerManager.RefreshDevices(false);

            controllerBox.Items.Clear();
            controllerBox.Items.Add("Auto (first controller used)");

            foreach (string name in controllerManager.DeviceNames)
                controllerBox.Items.Add(name);

            int selected = 0;
            if (!desired.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < controllerBox.Items.Count; i++)
                {
                    if (string.Equals(controllerBox.Items[i].ToString(), desired, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = i;
                        break;
                    }
                }
            }
            controllerBox.SelectedIndex = selected;
        }

        private void BuildDiagnosticsTab(TabPage page)
        {
            Label intro = new AdventureLabel();
            intro.Text = "Detected paths and recent runtime events used for troubleshooting.";
            intro.Location = new Point(15, 14);
            intro.AutoSize = true;
            page.Controls.Add(intro);

            GroupBox pathsGroup = new AdventureGroupBox();
            pathsGroup.Text = "Paths";
            pathsGroup.Location = new Point(15, 39);
            pathsGroup.Size = new Size(652, 326);
            pathsGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(pathsGroup);

            pathList.Location = new Point(12, 24);
            pathList.Size = new Size(628, 250);
            pathList.View = View.Details;
            pathList.FullRowSelect = true;
            pathList.GridLines = true;
            pathList.HideSelection = false;
            pathList.ShowItemToolTips = false;
            pathList.Columns.Add("Item", 155);
            pathList.Columns.Add("Status", 105);
            pathList.Columns.Add("Path", 365);
            pathList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pathsGroup.Controls.Add(pathList);

            AddPathRow("Current ROM", romPath, false, "");
            AddPathRow("Launcher folder", AppDomain.CurrentDomain.BaseDirectory, true, "");
            AddPathRow("Launcher config.ini", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"), false, "");
            AddPathRow("Dolphin.exe", paths.DolphinExe, false, "");
            AddPathRow("DolphinTool.exe", paths.DolphinTool, false, "Used");
            AddPathRow("Dolphin user folder", paths.UserDir, true, "");
            AddPathRow("Dolphin.ini", paths.DolphinIni, false, "Used");
            AddPathRow("Qt.ini", paths.QtIni, false, "Used");
            AddPathRow("Built-in title DB", paths.BuiltInTitleDb, false, "Used");
            AddPathRow("Runtime log", DiagnosticsLog.LogPath, false, "Troubleshooting");

            // Keep Diagnostics simple and stable. The Paths table uses the available
            // width rather than any custom horizontal-scrolling machinery. Full values
            // remain available through Copy Path / Open Location / Copy Diagnostic Info.
            pathList.Scrollable = true;
            pathList.Resize += delegate
            {
                if (pathList.Columns.Count >= 3)
                    pathList.Columns[2].Width = Math.Max(365, pathList.ClientSize.Width - 264);
            };
            pathList.Columns[2].Width = Math.Max(365, pathList.ClientSize.Width - 264);

            Button open = new AdventureButton();
            open.Text = "Open Location";
            open.Location = new Point(12, 284);
            open.Size = new Size(110, 30);
            open.Click += delegate { OpenSelectedPath(); };
            pathsGroup.Controls.Add(open);

            Button copyPath = new AdventureButton();
            copyPath.Text = "Copy Path";
            copyPath.Location = new Point(132, 284);
            copyPath.Size = new Size(100, 30);
            copyPath.Click += delegate
            {
                if (pathList.SelectedItems.Count == 0) return;
                try { Clipboard.SetText(pathList.SelectedItems[0].SubItems[2].Text); } catch { }
            };
            pathsGroup.Controls.Add(copyPath);

            GroupBox runtimeGroup = new AdventureGroupBox();
            runtimeGroup.Text = "Runtime diagnostics";
            runtimeGroup.Location = new Point(15, 377);
            runtimeGroup.Size = new Size(652, 72);
            runtimeGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(runtimeGroup);

            Button openLog = new AdventureButton();
            openLog.Text = "Open Log";
            openLog.Location = new Point(12, 27);
            openLog.Size = new Size(90, 30);
            openLog.Click += delegate
            {
                try
                {
                    if (File.Exists(DiagnosticsLog.LogPath))
                        Process.Start("notepad.exe", DiagnosticsLog.LogPath);
                }
                catch { }
            };
            runtimeGroup.Controls.Add(openLog);

            Button clearLog = new AdventureButton();
            clearLog.Text = "Clear Log";
            clearLog.Location = new Point(112, 27);
            clearLog.Size = new Size(90, 30);
            clearLog.Click += delegate
            {
                DiagnosticsLog.Clear();
                MessageBox.Show("Runtime diagnostic log cleared.", "Dolphin NetPlay Launcher Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            runtimeGroup.Controls.Add(clearLog);

            Button copyDiag = new AdventureButton();
            copyDiag.Text = "Copy Diagnostic Info";
            // Keep this core support action directly beside Open Log / Clear Log.
            // Right-anchoring it caused it to disappear from the visible Runtime
            // diagnostics row on the user's machine even though the control existed.
            copyDiag.Location = new Point(212, 27);
            copyDiag.Size = new Size(180, 30);
            copyDiag.Click += delegate
            {
                try
                {
                    Clipboard.SetText(BuildDiagnosticText());
                    MessageBox.Show(
                        "Diagnostic information copied to the clipboard.\n\nUser-profile paths are redacted automatically.",
                        "Dolphin NetPlay Launcher Diagnostics",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch { }
            };
            runtimeGroup.Controls.Add(copyDiag);
        }

        private void BuildAboutTab(TabPage page)
        {
            GroupBox project = new AdventureGroupBox();
            project.Text = "Dolphin NetPlay Launcher";
            project.Location = new Point(18, 18);
            project.Size = new Size(649, 165);
            page.Controls.Add(project);

            Label title = new AdventureLabel();
            title.Text = Program.AppDisplayName;
            title.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            title.Location = new Point(18, 27);
            title.AutoSize = true;
            project.Controls.Add(title);

            Label versionLabel = new AdventureLabel();
            versionLabel.Text = "Version " + Program.AppVersion;
            versionLabel.Location = new Point(20, 64);
            versionLabel.AutoSize = true;
            project.Controls.Add(versionLabel);

            Label purpose = new AdventureLabel();
            purpose.Text = "Steam-friendly front-end for hosting and joining sessions through stock Dolphin NetPlay.";
            purpose.Location = new Point(20, 91);
            purpose.Size = new Size(605, 24);
            project.Controls.Add(purpose);

            Label dolphinInfo = new AdventureLabel();
            dolphinInfo.Text = "Detected Dolphin: " + dolphinVersion + "   •   Update track: " + updateTrack;
            dolphinInfo.Location = new Point(20, 119);
            dolphinInfo.Size = new Size(605, 24);
            dolphinInfo.ForeColor = Color.DimGray;
            project.Controls.Add(dolphinInfo);

            GroupBox notices = new AdventureGroupBox();
            notices.Text = "Project & Attribution";
            notices.Location = new Point(18, 200);
            notices.Size = new Size(649, 230);
            page.Controls.Add(notices);

            Label unofficial = new AdventureLabel();
            unofficial.Text =
                "Unofficial third-party project. Dolphin NetPlay Launcher is not affiliated with or endorsed by the Dolphin Emulator project.";
            unofficial.Location = new Point(18, 27);
            unofficial.Size = new Size(605, 42);
            notices.Controls.Add(unofficial);

            Label ai = new AdventureLabel();
            ai.Text =
                "AI Development Disclosure: The application code was generated by ChatGPT. The project owner provided requirements/prompts, testing, bug reports, and behavior/design decisions.";
            ai.Location = new Point(18, 78);
            ai.Size = new Size(605, 54);
            notices.Controls.Add(ai);

            Label logo = new AdventureLabel();
            logo.Text =
                "Icon attribution: incorporates/modifies the Dolphin Emulator logo created by MayImilae. Original and modified icon artwork is handled under CC BY-SA 4.0 attribution/share-alike terms.";
            logo.Location = new Point(18, 141);
            logo.Size = new Size(605, 54);
            notices.Controls.Add(logo);

            Button copy = new AdventureButton();
            copy.Text = "Copy Version Info";
            copy.Location = new Point(18, 448);
            copy.Size = new Size(135, 30);
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(
                        Program.AppDisplayName + " " + Program.AppVersion + Environment.NewLine +
                        "Dolphin: " + dolphinVersion + Environment.NewLine +
                        "Update track: " + updateTrack);
                }
                catch { }
            };
            page.Controls.Add(copy);

            Button openFolder = new AdventureButton();
            openFolder.Text = "Open Launcher Folder";
            openFolder.Location = new Point(163, 448);
            openFolder.Size = new Size(145, 30);
            openFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); } catch { }
            };
            page.Controls.Add(openFolder);
        }

        private void AddPathRow(string name, string path, bool directory, string role)
        {
            bool exists = directory ? Directory.Exists(path) : File.Exists(path);
            string status = exists ? "Found" : "Missing";
            if (!string.IsNullOrWhiteSpace(role))
                status += " (" + role + ")";

            ListViewItem item = new ListViewItem(name);
            item.SubItems.Add(status);
            item.SubItems.Add(path ?? "");
            pathList.Items.Add(item);
        }

        private void OpenSelectedPath()
        {
            if (pathList.SelectedItems.Count == 0)
                return;

            string path = pathList.SelectedItems[0].SubItems[2].Text;
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start("explorer.exe", path);
                }
                else if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    string parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                        Process.Start("explorer.exe", parent);
                    else
                        MessageBox.Show("That location does not currently exist.", "Dolphin NetPlay Launcher Diagnostics",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
        }

        private string BuildDiagnosticText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Dolphin NetPlay Launcher Diagnostics");
            sb.AppendLine("Dolphin NetPlay Launcher Version: " + Program.AppVersion);
            sb.AppendLine("Dolphin Version: " + dolphinVersion);
            sb.AppendLine("Update Track: " + updateTrack);
            sb.AppendLine("Theme: " + (appearanceBox.SelectedItem ?? "System"));
            sb.AppendLine("Interface font: " + (interfaceStyleBox.SelectedItem ?? "Default"));
            sb.AppendLine("Accent style: " +
                (accentStyleBox.SelectedIndex == 1 ? "Animated Gradient" : "System Accent"));
            sb.AppendLine("Library default view: " + (libraryViewBox.SelectedItem ?? "Grid"));
            sb.AppendLine("Library grid columns: " + libraryColumnsOptionBox.Value.ToString("0"));
            sb.AppendLine("Automatically close Dolphin: " + autoCloseBox.Checked);
            sb.AppendLine("Return after Dolphin closes: " + returnAfterDolphinCloseBox.Checked);
            sb.AppendLine("NetPlay close grace: " + graceBox.Value.ToString("0.0") + " s");
            sb.AppendLine("Update-check close grace: " + updateGraceBox.Value.ToString("0.0") + " s");
            sb.AppendLine("Automation warning: " + warningBox.Checked);
            sb.AppendLine("Remember last Host/Join: " + rememberModeBox.Checked);
            sb.AppendLine("Controller navigation: " + controllerEnabledBox.Checked);
            sb.AppendLine("Left stick navigation: " + leftStickBox.Checked);
            sb.AppendLine("Controller input polling: " + GetSelectedControllerPollingMode());
            sb.AppendLine("Controller prompt style: " + (promptStyleBox.SelectedItem ?? "Xbox"));
            sb.AppendLine("Games shortcut: " + (gamesButtonBox.SelectedIndex == 1 ? "West face button" : "North face button"));
            sb.AppendLine("Highlight refresh mode: " + (highlightAutoBox.Checked ? "Match monitor" : "Manual"));
            sb.AppendLine("Highlight animation rate: " + highlightHzBox.Value.ToString("0") + " Hz");
            sb.AppendLine("Interface sounds: " + interfaceSoundsBox.Checked);
            sb.AppendLine("Sound style: " + soundStyleBox.Text);
            sb.AppendLine("Controller preference: " + (controllerBox.SelectedItem != null ? controllerBox.SelectedItem.ToString() : "Auto"));
            if (controllerManager != null)
                sb.AppendLine("Controller status: " + controllerManager.StatusText);
            sb.AppendLine();

            foreach (ListViewItem item in pathList.Items)
            {
                string path = RedactUserPath(item.SubItems[2].Text);
                sb.AppendLine(item.Text + ": " + item.SubItems[1].Text + " | " + path);
            }

            sb.AppendLine();
            sb.AppendLine("Recent Runtime Log (last 80 lines; sensitive connection targets are never logged):");
            sb.AppendLine(DiagnosticsLog.ReadRecent(80));

            return sb.ToString();
        }

        private static string RedactUserPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile) &&
                path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            {
                return "%USERPROFILE%" + path.Substring(profile.Length);
            }
            return path;
        }

        private void ApplySettings()
        {
            ApplySettingsTo(settings, true);
        }

        private void ApplySettingsTo(DnlSettings target, bool applyRuntime)
        {
            if (target == null) return;

            target.DolphinExe = dolphinExeBox.Text.Trim();
            string selectedTheme = appearanceBox.SelectedItem != null ? appearanceBox.SelectedItem.ToString() : "System";
            target.ThemeStyle = selectedTheme == "Adventure Blue" ? "AdventureBlue"
                : (selectedTheme == "OLED Black" ? "OledBlack"
                : (selectedTheme == "GameCube Indigo" ? "GameCubeIndigo"
                : (selectedTheme == "GameCube Spice Orange" ? "GameCubeSpice" : "Default")));
            target.Appearance =
                (selectedTheme == "Adventure Blue" || selectedTheme == "OLED Black" ||
                 selectedTheme == "GameCube Indigo" || selectedTheme == "GameCube Spice Orange")
                ? "Dark" : selectedTheme;
            target.AnimatedThemeBackground = animatedThemeBackgroundBox.Checked;
            target.InterfaceStyle = interfaceStyleBox.SelectedItem != null && interfaceStyleBox.SelectedItem.ToString() == "Outfit"
                ? "Outfit" : "Default";
            target.AccentStyle = accentStyleBox.SelectedIndex == 1
                ? "AnimatedGradient" : "SystemAccent";
            target.InterfaceSounds = interfaceSoundsBox.Checked;
            target.SoundStyle = GetSelectedSoundStyleId();
            if (applyRuntime)
                UiSoundManager.Configure(target);
            target.LibraryView = libraryViewBox.SelectedItem != null &&
                libraryViewBox.SelectedItem.ToString() == "List" ? "List" : "Grid";
            target.LibraryGridColumns = (int)libraryColumnsOptionBox.Value;
            target.AutoCloseDolphin = autoCloseBox.Checked;
            target.NetPlayCloseGraceMs = (int)(graceBox.Value * 1000M);
            target.UpdateCloseGraceMs = (int)(updateGraceBox.Value * 1000M);
            target.ShowAutomationWarning = warningBox.Checked;
            target.AutoReturnAfterFailedJoin = failedJoinReturnBox.Checked;
            target.ReturnToLauncherAfterDolphinClose = returnAfterDolphinCloseBox.Checked;
            target.RememberLastMode = rememberModeBox.Checked;
            if (!target.RememberLastMode)
                target.LastMode = "Host";

            target.OpenLibraryOnStandalone = openLibraryStandaloneBox.Checked;
            target.OpenLibraryOnSteam = openLibrarySteamBox.Checked;
            target.ShowControllerPrompts = controllerPromptsBox.Checked;
            target.ControllerPromptStyle = promptStyleBox.SelectedItem != null ? promptStyleBox.SelectedItem.ToString() : "Xbox";
            target.ControllerGamesButton = gamesButtonBox.SelectedIndex == 1 ? "West" : "North";
            target.ControllerNavigation = controllerEnabledBox.Checked;
            target.ControllerUseLeftStick = leftStickBox.Checked;
            target.ControllerPollingMode = GetSelectedControllerPollingMode();
            target.ControllerHighlightMatchMonitor = highlightAutoBox.Checked;
            target.ControllerHighlightHz = (int)highlightHzBox.Value;
            if (controllerBox.SelectedIndex <= 0)
                target.ControllerPreference = "Auto";
            else
                target.ControllerPreference = controllerBox.SelectedItem.ToString();

            if (applyRuntime && controllerManager != null)
                controllerManager.ApplyPreference(target.ControllerPreference);
        }

    }


    internal enum ControllerAction
    {
        None,
        Up,
        Down,
        Left,
        Right,
        Accept,
        Cancel,
        PreviousTab,
        NextTab,
        ToggleGames,
        ToggleSessions,
        FocusPaste,
        FocusPrimary,
        FocusClear
    }

    internal sealed class SdlControllerManager : IDisposable
    {
        private const uint SDL_INIT_GAMEPAD = 0x00002000;

        private const int SDL_GAMEPAD_AXIS_LEFTX = 0;
        private const int SDL_GAMEPAD_AXIS_LEFTY = 1;

        private const int SDL_GAMEPAD_BUTTON_SOUTH = 0;
        private const int SDL_GAMEPAD_BUTTON_EAST = 1;
        private const int SDL_GAMEPAD_BUTTON_WEST = 2;
        private const int SDL_GAMEPAD_BUTTON_NORTH = 3;
        private const int SDL_GAMEPAD_BUTTON_BACK = 4;
        private const int SDL_GAMEPAD_BUTTON_START = 6;
        private const int SDL_GAMEPAD_BUTTON_RIGHT_STICK = 8;
        private const int SDL_GAMEPAD_BUTTON_LEFT_SHOULDER = 9;
        private const int SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER = 10;
        private const int SDL_GAMEPAD_BUTTON_DPAD_UP = 11;
        private const int SDL_GAMEPAD_BUTTON_DPAD_DOWN = 12;
        private const int SDL_GAMEPAD_BUTTON_DPAD_LEFT = 13;
        private const int SDL_GAMEPAD_BUTTON_DPAD_RIGHT = 14;

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SDL_InitSubSystem(uint flags);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetGamepads(out int count);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_OpenGamepad(uint instanceId);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseGamepad(IntPtr gamepad);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_UpdateGamepads();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();

        [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr memory);

        private sealed class Device
        {
            public uint Id;
            public IntPtr Handle;
            public string Name;
            public bool PrevAccept;
            public bool PrevCancel;
            public bool PrevWest;
            public bool PrevNorth;
            public bool PrevBack;
            public bool PrevStart;
            public bool PrevR3;
            public bool PrevLB;
            public bool PrevRB;
            public DirectionRepeater Directions = new DirectionRepeater();
        }

        private sealed class DirectionRepeater
        {
            private ControllerAction held = ControllerAction.None;
            private DateTime nextRepeat = DateTime.MinValue;

            public ControllerAction Update(ControllerAction now)
            {
                DateTime current = DateTime.Now;

                if (now == ControllerAction.None)
                {
                    held = ControllerAction.None;
                    nextRepeat = DateTime.MinValue;
                    return ControllerAction.None;
                }

                if (now != held)
                {
                    held = now;
                    nextRepeat = current.AddMilliseconds(240);
                    return now;
                }

                if (current >= nextRepeat)
                {
                    nextRepeat = current.AddMilliseconds(55);
                    return now;
                }

                return ControllerAction.None;
            }
        }

        private readonly DnlSettings settings;
        private readonly List<Device> devices = new List<Device>();
        private bool initialized;
        private DateTime nextRefresh = DateTime.MinValue;
        private Device activeDevice;
        private string preference;

        public string StatusText { get; private set; }
        public string LastInputText { get; private set; }
        public bool NavigationEnabled
        {
            get { return settings == null || settings.ControllerNavigation; }
        }
        public IList<string> DeviceNames
        {
            get
            {
                List<string> names = new List<string>();
                foreach (Device d in devices)
                    names.Add(d.Name);
                return names;
            }
        }

        public SdlControllerManager(DnlSettings settings)
        {
            this.settings = settings;
            this.preference = settings != null ? settings.ControllerPreference : "Auto";
            if (string.IsNullOrWhiteSpace(preference))
                preference = "Auto";

            Initialize();
        }

        private void Initialize()
        {
            try
            {
                initialized = SDL_InitSubSystem(SDL_INIT_GAMEPAD);
                if (!initialized)
                {
                    StatusText = "SDL could not initialize gamepad support: " + GetSdlError();
                    return;
                }

                RefreshDevices(false);
            }
            catch (DllNotFoundException)
            {
                StatusText = "SDL3.dll was not found beside Dolphin NetPlay Launcher.";
            }
            catch (BadImageFormatException)
            {
                StatusText = "SDL3.dll has the wrong architecture for this launcher build.";
            }
            catch (EntryPointNotFoundException)
            {
                StatusText = "The installed SDL3.dll is too old or incompatible.";
            }
            catch (Exception ex)
            {
                StatusText = "Controller support could not start: " + ex.Message;
            }
        }

        public void ApplyPreference(string value)
        {
            preference = string.IsNullOrWhiteSpace(value) ? "Auto" : value;
            activeDevice = null;

            if (!preference.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Device d in devices)
                {
                    if (string.Equals(d.Name, preference, StringComparison.OrdinalIgnoreCase))
                    {
                        activeDevice = d;
                        break;
                    }
                }
            }

            UpdateStatus();
        }

        public void RefreshDevices(bool preserveInputState = false)
        {
            if (!initialized)
                return;

            try
            {
                // Periodic refreshes happen while the launcher is actively polling.  RC40
                // preserves edge/repeat state across those handle reopenings so a button that
                // is still physically held cannot look like a brand-new press merely because
                // the two-second SDL refresh created a new Device object. Explicit lifecycle
                // refreshes (startup, Options refresh, Dolphin -> launcher return) intentionally
                // keep the historical reset behavior.
                List<Device> previousDevices = preserveInputState
                    ? new List<Device>(devices)
                    : null;
                uint previousActiveId = preserveInputState && activeDevice != null ? activeDevice.Id : 0;
                string previousActiveName = preserveInputState && activeDevice != null ? activeDevice.Name : null;

                CloseDevices();

                SDL_UpdateGamepads();
                int count;
                IntPtr ids = SDL_GetGamepads(out count);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        uint id = unchecked((uint)Marshal.ReadInt32(ids, i * 4));
                        IntPtr handle = SDL_OpenGamepad(id);
                        if (handle == IntPtr.Zero)
                            continue;

                        string name = PtrToString(SDL_GetGamepadName(handle));
                        if (string.IsNullOrWhiteSpace(name))
                            name = "Controller " + (i + 1);

                        Device opened = new Device { Id = id, Handle = handle, Name = name };

                        if (previousDevices != null)
                        {
                            Device previous = null;
                            foreach (Device candidate in previousDevices)
                            {
                                if (candidate.Id == id)
                                {
                                    previous = candidate;
                                    break;
                                }
                            }
                            if (previous == null)
                            {
                                foreach (Device candidate in previousDevices)
                                {
                                    if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        previous = candidate;
                                        break;
                                    }
                                }
                            }

                            if (previous != null)
                            {
                                opened.PrevAccept = previous.PrevAccept;
                                opened.PrevCancel = previous.PrevCancel;
                                opened.PrevWest = previous.PrevWest;
                                opened.PrevNorth = previous.PrevNorth;
                                opened.PrevBack = previous.PrevBack;
                                opened.PrevStart = previous.PrevStart;
                                opened.PrevR3 = previous.PrevR3;
                                opened.PrevLB = previous.PrevLB;
                                opened.PrevRB = previous.PrevRB;
                                opened.Directions = previous.Directions;
                            }
                        }

                        devices.Add(opened);
                    }
                }
                finally
                {
                    if (ids != IntPtr.Zero)
                        SDL_free(ids);
                }

                nextRefresh = DateTime.Now.AddSeconds(2);
                ApplyPreference(preference);

                if (preserveInputState && preference.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                    (previousActiveId != 0 || !string.IsNullOrWhiteSpace(previousActiveName)))
                {
                    foreach (Device d in devices)
                    {
                        if ((previousActiveId != 0 && d.Id == previousActiveId) ||
                            (!string.IsNullOrWhiteSpace(previousActiveName) &&
                             string.Equals(d.Name, previousActiveName, StringComparison.OrdinalIgnoreCase)))
                        {
                            activeDevice = d;
                            break;
                        }
                    }
                    UpdateStatus();
                }
            }
            catch (Exception ex)
            {
                StatusText = "Controller refresh failed: " + ex.Message;
            }
        }

        public ControllerAction Poll(bool allowLeftStick)
        {
            if (!initialized)
                return ControllerAction.None;

            try
            {
                if (DateTime.Now >= nextRefresh)
                    RefreshDevices(true);

                SDL_UpdateGamepads();

                Device d = activeDevice;

                if (d == null && preference.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Device candidate in devices)
                    {
                        ControllerAction candidateAction = ReadAction(candidate, allowLeftStick, true);
                        if (candidateAction != ControllerAction.None)
                        {
                            activeDevice = candidate;
                            d = candidate;
                            UpdateStatus();
                            RecordInput(candidateAction);
                            return candidateAction;
                        }
                    }
                    return ControllerAction.None;
                }

                if (d == null)
                {
                    return ControllerAction.None;
                }

                ControllerAction action = ReadAction(d, allowLeftStick, false);
                if (action != ControllerAction.None)
                {
                    RecordInput(action);
                    return action;
                }

                return ControllerAction.None;
            }
            catch
            {
                return ControllerAction.None;
            }
        }

        private ControllerAction ReadAction(Device d, bool allowLeftStick, bool autoProbe)
        {
            bool accept = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_SOUTH);
            bool cancel = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_EAST);
            bool west = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_WEST);
            bool north = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_NORTH);
            bool back = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_BACK);
            bool start = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_START);
            bool r3 = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_RIGHT_STICK);
            bool lb = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER);
            bool rb = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER);

            Action rememberButtons = delegate
            {
                d.PrevAccept = accept;
                d.PrevCancel = cancel;
                d.PrevWest = west;
                d.PrevNorth = north;
                d.PrevBack = back;
                d.PrevStart = start;
                d.PrevR3 = r3;
                d.PrevLB = lb;
                d.PrevRB = rb;
            };

            if (accept && !d.PrevAccept)
            {
                rememberButtons();
                return ControllerAction.Accept;
            }
            if (cancel && !d.PrevCancel)
            {
                rememberButtons();
                return ControllerAction.Cancel;
            }

            bool gamesOnWest = settings != null &&
                string.Equals(settings.ControllerGamesButton, "West", StringComparison.OrdinalIgnoreCase);
            bool gamesPressed = gamesOnWest ? west : north;
            bool gamesWasPressed = gamesOnWest ? d.PrevWest : d.PrevNorth;
            if (gamesPressed && !gamesWasPressed)
            {
                rememberButtons();
                return ControllerAction.ToggleGames;
            }

            // The other North/West face button acts as a quick Paste-focus shortcut.
            bool pastePressed = gamesOnWest ? north : west;
            bool pasteWasPressed = gamesOnWest ? d.PrevNorth : d.PrevWest;
            if (pastePressed && !pasteWasPressed)
            {
                rememberButtons();
                return ControllerAction.FocusPaste;
            }

            if (back && !d.PrevBack)
            {
                rememberButtons();
                return ControllerAction.FocusClear;
            }

            if (start && !d.PrevStart)
            {
                rememberButtons();
                return ControllerAction.FocusPrimary;
            }
            if (r3 && !d.PrevR3)
            {
                rememberButtons();
                return ControllerAction.ToggleSessions;
            }
            if (lb && !d.PrevLB)
            {
                rememberButtons();
                return ControllerAction.PreviousTab;
            }
            if (rb && !d.PrevRB)
            {
                rememberButtons();
                return ControllerAction.NextTab;
            }

            rememberButtons();

            bool up = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_DPAD_UP);
            bool down = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_DPAD_DOWN);
            bool left = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_DPAD_LEFT);
            bool right = SDL_GetGamepadButton(d.Handle, SDL_GAMEPAD_BUTTON_DPAD_RIGHT);

            if (allowLeftStick)
            {
                short x = SDL_GetGamepadAxis(d.Handle, SDL_GAMEPAD_AXIS_LEFTX);
                short y = SDL_GetGamepadAxis(d.Handle, SDL_GAMEPAD_AXIS_LEFTY);
                const int threshold = 16000;
                left = left || x < -threshold;
                right = right || x > threshold;
                up = up || y < -threshold;
                down = down || y > threshold;
            }

            ControllerAction direction = ControllerAction.None;
            if (up) direction = ControllerAction.Up;
            else if (down) direction = ControllerAction.Down;
            else if (left) direction = ControllerAction.Left;
            else if (right) direction = ControllerAction.Right;

            return d.Directions.Update(direction);
        }

        private void RecordInput(ControllerAction action)
        {
            switch (action)
            {
                case ControllerAction.Accept: LastInputText = "South / A-style button"; break;
                case ControllerAction.Cancel: LastInputText = "East / B-style button"; break;
                case ControllerAction.PreviousTab: LastInputText = "Left shoulder"; break;
                case ControllerAction.NextTab: LastInputText = "Right shoulder"; break;
                case ControllerAction.ToggleGames:
                    LastInputText = (settings != null && string.Equals(settings.ControllerGamesButton, "West", StringComparison.OrdinalIgnoreCase))
                        ? "West face button / Games"
                        : "North face button / Games";
                    break;
                case ControllerAction.ToggleSessions: LastInputText = "R3 / Sessions"; break;
                case ControllerAction.FocusPaste:
                    LastInputText = (settings != null && string.Equals(settings.ControllerGamesButton, "West", StringComparison.OrdinalIgnoreCase))
                        ? "North face button / Paste"
                        : "West face button / Paste";
                    break;
                case ControllerAction.FocusPrimary: LastInputText = "Start / primary action"; break;
                case ControllerAction.FocusClear: LastInputText = "Select / Clear"; break;
                case ControllerAction.Up: LastInputText = "Up"; break;
                case ControllerAction.Down: LastInputText = "Down"; break;
                case ControllerAction.Left: LastInputText = "Left"; break;
                case ControllerAction.Right: LastInputText = "Right"; break;
            }
        }

        private void UpdateStatus()
        {
            if (!initialized)
                return;

            if (devices.Count == 0)
            {
                StatusText = "No SDL gamepads detected.";
                return;
            }

            if (activeDevice != null)
            {
                StatusText = "Active: " + activeDevice.Name;
                return;
            }

            if (preference.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                StatusText = devices.Count + " controller(s) detected. Press a control to choose the active controller.";
            else
                StatusText = "Preferred controller is not currently connected.";
        }

        private static string GetSdlError()
        {
            try { return PtrToString(SDL_GetError()); }
            catch { return "Unknown SDL error"; }
        }

        private static string PtrToString(IntPtr ptr)
        {
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ptr);
        }

        private void CloseDevices()
        {
            foreach (Device d in devices)
            {
                try
                {
                    if (d.Handle != IntPtr.Zero)
                        SDL_CloseGamepad(d.Handle);
                }
                catch { }
            }
            devices.Clear();
            activeDevice = null;
        }

        public void Dispose()
        {
            DiagnosticsLog.Write("SHUTDOWN", "Beginning SDL controller manager disposal. Initialized=" + initialized + "; devices=" + devices.Count + ".");

            if (!initialized)
            {
                DiagnosticsLog.Write("SHUTDOWN", "SDL controller manager was not initialized; disposal complete.");
                return;
            }

            try
            {
                DiagnosticsLog.Write("SHUTDOWN", "Closing SDL gamepad handles...");
                CloseDevices();
                DiagnosticsLog.Write("SHUTDOWN", "SDL gamepad handles closed.");
                DiagnosticsLog.Write("SHUTDOWN", "Calling SDL_QuitSubSystem(SDL_INIT_GAMEPAD)...");
                SDL_QuitSubSystem(SDL_INIT_GAMEPAD);
                DiagnosticsLog.Write("SHUTDOWN", "SDL_QuitSubSystem returned.");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Exception("SDL controller manager disposal failed", ex);
            }

            initialized = false;
            DiagnosticsLog.Write("SHUTDOWN", "SDL controller manager disposal complete.");
        }
    }

    internal sealed class ControllerSelectionCursor : IDisposable
    {
        private sealed class GlowAdorner : Control
        {
            private readonly DnlSettings settings;

            public GlowAdorner(DnlSettings settings)
            {
                this.settings = settings;
                SetStyle(ControlStyles.SupportsTransparentBackColor |
                         ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Transparent;
                Enabled = false;
                TabStop = false;
            }

            private GraphicsPath RoundedRect(RectangleF r, float radius)
            {
                GraphicsPath path = new GraphicsPath();
                float d = radius * 2F;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                RectangleF outer = new RectangleF(5, 5, Math.Max(1, Width - 11), Math.Max(1, Height - 11));
                RectangleF middle = new RectangleF(7, 7, Math.Max(1, Width - 15), Math.Max(1, Height - 15));
                RectangleF inner = new RectangleF(9, 9, Math.Max(1, Width - 19), Math.Max(1, Height - 19));

                using (GraphicsPath p1 = RoundedRect(outer, 9F))
                using (GraphicsPath p2 = RoundedRect(middle, 8F))
                using (GraphicsPath p3 = RoundedRect(inner, 7F))
                {
                    if (AccentVisuals.Animated(settings))
                    {
                        Rectangle bounds = new Rectangle(0, 0,
                            Math.Max(2, Width), Math.Max(2, Height));
                        using (LinearGradientBrush b1 = AccentVisuals.CreateGradient(bounds, 65))
                        using (LinearGradientBrush b2 = AccentVisuals.CreateGradient(bounds, 125))
                        using (LinearGradientBrush b3 = AccentVisuals.CreateGradient(bounds, 245))
                        using (Pen glow1 = new Pen(b1, 7F))
                        using (Pen glow2 = new Pen(b2, 4F))
                        using (Pen core = new Pen(b3, 2.5F))
                        {
                            glow1.LineJoin = LineJoin.Round;
                            glow2.LineJoin = LineJoin.Round;
                            core.LineJoin = LineJoin.Round;
                            e.Graphics.DrawPath(glow1, p1);
                            e.Graphics.DrawPath(glow2, p2);
                            e.Graphics.DrawPath(core, p3);
                        }
                    }
                    else
                    {
                        Color accent = SystemColors.Highlight;
                        using (Pen glow1 = new Pen(Color.FromArgb(42, accent), 7F))
                        using (Pen glow2 = new Pen(Color.FromArgb(92, accent), 4F))
                        using (Pen core = new Pen(Color.FromArgb(225, accent), 2F))
                        {
                            e.Graphics.DrawPath(glow1, p1);
                            e.Graphics.DrawPath(glow2, p2);
                            e.Graphics.DrawPath(core, p3);
                        }
                    }
                }
            }
        }

        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(
            string lpszDeviceName,
            int iModeNum,
            ref DEVMODE lpDevMode);

        private readonly Form form;
        private readonly DnlSettings settings;
        private readonly GlowAdorner adorner;
        private readonly System.Windows.Forms.Timer timer;
        private RectangleF currentRect;
        private RectangleF targetRect;
        private bool hasRect;
        private bool controllerVisible;
        private Control targetControl;
        private Rectangle? clipToFormRect;
        // The glow's Region depends on local width/height, not screen position. Cache
        // the size so a high-refresh cursor can move/repaint without allocating two
        // GraphicsPaths + a Region on every timer tick after its size has settled.
        private Size adornerRegionSize = Size.Empty;
        public ControllerSelectionCursor(Form form, DnlSettings settings)
        {
            this.form = form;
            this.settings = settings;

            adorner = new GlowAdorner(settings);
            adorner.Visible = false;
            form.Controls.Add(adorner);
            adorner.BringToFront();

            timer = new System.Windows.Forms.Timer();
            ApplyAnimationRate();
            timer.Tick += delegate { Animate(); };
            // Do not run a monitor-rate cursor timer before the user has actually
            // navigated with a controller. NotifyControllerNavigation starts it when
            // a real controller cursor exists.
            form.Shown += delegate { ApplyAnimationRate(); };
            form.LocationChanged += delegate { if (settings.ControllerHighlightMatchMonitor) ApplyAnimationRate(); };
            form.FormClosed += delegate { Dispose(); };

            HookMouse(form);
        }

        private void HookMouse(Control root)
        {
            root.MouseDown += delegate { HideForMouse(); };
            root.MouseWheel += delegate { HideForMouse(); };
            root.GotFocus += delegate
            {
                // If controller navigation owns the cursor, follow deterministic
                // programmatic focus restoration after a Games/Sessions transition.
                // A mouse click calls HideForMouse first, so mouse focus never
                // resurrects the controller cursor.
                if (controllerVisible && root.Visible && root.Enabled)
                {
                    targetControl = root;
                    clipToFormRect = null;
                    hasRect = false;
                }
            };
            root.ControlAdded += delegate(object sender, ControlEventArgs e)
            {
                if (e.Control != adorner)
                    HookMouse(e.Control);
            };

            foreach (Control child in root.Controls)
                if (child != adorner)
                    HookMouse(child);
        }

        private void HideForMouse()
        {
            controllerVisible = false;
            targetControl = null;
            clipToFormRect = null;
            adorner.Visible = false;
            hasRect = false;
            if (timer.Enabled) timer.Stop();
        }

        private RectangleF GetTargetRect(Control control)
        {
            Rectangle screen = control.RectangleToScreen(control.ClientRectangle);
            Point topLeft = form.PointToClient(new Point(screen.Left, screen.Top));
            const int pad = 11;

            return new RectangleF(
                topLeft.X - pad,
                topLeft.Y - pad,
                screen.Width + pad * 2,
                screen.Height + pad * 2);
        }

        public static int GetMonitorRefreshRate(Control control)
        {
            try
            {
                Screen screen = Screen.FromControl(control);
                DEVMODE mode = new DEVMODE();
                mode.dmDeviceName = new string('\0', 32);
                mode.dmFormName = new string('\0', 32);
                mode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));

                if (EnumDisplaySettings(screen.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    int hz = (int)mode.dmDisplayFrequency;
                    if (hz >= 30 && hz <= 1000)
                        return hz;
                }
            }
            catch { }

            return 60;
        }

        private void ApplyAnimationRate()
        {
            int hz = settings != null && settings.ControllerHighlightMatchMonitor
                ? GetMonitorRefreshRate(form)
                : (settings != null ? settings.ControllerHighlightHz : 60);

            hz = Math.Max(30, Math.Min(360, hz));

            // WinForms Timer uses whole milliseconds, so non-divisible rates are
            // approximated to the nearest millisecond (e.g. 170 Hz -> 6 ms).
            timer.Interval = Math.Max(1, (int)Math.Round(1000.0 / hz));
        }

        public void NotifyControllerNavigation()
        {
            Control next = ControllerNavigation.GetDeepActiveControl(form);

            // Grid navigation keeps focus on the FlowLayoutPanel, but the animated
            // controller cursor belongs around the selected COVER TILE, not around
            // the entire library container.
            clipToFormRect = null;

            FlowLayoutPanel grid = next as FlowLayoutPanel;
            if (grid != null)
            {
                LauncherForm launcher = form as LauncherForm;
                Control tile = launcher != null ? launcher.SelectedGridTile : null;

                if (tile == null)
                    next = null;
                else
                {
                    next = tile;

                    Rectangle gridScreen = grid.RectangleToScreen(grid.ClientRectangle);
                    Point gridTopLeft = form.PointToClient(new Point(gridScreen.Left, gridScreen.Top));
                    clipToFormRect = new Rectangle(
                        gridTopLeft.X,
                        gridTopLeft.Y,
                        gridScreen.Width,
                        gridScreen.Height);
                }
            }

            // ListBox keeps its native selected-row indicator.
            if (next == null || next is ListBox || next is FlowLayoutPanel)
            {
                controllerVisible = false;
                targetControl = null;
                clipToFormRect = null;
                adorner.Visible = false;
                hasRect = false;
                if (timer.Enabled) timer.Stop();
                return;
            }

            targetControl = next;
            targetRect = GetTargetRect(next);

            // First controller selection on a form appears immediately. Subsequent
            // selections animate from the CURRENT on-form position.
            if (!hasRect)
            {
                currentRect = targetRect;
                hasRect = true;
            }

            controllerVisible = true;
            adorner.Visible = true;
            adorner.BringToFront();
            if (!timer.Enabled) timer.Start();

            // Give the glow an immediate first step toward the new target in the same
            // input tick. This keeps the visual motion perceptually aligned with the
            // UI sound instead of letting audio lead a slowly easing cursor.
            if (hasRect)
            {
                const float immediateEase = 0.62F;
                currentRect = new RectangleF(
                    currentRect.X + (targetRect.X - currentRect.X) * immediateEase,
                    currentRect.Y + (targetRect.Y - currentRect.Y) * immediateEase,
                    currentRect.Width + (targetRect.Width - currentRect.Width) * immediateEase,
                    currentRect.Height + (targetRect.Height - currentRect.Height) * immediateEase);
            }

            // Do NOT BringToFront a child of FlowLayoutPanel. In WinForms,
            // changing a FlowLayoutPanel child's z-order also changes its layout
            // order. That was physically moving each selected game into the
            // first/top-left slot instead of moving the cursor to the game.
            if (!(next.Parent is FlowLayoutPanel))
                next.BringToFront();

            ApplyBounds();
        }

        private void Animate()
        {
            if (!controllerVisible || targetControl == null || targetControl.IsDisposed)
            {
                adorner.Visible = false;
                controllerVisible = false;
                if (timer.Enabled) timer.Stop();
                return;
            }

            // Keep the logical controller target while the launcher temporarily loses
            // focus, but do not paint the cursor over another application. The timer
            // remains alive in this specific case so the glow resumes naturally when
            // focus returns, matching RC29 behavior.
            if (!form.Visible || !form.ContainsFocus)
            {
                adorner.Visible = false;
                return;
            }

            targetRect = GetTargetRect(targetControl);

            // During an AutoScroll update, the selected tile can move by a full row
            // between timer ticks. If it is momentarily outside the grid viewport,
            // hide the glow rather than animating toward a location the user cannot see.
            if (clipToFormRect.HasValue)
            {
                RectangleF clip = clipToFormRect.Value;
                if (!clip.IntersectsWith(targetRect))
                {
                    adorner.Visible = false;
                    return;
                }
            }

            const float ease = 0.52F;
            currentRect = new RectangleF(
                currentRect.X + (targetRect.X - currentRect.X) * ease,
                currentRect.Y + (targetRect.Y - currentRect.Y) * ease,
                currentRect.Width + (targetRect.Width - currentRect.Width) * ease,
                currentRect.Height + (targetRect.Height - currentRect.Height) * ease);

            if (Math.Abs(currentRect.X - targetRect.X) < 0.4F &&
                Math.Abs(currentRect.Y - targetRect.Y) < 0.4F &&
                Math.Abs(currentRect.Width - targetRect.Width) < 0.4F &&
                Math.Abs(currentRect.Height - targetRect.Height) < 0.4F)
                currentRect = targetRect;

            ApplyBounds();
        }

        private void ApplyBounds()
        {
            Rectangle r = Rectangle.Round(currentRect);

            // Keep the adorner fully inside the client area. For grid navigation,
            // ALSO clamp to the visible Games viewport so the fancy cursor physically
            // scrolls with the library and can never drift outside it.
            int clipLeft = 0;
            int clipTop = 0;
            int clipRight = form.ClientSize.Width;
            int clipBottom = form.ClientSize.Height;

            if (clipToFormRect.HasValue)
            {
                Rectangle clip = clipToFormRect.Value;
                clipLeft = Math.Max(clipLeft, clip.Left);
                clipTop = Math.Max(clipTop, clip.Top);
                clipRight = Math.Min(clipRight, clip.Right);
                clipBottom = Math.Min(clipBottom, clip.Bottom);
            }

            int x = Math.Max(clipLeft, r.X);
            int y = Math.Max(clipTop, r.Y);
            int right = Math.Min(clipRight, r.Right);
            int bottom = Math.Min(clipBottom, r.Bottom);

            if (right <= x || bottom <= y)
            {
                adorner.Visible = false;
                return;
            }

            Rectangle nextBounds = new Rectangle(x, y, right - x, bottom - y);
            bool boundsChanged = adorner.Bounds != nextBounds;
            bool wasVisible = adorner.Visible;
            if (boundsChanged)
                adorner.Bounds = nextBounds;

            // IMPORTANT: make the adorner a hollow ring. A transparent WinForms child
            // control still repaints its parent's background, so a full rectangular
            // adorner can visually cover the selected control underneath it. Excluding
            // the center lets the real control render untouched while keeping only the
            // animated glow around its outside edge.
            //
            // RC30 performance change: this Region only depends on local WIDTH/HEIGHT.
            // RC29 rebuilt it every monitor-rate timer tick, even after the cursor had
            // completely settled. Rebuild it only when clipping/selection changes size.
            Size nextRegionSize = nextBounds.Size;
            bool regionChanged = adornerRegionSize != nextRegionSize;
            if (regionChanged)
            {
                Region oldRegion = adorner.Region;
                GraphicsPath outerRegionPath = CreateRoundedRegionPath(
                    new Rectangle(2, 2, Math.Max(1, nextRegionSize.Width - 4), Math.Max(1, nextRegionSize.Height - 4)), 13F);
                Region ring = new Region(outerRegionPath);
                outerRegionPath.Dispose();

                const int holeInset = 11;
                int holeWidth = Math.Max(0, nextRegionSize.Width - holeInset * 2);
                int holeHeight = Math.Max(0, nextRegionSize.Height - holeInset * 2);
                if (holeWidth > 0 && holeHeight > 0)
                {
                    GraphicsPath innerRegionPath = CreateRoundedRegionPath(
                        new Rectangle(holeInset, holeInset, holeWidth, holeHeight), 7F);
                    ring.Exclude(innerRegionPath);
                    innerRegionPath.Dispose();
                }

                adorner.Region = ring;
                adornerRegionSize = nextRegionSize;
                if (oldRegion != null)
                    oldRegion.Dispose();
            }

            adorner.Visible = true;
            if (!wasVisible)
                adorner.BringToFront();

            // Animated gradients still repaint at the user's chosen rate. Geometry and
            // Region allocation no longer have to accompany every color-animation frame.
            if (AccentVisuals.Animated(settings) || boundsChanged || regionChanged || !wasVisible)
                adorner.Invalidate();
        }

        private static GraphicsPath CreateRoundedRegionPath(Rectangle r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0)
                return path;

            float safeRadius = Math.Max(0F, Math.Min(radius, Math.Min(r.Width, r.Height) / 2F));
            if (safeRadius <= 0.5F)
            {
                path.AddRectangle(r);
                return path;
            }

            float d = safeRadius * 2F;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            try { timer.Stop(); timer.Dispose(); } catch { }
            try { adorner.Dispose(); } catch { }
        }
    }

    internal static class ControllerNavigation
    {
        public static string NormalizePollingMode(string value)
        {
            if (string.Equals(value, "120", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "120Hz", StringComparison.OrdinalIgnoreCase))
                return "120";
            if (string.Equals(value, "MatchMonitor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Match monitor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
                return "MatchMonitor";
            return "60";
        }

        public static int GetPollingTargetHz(Control control, DnlSettings settings)
        {
            string mode = NormalizePollingMode(settings != null ? settings.ControllerPollingMode : "60");
            if (mode == "120") return 120;
            if (mode == "MatchMonitor")
            {
                int detected = ControllerSelectionCursor.GetMonitorRefreshRate(control);
                return Math.Max(30, Math.Min(240, detected));
            }
            return 60;
        }

        private static int GetPollingIntervalMs(Control control, DnlSettings settings)
        {
            string mode = NormalizePollingMode(settings != null ? settings.ControllerPollingMode : "60");
            if (mode == "60")
                return 16; // Exact RC37 baseline.

            int hz = GetPollingTargetHz(control, settings);
            return Math.Max(1, (int)Math.Round(1000.0 / hz));
        }

        public static void Attach(Form form, SdlControllerManager manager, DnlSettings settings, TabControl tabs)
        {
            if (form == null || manager == null || settings == null) return;

            ControllerSelectionCursor selectionCursor = new ControllerSelectionCursor(form, settings);
            LauncherForm focusLauncher = form as LauncherForm;
            EventHandler focusSettledHandler = delegate { selectionCursor.NotifyControllerNavigation(); };
            if (focusLauncher != null) focusLauncher.ControllerFocusSettled += focusSettledHandler;
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            string activePollingMode = "";
            int activePollingIntervalMs = 0;
            int activePollingTargetHz = 0;
            Action applyPollingRate = delegate
            {
                string mode = NormalizePollingMode(settings.ControllerPollingMode);
                int intervalMs = GetPollingIntervalMs(form, settings);
                int targetHz = GetPollingTargetHz(form, settings);
                if (intervalMs == activePollingIntervalMs &&
                    targetHz == activePollingTargetHz &&
                    string.Equals(mode, activePollingMode, StringComparison.Ordinal))
                    return;

                timer.Interval = intervalMs;
                activePollingMode = mode;
                activePollingIntervalMs = intervalMs;
                activePollingTargetHz = targetHz;

                if (form is LauncherForm && form.Visible)
                {
                    DiagnosticsLog.Write("CONTROLLER",
                        "Launcher input polling configured: " + mode +
                        " (target " + targetHz + " Hz; WinForms timer " + intervalMs + " ms)." +
                        " Polling remains disabled when the launcher does not contain focus.");
                }
            };
            applyPollingRate();

            EventHandler pollingMonitorChanged = delegate
            {
                if (NormalizePollingMode(settings.ControllerPollingMode) == "MatchMonitor")
                    applyPollingRate();
            };
            form.LocationChanged += pollingMonitorChanged;

            DateTime nextReturnDiagnosticUtc = DateTime.MinValue;
            bool returnInputLogged = false;
            timer.Tick += delegate
            {
                string configuredMode = NormalizePollingMode(settings.ControllerPollingMode);
                if (!string.Equals(configuredMode, activePollingMode, StringComparison.Ordinal))
                    applyPollingRate();

                if (form.IsDisposed || !form.Visible || !settings.ControllerNavigation)
                    return;

                if (Program.InProcessReturnControllerWatch && !form.ContainsFocus)
                {
                    if (DateTime.UtcNow >= nextReturnDiagnosticUtc)
                    {
                        IntPtr foreground = Program.GetForegroundWindowForDiagnostics();
                        uint foregroundPid = Program.GetWindowProcessIdForDiagnostics(foreground);
                        DiagnosticsLog.Write("FOCUS", "Returned launcher is visible but does not contain focus; foreground PID " + foregroundPid.ToString() + ". Controller status: " + manager.StatusText);
                        nextReturnDiagnosticUtc = DateTime.UtcNow.AddSeconds(2);
                    }
                    return;
                }

                if (!form.ContainsFocus)
                    return;

                ControllerAction action = manager.Poll(settings.ControllerUseLeftStick);
                if (action != ControllerAction.None)
                {
                    if (Program.InProcessReturnControllerWatch && !returnInputLogged)
                    {
                        double elapsed = Program.InProcessReturnStartedUtc == DateTime.MinValue
                            ? 0.0
                            : (DateTime.UtcNow - Program.InProcessReturnStartedUtc).TotalSeconds;
                        DiagnosticsLog.Write("CONTROLLER", "First controller input received after in-process return (" + elapsed.ToString("0.0") + " seconds). Controller status: " + manager.StatusText);
                        returnInputLogged = true;
                        Program.InProcessReturnControllerWatch = false;
                    }
                    Control beforeControl = GetDeepActiveControl(form);
                    LauncherForm launcherBefore = form as LauncherForm;
                    bool libraryWasVisible = launcherBefore != null && launcherBefore.IsLibraryVisible;
                    bool sessionsWasVisible = launcherBefore != null && launcherBefore.IsSessionsVisible;

                    // RC35 measured combination: RC33 proved that briefly prioritizing
                    // Games/Sessions input materially improves timer delivery; RC34
                    // proved that removing only the hidden side-panel animation is not
                    // sufficient because the visible main backdrop can still monopolize
                    // WinForms' single UI thread.  Keep RC34's static side panels and
                    // restore RC33's short priority window for the remaining main backdrop.
                    if (action == ControllerAction.ToggleGames ||
                        action == ControllerAction.ToggleSessions)
                        UiAnimationBudget.PrioritizeInteractiveUi(850);
                    else if (libraryWasVisible || sessionsWasVisible)
                        UiAnimationBudget.PrioritizeInteractiveUi(180);

                    Handle(form, tabs, action);

                    // Start the visual response immediately, then request the already-
                    // preloaded async sound in the same input tick.
                    selectionCursor.NotifyControllerNavigation();

                    LauncherForm launcherAfter = form as LauncherForm;
                    bool libraryIsVisible = launcherAfter != null && launcherAfter.IsLibraryVisible;
                    UiSoundManager.PlayForControllerAction(
                        form, settings, action, beforeControl, libraryWasVisible, libraryIsVisible);
                }
            };

            form.Shown += delegate
            {
                // Re-evaluate after WinForms has placed the form on its actual screen.
                // This avoids a pre-show Screen.FromControl result from locking in the
                // primary monitor when the launcher opens on a different display.
                if (NormalizePollingMode(settings.ControllerPollingMode) == "MatchMonitor")
                    activePollingTargetHz = 0;
                applyPollingRate();
                timer.Start();
            };
            form.FormClosed += delegate
            {
                form.LocationChanged -= pollingMonitorChanged;
                timer.Stop();
                timer.Dispose();
                if (focusLauncher != null) focusLauncher.ControllerFocusSettled -= focusSettledHandler;
                selectionCursor.Dispose();
            };
        }

        public static Control GetDeepActiveControl(Form form)
        {
            Control current = form.ActiveControl;
            ContainerControl container = current as ContainerControl;
            while (container != null && container.ActiveControl != null)
            {
                current = container.ActiveControl;
                container = current as ContainerControl;
            }

            // NumericUpDown contains an internal text box. Treat the whole spinner as one
            // controller target so Up/Down can leave it instead of getting trapped inside.
            Control p = current;
            while (p != null && p != form)
            {
                NumericUpDown numericOwner = p as NumericUpDown;
                if (numericOwner != null)
                    return numericOwner;
                if (p.Parent is NumericUpDown)
                    return p.Parent;
                p = p.Parent;
            }

            return current;
        }

        public static bool IsNavigable(Control c)
        {
            return c != null && c.Visible && c.Enabled &&
                   (c is Button || c is RadioButton || c is CheckBox ||
                    c is TextBoxBase || c is NumericUpDown || c is ComboBox || c is ListBox ||
                    c is FlowLayoutPanel);
        }

        private static void Handle(Form form, TabControl tabs, ControllerAction action)
        {
            if (action == ControllerAction.ToggleGames)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                {
                    launcher.ToggleLibraryFromController();
                    return;
                }
            }
            if (action == ControllerAction.ToggleSessions)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                {
                    launcher.ToggleSessionsFromController();
                    return;
                }
            }
            if (action == ControllerAction.FocusPaste)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                {
                    launcher.FocusPasteFromController();
                    return;
                }
            }
            if (action == ControllerAction.FocusClear)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                {
                    launcher.FocusClearFromController();
                    return;
                }
            }
            if (action == ControllerAction.FocusPrimary)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                {
                    launcher.FocusPrimaryActionFromController();
                    return;
                }

                Control accept = form.AcceptButton as Control;
                if (accept != null && accept.Visible && accept.Enabled)
                    accept.Focus();
                else
                {
                    Control cancel = form.CancelButton as Control;
                    if (cancel != null && cancel.Visible && cancel.Enabled)
                        cancel.Focus();
                }
                return;
            }
            LauncherForm gridLauncher = form as LauncherForm;
            if (gridLauncher != null && gridLauncher.IsLibraryGridFocused)
            {
                if (action == ControllerAction.Up) { gridLauncher.MoveLibraryGridFromController(0, -1); return; }
                if (action == ControllerAction.Down) { gridLauncher.MoveLibraryGridFromController(0, 1); return; }
                if (action == ControllerAction.Left) { gridLauncher.MoveLibraryGridFromController(-1, 0); return; }
                if (action == ControllerAction.Right) { gridLauncher.MoveLibraryGridFromController(1, 0); return; }
                if (action == ControllerAction.PreviousTab) { gridLauncher.PageLibraryGridFromController(-1); return; }
                if (action == ControllerAction.NextTab) { gridLauncher.PageLibraryGridFromController(1); return; }
                if (action == ControllerAction.Accept) { gridLauncher.StageLibraryGridSelectionFromController(); return; }
                if (action == ControllerAction.Cancel) { gridLauncher.FocusLibraryHeaderFromController(); return; }
            }

            PublicSessionsForm publicSessions = form as PublicSessionsForm;
            if (publicSessions != null && publicSessions.HandleControllerNavigation(action))
                return;

            // The integrated Sessions browser is a child Form inside LauncherForm. Route
            // controller input to it before the launcher's generic ListBox/library logic;
            // otherwise A/B/Up/Down are mistaken for Games-list commands.
            LauncherForm embeddedSessionsLauncher = form as LauncherForm;
            if (embeddedSessionsLauncher != null && embeddedSessionsLauncher.IsSessionsVisible &&
                embeddedSessionsLauncher.HandleSessionsControllerNavigation(action))
                return;

            ListBox activeList = GetDeepActiveControl(form) as ListBox;
            if (activeList != null)
            {
                LauncherForm launcher = form as LauncherForm;

                if (action == ControllerAction.Up || action == ControllerAction.Down)
                {
                    if (activeList.Items.Count > 0)
                    {
                        int delta = action == ControllerAction.Up ? -1 : 1;
                        int current = activeList.SelectedIndex < 0 ? 0 : activeList.SelectedIndex;
                        int next = Math.Max(0, Math.Min(activeList.Items.Count - 1, current + delta));
                        activeList.SelectedIndex = next;
                        activeList.TopIndex = Math.Max(0, Math.Min(next, activeList.Items.Count - 1));
                    }
                    return;
                }

                if (action == ControllerAction.PreviousTab || action == ControllerAction.NextTab)
                {
                    if (activeList.Items.Count > 0)
                    {
                        int page = Math.Max(1, activeList.ClientSize.Height / Math.Max(1, activeList.ItemHeight) - 2);
                        int delta = action == ControllerAction.PreviousTab ? -page : page;
                        int current = activeList.SelectedIndex < 0 ? 0 : activeList.SelectedIndex;
                        int next = Math.Max(0, Math.Min(activeList.Items.Count - 1, current + delta));
                        activeList.SelectedIndex = next;
                        activeList.TopIndex = Math.Max(0, next);
                    }
                    return;
                }

                if (action == ControllerAction.Accept)
                {
                    if (launcher != null)
                    {
                        launcher.StageLibrarySelectionFromController();
                        return;
                    }

                    PublicSessionsForm sessionsForm = form as PublicSessionsForm;
                    if (sessionsForm != null)
                    {
                        sessionsForm.JoinSelectedFromController();
                        return;
                    }
                    return;
                }

                if (action == ControllerAction.Right)
                {
                    if (launcher != null)
                        launcher.FocusLibraryFooterFromController(true);
                    return;
                }

                if (action == ControllerAction.Left)
                {
                    if (launcher != null)
                        launcher.CollapseLibraryFromController();
                    return;
                }

                if (action == ControllerAction.Cancel)
                {
                    if (launcher != null)
                        launcher.FocusLibraryHeaderFromController();
                    else
                        form.SelectNextControl(activeList, true, true, true, false);
                    return;
                }
            }

            LauncherForm headerLauncher = form as LauncherForm;
            if (headerLauncher != null && headerLauncher.IsLibraryHeaderFocused)
            {
                ComboBox headerCombo = GetDeepActiveControl(form) as ComboBox;

                // While the columns dropdown is open, normal Up/Down changes the
                // selected value; Accept closes it. Cancel closes it without leaving
                // the toolbar.
                if (headerCombo != null && headerCombo.DroppedDown)
                {
                    if (action == ControllerAction.Up || action == ControllerAction.Down)
                    {
                        if (headerCombo.Items.Count > 0)
                        {
                            int delta = action == ControllerAction.Up ? -1 : 1;
                            int current = headerCombo.SelectedIndex < 0 ? 0 : headerCombo.SelectedIndex;
                            int next = Math.Max(0, Math.Min(headerCombo.Items.Count - 1, current + delta));
                            headerCombo.SelectedIndex = next;
                        }
                        return;
                    }

                    if (action == ControllerAction.Accept || action == ControllerAction.Cancel)
                    {
                        headerCombo.DroppedDown = false;
                        headerCombo.Focus();
                        return;
                    }

                    return;
                }

                if (action == ControllerAction.Left)
                {
                    headerLauncher.MoveLibraryHeaderFromController(-1);
                    return;
                }
                if (action == ControllerAction.Right)
                {
                    headerLauncher.MoveLibraryHeaderFromController(1);
                    return;
                }
                if (action == ControllerAction.Down)
                {
                    headerLauncher.FocusLibraryContentFromController();
                    return;
                }
                if (action == ControllerAction.Accept)
                {
                    headerLauncher.ActivateLibraryHeaderFromController();
                    return;
                }
                if (action == ControllerAction.Cancel)
                {
                    // Back from the toolbar returns to the games rather than closing
                    // the library unexpectedly. Hide remains an explicit toolbar action.
                    headerLauncher.FocusLibraryContentFromController();
                    return;
                }
            }

            LauncherForm libraryLauncher = form as LauncherForm;
            Button activeButton = GetDeepActiveControl(form) as Button;
            if (libraryLauncher != null && libraryLauncher.IsLibraryVisible && activeButton != null)
            {
                if (action == ControllerAction.Left)
                {
                    libraryLauncher.FocusLibraryFooterFromController(false);
                    return;
                }
                if (action == ControllerAction.Right)
                {
                    libraryLauncher.FocusLibraryFooterFromController(true);
                    return;
                }
                if (action == ControllerAction.Up)
                {
                    // Return directly to whichever library view is active.
                    libraryLauncher.FocusLibraryContentFromController();
                    return;
                }
            }

            LauncherForm mainLauncher = form as LauncherForm;
            if (mainLauncher != null && !mainLauncher.IsLibraryVisible &&
                mainLauncher.HandleMainControllerNavigation(action))
                return;

            ComboBox activeCombo = GetDeepActiveControl(form) as ComboBox;
            if (activeCombo != null && activeCombo.DroppedDown)
            {
                if (action == ControllerAction.Up || action == ControllerAction.Down)
                {
                    if (activeCombo.Items.Count > 0)
                    {
                        int delta = action == ControllerAction.Up ? -1 : 1;
                        int current = activeCombo.SelectedIndex < 0 ? 0 : activeCombo.SelectedIndex;
                        int next = Math.Max(0, Math.Min(activeCombo.Items.Count - 1, current + delta));
                        activeCombo.SelectedIndex = next;
                    }
                    return;
                }

                if (action == ControllerAction.Accept || action == ControllerAction.Cancel)
                {
                    activeCombo.DroppedDown = false;
                    activeCombo.Focus();
                    return;
                }

                if (action == ControllerAction.Left || action == ControllerAction.Right ||
                    action == ControllerAction.PreviousTab || action == ControllerAction.NextTab)
                    return;
            }

            switch (action)
            {
                case ControllerAction.Up:
                    MoveFocus(form, false);
                    break;
                case ControllerAction.Down:
                    MoveFocus(form, true);
                    break;
                case ControllerAction.Left:
                    if (!AdjustFocused(form, -1)) MoveFocus(form, false);
                    break;
                case ControllerAction.Right:
                    if (!AdjustFocused(form, 1)) MoveFocus(form, true);
                    break;
                case ControllerAction.Accept:
                    ActivateFocused(form);
                    break;
                case ControllerAction.Cancel:
                    // Back is navigation, not an immediate destructive/closing action.
                    // Move focus to the form's Cancel button; require Accept to activate it.
                    if (form.CancelButton is Control)
                    {
                        Control cancelControl = form.CancelButton as Control;
                        if (cancelControl != null && cancelControl.Visible && cancelControl.Enabled)
                            cancelControl.Focus();
                    }
                    break;
                case ControllerAction.PreviousTab:
                    ChangeTab(tabs, -1);
                    break;
                case ControllerAction.NextTab:
                    ChangeTab(tabs, 1);
                    break;
            }
        }

        private static TabPage FindParentTabPage(Control control)
        {
            Control current = control;
            while (current != null)
            {
                TabPage page = current as TabPage;
                if (page != null) return page;
                current = current.Parent;
            }
            return null;
        }

        public static void FocusFirstNavigable(Control root)
        {
            if (root == null) return;

            List<Control> candidates = new List<Control>();
            CollectNavigableControls(root, candidates);
            candidates.Sort(delegate(Control a, Control b)
            {
                int ay = a.PointToScreen(Point.Empty).Y;
                int by = b.PointToScreen(Point.Empty).Y;
                if (ay != by) return ay.CompareTo(by);

                int ax = a.PointToScreen(Point.Empty).X;
                int bx = b.PointToScreen(Point.Empty).X;
                return ax.CompareTo(bx);
            });

            foreach (Control candidate in candidates)
            {
                TextBoxBase text = candidate as TextBoxBase;
                if (text != null && text.ReadOnly)
                    continue;

                if (candidate.CanFocus)
                {
                    candidate.Focus();
                    return;
                }
            }

            if (candidates.Count > 0 && candidates[0].CanFocus)
                candidates[0].Focus();
        }

        private static void CollectNavigableControls(Control root, List<Control> results)
        {
            foreach (Control child in root.Controls)
            {
                if (!child.Visible || !child.Enabled)
                    continue;

                if (IsNavigable(child))
                    results.Add(child);

                if (child.HasChildren)
                    CollectNavigableControls(child, results);
            }
        }

        private static void MoveFocus(Form form, bool forward)
        {
            Control current = GetDeepActiveControl(form);
            Control start = current;
            TabPage startingPage = FindParentTabPage(current);
            bool keepReverseNavigationInsideOptionsPage =
                !forward && form is OptionsForm && startingPage != null;

            for (int i = 0; i < 60; i++)
            {
                Control previous = current;
                form.SelectNextControl(current, forward, true, true, false);
                Control next = GetDeepActiveControl(form);

                if (next == null || next == start || next == previous)
                    return;

                if (keepReverseNavigationInsideOptionsPage &&
                    FindParentTabPage(next) != startingPage)
                {
                    if (previous != null && previous.CanFocus)
                        previous.Focus();
                    return;
                }

                if (IsNavigable(next)) return;
                current = next;
            }
        }

        private static bool AdjustFocused(Form form, int delta)
        {
            Control current = GetDeepActiveControl(form);

            RadioButton radio = current as RadioButton;
            if (radio != null && radio.Parent != null)
            {
                List<RadioButton> radios = new List<RadioButton>();
                foreach (Control c in radio.Parent.Controls)
                {
                    RadioButton r = c as RadioButton;
                    if (r != null && r.Visible && r.Enabled) radios.Add(r);
                }
                radios.Sort(delegate(RadioButton a, RadioButton b) { return a.TabIndex.CompareTo(b.TabIndex); });
                int idx = radios.IndexOf(radio);
                if (idx >= 0 && radios.Count > 1)
                {
                    int next = (idx + delta + radios.Count) % radios.Count;
                    radios[next].Checked = true;
                    radios[next].Focus();
                    return true;
                }
            }

            NumericUpDown num = current as NumericUpDown;
            if (num != null)
            {
                decimal value = num.Value + num.Increment * delta;
                if (value < num.Minimum) value = num.Minimum;
                if (value > num.Maximum) value = num.Maximum;
                num.Value = value;
                return true;
            }

            ComboBox combo = current as ComboBox;
            if (combo != null && combo.Items.Count > 0)
            {
                int idx = combo.SelectedIndex + delta;
                if (idx < 0) idx = 0;
                if (idx >= combo.Items.Count) idx = combo.Items.Count - 1;
                combo.SelectedIndex = idx;
                return true;
            }

            return false;
        }

        private static void ActivateFocused(Form form)
        {
            Control current = GetDeepActiveControl(form);

            Button button = current as Button;
            if (button != null) { button.PerformClick(); return; }

            RadioButton radio = current as RadioButton;
            if (radio != null) { radio.Checked = true; return; }

            CheckBox check = current as CheckBox;
            if (check != null) { check.Checked = !check.Checked; return; }

            ComboBox combo = current as ComboBox;
            if (combo != null) { combo.DroppedDown = !combo.DroppedDown; return; }

            TextBoxBase text = current as TextBoxBase;
            if (text != null) { text.Focus(); return; }

            NumericUpDown num = current as NumericUpDown;
            if (num != null) { num.Focus(); return; }

            ListBox list = current as ListBox;
            if (list != null)
            {
                LauncherForm launcher = form as LauncherForm;
                if (launcher != null)
                    launcher.StageLibrarySelectionFromController();
                return;
            }

            if (form.AcceptButton != null) form.AcceptButton.PerformClick();
        }

        private static T FindFirstControl<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                T match = child as T;
                if (match != null)
                    return match;

                T nested = FindFirstControl<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static void ChangeTab(TabControl tabs, int delta)
        {
            if (tabs == null || tabs.TabPages.Count == 0) return;
            int next = tabs.SelectedIndex + delta;
            if (next < 0) next = tabs.TabPages.Count - 1;
            if (next >= tabs.TabPages.Count) next = 0;
            tabs.SelectedIndex = next;
        }
    }


    internal sealed class JoinFailureRecoveryForm : Form
    {
        private readonly CheckBox alwaysReturnBox = new CheckBox();
        private UpdaterControllerPromptForm controllerHint;
        public bool AlwaysReturn { get { return alwaysReturnBox.Checked; } }

        public JoinFailureRecoveryForm(DnlSettings settings)
        {
            Text = "Dolphin NetPlay Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 178);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            Font = new Font("Segoe UI", 9F);

            Label title = new AdventureLabel();
            title.Text = "Return to Dolphin NetPlay Launcher?";
            title.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(18, 18);
            Controls.Add(title);

            Label body = new AdventureLabel();
            body.Text = "The NetPlay connection did not open a lobby. You can return to the launcher\nand correct the room code/IP, or stay in Dolphin NetPlay Setup.";
            body.AutoSize = true;
            body.Location = new Point(18, 51);
            Controls.Add(body);

            alwaysReturnBox.Text = "Always return automatically after future failed Joins";
            alwaysReturnBox.AutoSize = true;
            alwaysReturnBox.Location = new Point(18, 96);
            Controls.Add(alwaysReturnBox);

            Button yes = new AdventureButton();
            yes.Text = "Yes";
            yes.DialogResult = DialogResult.Yes;
            yes.Size = new Size(82, 30);
            yes.Location = new Point(282, 132);
            Controls.Add(yes);

            Button no = new AdventureButton();
            no.Text = "No";
            no.DialogResult = DialogResult.No;
            no.Size = new Size(82, 30);
            no.Location = new Point(372, 132);
            Controls.Add(no);

            AcceptButton = yes;
            CancelButton = no;
            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);
            ControllerNavigation.Attach(this, Program.ControllerManagerForChildForms, settings, null);

            // This dialog is launched while Dolphin owns the foreground. ShowDialog/TopMost
            // alone can leave Dolphin as the active window, which prevents ControllerNavigation
            // from accepting input until the user clicks this form. Explicitly activate our own
            // recovery choice when it appears, then focus Yes as the safe/default action.
            Shown += delegate
            {
                Activate();
                BringToFront();
                yes.Focus();

                // Re-assert activation after the Shown event has fully unwound. This avoids a
                // final Qt/Dolphin foreground transition winning the same-message-loop race.
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && Visible)
                    {
                        Activate();
                        BringToFront();
                        yes.Focus();
                    }
                });
            };

            bool showHint = settings != null && settings.ControllerNavigation && settings.ShowControllerPrompts;
            if (showHint)
            {
                controllerHint = new UpdaterControllerPromptForm(
                    true,
                    settings.ControllerPromptStyle,
                    false,
                    false,
                    false,
                    true);

                Shown += delegate
                {
                    controllerHint.ShowForWindow(Handle);
                };
                LocationChanged += delegate
                {
                    if (controllerHint != null && Visible)
                        controllerHint.ShowForWindow(Handle);
                };
            }

            FormClosed += delegate
            {
                if (controllerHint != null)
                {
                    controllerHint.HidePrompt();
                    controllerHint.Dispose();
                    controllerHint = null;
                }
            };
        }
    }

    internal sealed class WarningForm : Form
    {
        private readonly bool animatedBorder;
        private readonly System.Windows.Forms.Timer borderTimer = new System.Windows.Forms.Timer();

        public WarningForm(string line1, string line2, DnlSettings settings = null, bool showAnimatedBorder = false)
        {
            animatedBorder = showAnimatedBorder;
            Text = "";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(405, 92);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ControlBox = false;
            TopMost = true;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);

            Label a = new AdventureLabel();
            a.Text = line1;
            a.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            a.AutoSize = true;
            a.Location = new Point(20, 16);
            Controls.Add(a);

            Label b = new AdventureLabel();
            b.Text = line2;
            b.Font = new Font("Segoe UI", 10F);
            b.AutoSize = true;
            b.Location = new Point(20, 51);
            Controls.Add(b);
            AppTheme.Apply(this, settings);
            AppFonts.Apply(this, settings);

            if (animatedBorder)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                borderTimer.Interval = 50;
                borderTimer.Tick += delegate { if (Visible) Invalidate(); };
                Shown += delegate { borderTimer.Start(); };
                FormClosed += delegate { borderTimer.Stop(); };
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!animatedBorder)
                return;

            Rectangle rect = ClientRectangle;
            rect.Inflate(-2, -2);
            using (LinearGradientBrush brush = AccentVisuals.CreateGradient(rect, 255))
            using (Pen pen = new Pen(brush, 4F))
            {
                pen.Alignment = PenAlignment.Inset;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                borderTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
