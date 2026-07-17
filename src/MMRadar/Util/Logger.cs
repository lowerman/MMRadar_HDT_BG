using System;

namespace MMRadar.Util
{
    /// <summary>
    /// Logging indirection: inside HDT the plugin routes these to HDT's log file,
    /// in the standalone harness they go to the debug console.
    /// </summary>
    public static class Logger
    {
        public static Action<string> InfoSink = m => System.Diagnostics.Debug.WriteLine("[MMRadar] " + m);
        public static Action<string> DebugSink = m => System.Diagnostics.Debug.WriteLine("[MMRadar] " + m);
        public static Action<string> ErrorSink = m => System.Diagnostics.Debug.WriteLine("[MMRadar][ERROR] " + m);

        public static void Info(string message) => Safe(() => InfoSink(message));
        public static void Debug(string message) => Safe(() => DebugSink(message));

        public static void Error(string message, Exception ex = null) =>
            Safe(() => ErrorSink(ex == null ? message : message + " — " + ex));

        private static void Safe(Action action)
        {
            try { action(); }
            catch { /* logging must never throw */ }
        }
    }
}
