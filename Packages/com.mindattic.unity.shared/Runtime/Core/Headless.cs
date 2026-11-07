using System;
using System.Collections.Generic;
using System.Threading;

namespace Unity.Shared.Core
{
    /// <summary>
    /// The shared, off-by-default headless mode for scripted and automated testing: the real game, no window,
    /// driven and queried by a script. On when the command line has <c>--headless</c> (or <c>-headless</c>, any
    /// case) or the environment sets <c>MINDATTIC_HEADLESS=1</c>; with neither, nothing changes. In Unity the
    /// package's <c>HeadlessBootstrap</c> then mutes the session (<see cref="Profiles.SessionMuted"/>, settings.json
    /// untouched), sets <c>Application.runInBackground</c>, turns vSync off and logs one
    /// <see cref="ReadyMarker"/> line. The window itself is the launcher's job (<c>-batchmode -nographics</c>,
    /// see <c>tools/Start-Headless.ps1</c>). Games add their own opt-ins (skip the main menu, ...) with
    /// <see cref="WhenActive"/> or by reading <see cref="IsActive"/>. The same contract as Unreal.Shared's.
    /// </summary>
    public static class Headless
    {
        /// <summary>The command-line switch, <c>--headless</c>; <c>-headless</c> is accepted too, ignoring case.</summary>
        public const string CommandLineSwitch = "--headless";

        /// <summary>The single-dash spelling of <see cref="CommandLineSwitch"/>, also accepted.</summary>
        public const string ShortCommandLineSwitch = "-headless";

        /// <summary>An environment variable that, set to <c>1</c> (or <c>true</c>/<c>yes</c>/<c>on</c>), turns headless mode on.</summary>
        public const string EnvironmentVariable = "MINDATTIC_HEADLESS";

        /// <summary>The start of the one log line written once headless mode is applied; scripts wait for it.</summary>
        public const string ReadyMarker = "MINDATTIC_HEADLESS_READY";

        static readonly object gate = new object();
        static bool? active;
        static Action activated;

        /// <summary>
        /// Whether this session is headless. Resolved once, on first read, from <see cref="Requested()"/>; setting it
        /// overrides that (tests, tools). Becoming true raises <see cref="Activated"/> once.
        /// </summary>
        public static bool IsActive
        {
            get
            {
                Action raise = null;
                bool result;
                lock (gate)
                {
                    if (active == null)
                    {
                        active = Requested();
                        if (active == true) raise = activated;
                    }
                    result = active.Value;
                }
                raise?.Invoke();
                return result;
            }
            set
            {
                Action raise = null;
                lock (gate)
                {
                    bool was = active == true;
                    active = value;
                    if (value && !was) raise = activated;
                }
                raise?.Invoke();
            }
        }

        /// <summary>
        /// Raised once when headless mode becomes active (when <see cref="IsActive"/> first resolves true, or is set
        /// true). It has usually fired before any game code runs, so prefer <see cref="WhenActive"/>, which also
        /// runs the callback straight away if the session is already headless.
        /// </summary>
        public static event Action Activated
        {
            add { lock (gate) activated += value; }
            remove { lock (gate) activated -= value; }
        }

        /// <summary>
        /// Runs <paramref name="callback"/> now if the session is headless, otherwise once it becomes headless (set
        /// from code). The way a game opts in: <c>Headless.WhenActive(() =&gt; skipMainMenu = true);</c>
        /// </summary>
        public static void WhenActive(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            int done = 0;
            Action once = null;
            once = () =>
            {
                if (Interlocked.Exchange(ref done, 1) != 0) return;
                Activated -= once;
                callback();
            };
            // Subscribe first, then ask: whichever comes first (the event, or IsActive being already true) runs it, once.
            Activated += once;
            if (IsActive) once();
        }

        /// <summary>The ready line for <paramref name="productName"/>: <c>MINDATTIC_HEADLESS_READY &lt;productName&gt;</c>.</summary>
        public static string ReadyLine(string productName) =>
            string.IsNullOrWhiteSpace(productName) ? ReadyMarker : ReadyMarker + " " + productName.Trim();

        /// <summary>Whether this process's environment variable or command line asks for headless mode.</summary>
        public static bool Requested()
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (NotSupportedException)
            {
                args = Array.Empty<string>();
            }
            return Requested(args, Environment.GetEnvironmentVariable(EnvironmentVariable));
        }

        /// <summary>
        /// Whether <paramref name="commandLineArgs"/> or <paramref name="environmentValue"/> (the value of
        /// <see cref="EnvironmentVariable"/>) asks for headless mode: either switch spelling in any case, or
        /// <c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>. Null or empty inputs ask for nothing.
        /// </summary>
        public static bool Requested(IEnumerable<string> commandLineArgs, string environmentValue)
        {
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                switch (environmentValue.Trim().ToLowerInvariant())
                {
                    case "1": case "true": case "yes": case "on": return true;
                }
            }
            if (commandLineArgs == null) return false;
            foreach (string a in commandLineArgs)
            {
                if (string.Equals(a, CommandLineSwitch, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, ShortCommandLineSwitch, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Forgets the resolved value, any override and every <see cref="Activated"/> handler (tests).</summary>
        public static void Reset()
        {
            lock (gate)
            {
                active = null;
                activated = null;
            }
        }
    }
}
