using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PclHarmony
{
    /// <summary>
    /// Low-cost, deferred logger. Nothing here touches the filesystem until the
    /// first line is written, which happens only after the ADM bootstrap has
    /// handed over (see Entry.InitializeNewDomain: it must stay I/O free).
    /// Lines are buffered in memory; the buffer is flushed explicitly at phase
    /// boundaries, on a 2 s timer, and on process exit.
    /// </summary>
    internal static class LoaderLog
    {
        private const string FileName = "PclHarmony.log";
        private const int FlushThreshold = 64;
        private const int FlushIntervalMs = 2000;

        private static readonly object Gate = new object();
        private static readonly object WriteGate = new object();
        private static readonly List<string> Buffer = new List<string>();
        private static Timer _flushTimer;

        private static string _baseDirectory;
        private static string _logPath;
        private static bool _created;
        private static int _initialised;

        /// <summary>Called from InitializeNewDomain: plain string assignment only.</summary>
        internal static void SetBaseDirectory(string directory)
        {
            _baseDirectory = directory;
        }

        internal static string BaseDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(_baseDirectory)) return _baseDirectory;
                string dir = null;
                try
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    if (entry != null && !string.IsNullOrEmpty(entry.Location))
                        dir = Path.GetDirectoryName(entry.Location);
                }
                catch { }
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                }
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = Path.GetTempPath(); } catch { }
                }
                _baseDirectory = dir;
                return dir;
            }
        }

        internal static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                string dir = BaseDirectory;
                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = Path.GetTempPath(); } catch { dir = "."; }
                }
                _logPath = Path.Combine(dir, FileName);
                return _logPath;
            }
        }

        internal static void Info(string message)
        {
            if (message == null) return;
            try
            {
                EnsureInitialised();

                string line = DateTime.Now.ToString("HH:mm:ss.fff")
                              + " [T" + Thread.CurrentThread.ManagedThreadId.ToString("00") + "] "
                              + message;

                bool flushNow;
                lock (Gate)
                {
                    Buffer.Add(line);
                    flushNow = Buffer.Count >= FlushThreshold;
                }
                if (flushNow) Flush();
            }
            catch
            {
                // Logging must never throw into the host.
            }
        }

        internal static void Info(string format, params object[] args)
        {
            if (format == null) return;
            try { Info(args == null || args.Length == 0 ? format : string.Format(format, args)); }
            catch { Info(format); }
        }

        internal static void Flush()
        {
            string text;
            lock (Gate)
            {
                if (Buffer.Count == 0) return;
                StringBuilder sb = new StringBuilder(Buffer.Count * 48);
                for (int i = 0; i < Buffer.Count; i++) sb.Append(Buffer[i]).Append(Environment.NewLine);
                text = sb.ToString();
                Buffer.Clear();
            }

            // Serialise the whole file operation: two threads racing here could
            // otherwise both take the "first write" path and truncate each other.
            lock (WriteGate)
            {
                try
                {
                    string path = LogPath;
                    UTF8Encoding utf8 = new UTF8Encoding(false);
                    if (!_created)
                    {
                        File.WriteAllText(path, text, utf8);
                        _created = true;
                    }
                    else
                    {
                        File.AppendAllText(path, text, utf8);
                    }
                }
                catch
                {
                    // Unwritable folder: drop the batch rather than crash/loop.
                    _created = true;
                }
            }
        }

        private static void EnsureInitialised()
        {
            if (Interlocked.Exchange(ref _initialised, 1) != 0) return;
            // Prove the hooks are actually registered even if they never fire.
            // Written here lazily on purpose (no I/O from InitializeNewDomain).
            Info("logger hooks registering (threshold=" + FlushThreshold
                 + ", interval=" + FlushIntervalMs + "ms)");
            try
            {
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    Info("loader shutting down (process exit, flush)");
                    Flush();
                };
            }
            catch { }
            try
            {
                AppDomain.CurrentDomain.DomainUnload += delegate
                {
                    Info("loader shutting down (domain unload, flush)");
                    Flush();
                };
            }
            catch { }
            try
            {
                // Held in a static field: GC.KeepAlive() inside this method would not
                // stop the timer from being collected once the method returns.
                _flushTimer = new Timer(delegate { Flush(); }, null, FlushIntervalMs, FlushIntervalMs);
            }
            catch { }
        }
    }
}
