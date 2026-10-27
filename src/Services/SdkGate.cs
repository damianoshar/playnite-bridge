namespace PlayniteBridge.Services
{
    using System;

    /// <summary>
    /// Serializes Playnite SDK database reads from HTTP worker threads.
    /// Playnite's SDK is not fully thread-safe; concurrent enumeration of the
    /// game collection (e.g. during a library import) from unbounded request
    /// threads is a crash vector. All list-style reads go through here.
    /// Single Get-by-id lookups are cheap and stay lock-free.
    /// </summary>
    internal class SdkGate
    {
        private readonly object _lock = new object();

        public T Read<T>(Func<T> read)
        {
            lock (_lock) { return read(); }
        }
    }
}
