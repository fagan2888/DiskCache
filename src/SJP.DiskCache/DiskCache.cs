﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SJP.DiskCache
{
    /// <summary>
    /// A disk-based caching store.
    /// </summary>
    public class DiskCache : IDiskCache
    {
        /// <summary>
        /// Creates a disk-based caching store.
        /// </summary>
        /// <param name="directory">A directory.</param>
        /// <param name="cachePolicy">A cache policy to apply to values in the cache.</param>
        /// <param name="storageCapacity">The maximum amount of space to store in the cache.</param>
        /// <param name="pollingInterval">The maximum time that will elapse before a cache policy will be applied. Defaults to 1 minute.</param>
        /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <c>null</c> or <paramref name="cachePolicy"/> is <c>null</c>.</exception>
        /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> does not exist.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="storageCapacity"/> is less than <c>1</c>. Can also be thrown when <paramref name="pollingInterval"/> represents a negative timespan or a zero-length timespan.</exception>
        public DiskCache(DirectoryInfo directory, ICachePolicy cachePolicy, ulong storageCapacity, TimeSpan? pollingInterval = null)
            : this(directory?.FullName ?? throw new ArgumentNullException(nameof(directory)), cachePolicy, storageCapacity, pollingInterval)
        {
        }

        /// <summary>
        /// Creates a disk-based caching store.
        /// </summary>
        /// <param name="directoryPath">A path to a directory.</param>
        /// <param name="cachePolicy">A cache policy to apply to values in the cache.</param>
        /// <param name="storageCapacity">The maximum amount of space to store in the cache.</param>
        /// <param name="pollingInterval">The maximum time that will elapse before a cache policy will be applied. Defaults to 1 minute.</param>
        /// <exception cref="ArgumentNullException"><paramref name="directoryPath"/> is <c>null</c>, empty or whitespace. Also thrown when <paramref name="cachePolicy"/> is <c>null</c>.</exception>
        /// <exception cref="DirectoryNotFoundException">The directory at <paramref name="directoryPath"/> does not exist.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="storageCapacity"/> is less than <c>1</c>. Can also be thrown when <paramref name="pollingInterval"/> represents a negative timespan or a zero-length timespan.</exception>
        public DiskCache(string directoryPath, ICachePolicy cachePolicy, ulong storageCapacity, TimeSpan? pollingInterval = null)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentNullException(nameof(directoryPath));
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"The cache directory does not exist. The directory at '{ directoryPath }' must be present.");
            if (storageCapacity == 0)
                throw new ArgumentOutOfRangeException("The storage capacity must be at least 1 byte. Given: " + storageCapacity.ToString(CultureInfo.InvariantCulture), nameof(storageCapacity));

            pollingInterval = pollingInterval ?? TimeSpan.FromMinutes(1);
            var interval = pollingInterval.Value;
            if (interval <= _zero)
                throw new ArgumentOutOfRangeException("The polling time interval must be a non-negative and non-zero timespan. Given: " + interval.ToString(), nameof(pollingInterval));

            CachePath = new DirectoryInfo(directoryPath);
            Policy = cachePolicy ?? throw new ArgumentNullException(nameof(cachePolicy));
            MaximumStorageCapacity = storageCapacity;
            PollingInterval = interval;

            Clear();

            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(PollingInterval).ConfigureAwait(false);
                    ApplyCachePolicy();
                }
            }, _cts.Token);
        }

        /// <summary>
        /// The maximum size that the cache can contain. This can be temporarily exceeded when a file is added that is too large, to a maximum of twice the storage capacity.
        /// </summary>
        public ulong MaximumStorageCapacity { get; }

        /// <summary>
        /// The maximum timespan that will occur before the cache policy will be re-evaluated on cache entries.
        /// </summary>
        public TimeSpan PollingInterval { get; }

        /// <summary>
        /// The cache eviction policy that evaluates which entries should be removed from the cache.
        /// </summary>
        public ICachePolicy Policy { get; }

        /// <summary>
        /// The directory that is storing the cache.
        /// </summary>
        protected DirectoryInfo CachePath { get; }

        /// <summary>
        /// Occurs when an entry has been added to the cache.
        /// </summary>
        public event EventHandler<ICacheEntry> EntryAdded;

        /// <summary>
        /// Occurs when an entry has been updated in the cache.
        /// </summary>
        public event EventHandler<ICacheEntry> EntryUpdated;

        /// <summary>
        /// Occurs when an entry has been removed or evicted from the cache.
        /// </summary>
        public event EventHandler<ICacheEntry> EntryRemoved;

        /// <summary>
        /// Empties the cache of all values that it is currently tracking.
        /// </summary>
        public void Clear()
        {
            foreach (var entry in _entryLookup)
            {
                File.Delete(_fileLookup[entry.Key]);
                _entryLookup.TryRemove(entry.Key, out var lookupEntry);
                EntryRemoved?.Invoke(this, lookupEntry);
            }

            foreach (var dir in CachePath.EnumerateDirectories())
                dir.Delete(true);

            foreach (var file in CachePath.EnumerateFiles())
                file.Delete();

            _entryLookup.Clear();
            _fileLookup.Clear();
        }

        /// <summary>
        /// Determines whether the <see cref="IDiskCache" /> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns><c>true</c> if the cache contains the key; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace.</exception>
        public bool ContainsKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            return _entryLookup.ContainsKey(key);
        }

        /// <summary>
        /// Asynchronously determines whether the <see cref="IDiskCache" /> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns><c>true</c> if the cache contains the key; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace.</exception>
        public Task<bool> ContainsKeyAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            return Task.Run(() => ContainsKey(key));
        }

        /// <summary>
        /// Gets the value associated with a key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>A stream of data from the cache.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace.</exception>
        public Stream GetValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            if (!_fileLookup.ContainsKey(key))
                throw new KeyNotFoundException($"Could not find a value for the key '{ key }'");
            var path = _fileLookup[key];
            if (!File.Exists(path))
                throw new FileNotFoundException($"Expected to find a path at the path '{ path }', but it does not exist.", path);

            var cacheEntry = _entryLookup[key];
            cacheEntry.Refresh();
            _entryLookup[key] = cacheEntry;

            return File.OpenRead(path);
        }

        /// <summary>
        /// Asynchronously gets the value associated with a key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>A stream of data from the cache.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace.</exception>
        public Task<Stream> GetValueAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            return Task.Run(() => GetValue(key));
        }

        /// <summary>
        /// Stores a value associated with a key.
        /// </summary>
        /// <param name="key">The key used to locate the value in the cache.</param>
        /// <param name="value">A stream of data to store in the cache.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace. Can also be thrown when <paramref name="value"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> is not readable.</exception>
        public void SetValue(string key, Stream value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!value.CanRead)
                throw new ArgumentException("The given stream is not readable.", nameof(value));

            ulong totalBytesRead = 0;
            const long bufferSize = 4096;

            var tmpFileName = Path.Combine(CachePath.FullName, Guid.NewGuid().ToString());
            string hash = null;

            using (var shaHasher = new SHA256Managed())
            {
                using (var writer = File.OpenWrite(tmpFileName))
                {
                    byte[] oldBuffer;
                    int oldBytesRead;

                    var buffer = new byte[bufferSize];
                    var bytesRead = value.Read(buffer, 0, buffer.Length);
                    totalBytesRead += Convert.ToUInt32(bytesRead);

                    do
                    {
                        oldBytesRead = bytesRead;
                        oldBuffer = buffer;

                        buffer = new byte[bufferSize];
                        bytesRead = value.Read(buffer, 0, buffer.Length);
                        totalBytesRead += Convert.ToUInt32(bytesRead);

                        if (bytesRead == 0)
                        {
                            shaHasher.TransformFinalBlock(oldBuffer, 0, oldBytesRead);
                            writer.Write(oldBuffer, 0, oldBytesRead);
                        }
                        else
                        {
                            shaHasher.TransformBlock(oldBuffer, 0, oldBytesRead, oldBuffer, 0);
                            writer.Write(oldBuffer, 0, oldBytesRead);
                        }
                    }
                    while (bytesRead != 0 && totalBytesRead <= MaximumStorageCapacity);
                }

                if (totalBytesRead > MaximumStorageCapacity)
                {
                    File.Delete(tmpFileName); // remove the file, we can't keep it anyway
                    throw new ArgumentException("The given stream received data that was larger than the allotted storage capacity of " + MaximumStorageCapacity.ToString(CultureInfo.InvariantCulture), nameof(value));
                }

                var shaHashBytes = shaHasher.Hash;
                hash = BitConverter.ToString(shaHashBytes).Replace("-", string.Empty);
            }

            var isNew = !ContainsKey(key);

            var cachePath = GetPath(hash);
            var cachePathDir = Path.GetDirectoryName(cachePath);
            if (!Directory.Exists(cachePathDir))
                Directory.CreateDirectory(cachePathDir);
            File.Move(tmpFileName, cachePath);
            var cacheFileInfo = new FileInfo(cachePath);

            _fileLookup[key] = cachePath;
            var cacheEntry = new CacheEntry(key, Convert.ToUInt64(cacheFileInfo.Length));
            _entryLookup[key] = cacheEntry;

            if (isNew)
                EntryAdded?.Invoke(this, cacheEntry);
            else
                EntryUpdated?.Invoke(this, cacheEntry);

            ApplyCachePolicy();
        }

        /// <summary>
        /// Stores a value associated with a key.
        /// </summary>
        /// <param name="key">The key used to locate the value in the cache.</param>
        /// <param name="value">A stream of data to store in the cache.</param>
        /// <returns><c>true</c> if the data was able to be stored without error; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>, empty or whitespace. Can also be thrown when <paramref name="value"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> is not readable.</exception>
        public bool TrySetValue(string key, Stream value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!value.CanRead)
                throw new ArgumentException("The given stream is not readable.", nameof(value));

            ulong totalBytesRead = 0;
            const long bufferSize = 4096;

            var tmpFileName = Path.Combine(CachePath.FullName, Guid.NewGuid().ToString());
            string hash = null;

            using (var shaHasher = new SHA256Managed())
            {
                using (var writer = File.OpenWrite(tmpFileName))
                {
                    byte[] oldBuffer;
                    int oldBytesRead;

                    var buffer = new byte[bufferSize];
                    var bytesRead = value.Read(buffer, 0, buffer.Length);
                    totalBytesRead += Convert.ToUInt32(bytesRead);

                    do
                    {
                        oldBytesRead = bytesRead;
                        oldBuffer = buffer;

                        buffer = new byte[bufferSize];
                        bytesRead = value.Read(buffer, 0, buffer.Length);
                        totalBytesRead += Convert.ToUInt32(bytesRead);

                        if (bytesRead == 0)
                        {
                            shaHasher.TransformFinalBlock(oldBuffer, 0, oldBytesRead);
                            writer.Write(oldBuffer, 0, oldBytesRead);
                        }
                        else
                        {
                            shaHasher.TransformBlock(oldBuffer, 0, oldBytesRead, oldBuffer, 0);
                            writer.Write(oldBuffer, 0, oldBytesRead);
                        }
                    }
                    while (bytesRead != 0 && totalBytesRead <= MaximumStorageCapacity);
                }

                if (totalBytesRead > MaximumStorageCapacity)
                {
                    File.Delete(tmpFileName); // remove the file, we can't keep it anyway
                    return false;
                }

                var shaHashBytes = shaHasher.Hash;
                hash = BitConverter.ToString(shaHashBytes).Replace("-", string.Empty);
            }

            var isNew = !ContainsKey(key);

            var cachePath = GetPath(hash);
            var cachePathDir = Path.GetDirectoryName(cachePath);
            if (!Directory.Exists(cachePathDir))
                Directory.CreateDirectory(cachePathDir);
            File.Move(tmpFileName, cachePath);
            var cacheFileInfo = new FileInfo(cachePath);

            _fileLookup[key] = cachePath;
            var cacheEntry = new CacheEntry(key, Convert.ToUInt64(cacheFileInfo.Length));
            _entryLookup[key] = cacheEntry;

            if (isNew)
                EntryAdded?.Invoke(this, cacheEntry);
            else
                EntryUpdated?.Invoke(this, cacheEntry);

            ApplyCachePolicy();
            return true;
        }

        /// <summary>
        /// Asynchronously stores a value associated with a key.
        /// </summary>
        /// <param name="key">The key used to locate the value in the cache.</param>
        /// <param name="value">A stream of data to store in the cache.</param>
        public async Task SetValueAsync(string key, Stream value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!value.CanRead)
                throw new ArgumentException("The given stream is not readable.", nameof(value));

            ulong totalBytesRead = 0;
            const long bufferSize = 4096;

            var tmpFileName = Path.Combine(CachePath.FullName, Guid.NewGuid().ToString());
            string hash = null;

            using (var shaHasher = new SHA256Managed())
            {
                using (var writer = File.OpenWrite(tmpFileName))
                {
                    byte[] oldBuffer;
                    int oldBytesRead;

                    var buffer = new byte[bufferSize];
                    var bytesRead = await value.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    totalBytesRead += Convert.ToUInt32(bytesRead);

                    do
                    {
                        oldBytesRead = bytesRead;
                        oldBuffer = buffer;

                        buffer = new byte[bufferSize];
                        bytesRead = await value.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        totalBytesRead += Convert.ToUInt32(bytesRead);

                        if (bytesRead == 0)
                        {
                            shaHasher.TransformFinalBlock(oldBuffer, 0, oldBytesRead);
                            await writer.WriteAsync(oldBuffer, 0, oldBytesRead).ConfigureAwait(false);
                        }
                        else
                        {
                            shaHasher.TransformBlock(oldBuffer, 0, oldBytesRead, oldBuffer, 0);
                            await writer.WriteAsync(oldBuffer, 0, oldBytesRead).ConfigureAwait(false);
                        }
                    }
                    while (bytesRead != 0 && totalBytesRead <= MaximumStorageCapacity);
                }

                if (totalBytesRead > MaximumStorageCapacity)
                {
                    File.Delete(tmpFileName); // remove the file, we can't keep it anyway
                    throw new ArgumentException("The given stream received data that was larger than the allotted storage capacity of " + MaximumStorageCapacity.ToString(CultureInfo.InvariantCulture), nameof(value));
                }

                var shaHashBytes = shaHasher.Hash;
                hash = BitConverter.ToString(shaHashBytes).Replace("-", string.Empty);
            }

            var isNew = !ContainsKey(key);

            var cachePath = GetPath(hash);
            var cachePathDir = Path.GetDirectoryName(cachePath);
            if (!Directory.Exists(cachePathDir))
                Directory.CreateDirectory(cachePathDir);
            File.Move(tmpFileName, cachePath);
            var cacheFileInfo = new FileInfo(cachePath);

            _fileLookup[key] = cachePath;
            var cacheEntry = new CacheEntry(key, Convert.ToUInt64(cacheFileInfo.Length));
            _entryLookup[key] = cacheEntry;

            if (isNew)
                EntryAdded?.Invoke(this, cacheEntry);
            else
                EntryUpdated?.Invoke(this, cacheEntry);

            ApplyCachePolicy();
        }

        /// <summary>
        /// Asynchronously stores a value associated with a key.
        /// </summary>
        /// <param name="key">The key used to locate the value in the cache.</param>
        /// <param name="value">A stream of data to store in the cache.</param>
        /// <returns><c>true</c> if the data was able to be stored without error; otherwise <c>false</c>.</returns>
        public async Task<bool> TrySetValueAsync(string key, Stream value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!value.CanRead)
                throw new ArgumentException("The given stream is not readable.", nameof(value));

            ulong totalBytesRead = 0;
            const long bufferSize = 4096;

            var tmpFileName = Path.Combine(CachePath.FullName, Guid.NewGuid().ToString());
            string hash = null;

            using (var shaHasher = new SHA256Managed())
            {
                using (var writer = File.OpenWrite(tmpFileName))
                {
                    byte[] oldBuffer;
                    int oldBytesRead;

                    var buffer = new byte[bufferSize];
                    var bytesRead = await value.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    totalBytesRead += Convert.ToUInt32(bytesRead);

                    do
                    {
                        oldBytesRead = bytesRead;
                        oldBuffer = buffer;

                        buffer = new byte[bufferSize];
                        bytesRead = await value.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        totalBytesRead += Convert.ToUInt32(bytesRead);

                        if (bytesRead == 0)
                        {
                            shaHasher.TransformFinalBlock(oldBuffer, 0, oldBytesRead);
                            await writer.WriteAsync(oldBuffer, 0, oldBytesRead).ConfigureAwait(false);
                        }
                        else
                        {
                            shaHasher.TransformBlock(oldBuffer, 0, oldBytesRead, oldBuffer, 0);
                            await writer.WriteAsync(oldBuffer, 0, oldBytesRead).ConfigureAwait(false);
                        }
                    }
                    while (bytesRead != 0 && totalBytesRead <= MaximumStorageCapacity);
                }

                if (totalBytesRead > MaximumStorageCapacity)
                {
                    File.Delete(tmpFileName); // remove the file, we can't keep it anyway
                    return false;
                }

                var shaHashBytes = shaHasher.Hash;
                hash = BitConverter.ToString(shaHashBytes).Replace("-", string.Empty);
            }

            var isNew = !ContainsKey(key);

            var cachePath = GetPath(hash);
            var cachePathDir = Path.GetDirectoryName(cachePath);
            if (!Directory.Exists(cachePathDir))
                Directory.CreateDirectory(cachePathDir);
            File.Move(tmpFileName, cachePath);
            var cacheFileInfo = new FileInfo(cachePath);

            _fileLookup[key] = cachePath;
            var cacheEntry = new CacheEntry(key, Convert.ToUInt64(cacheFileInfo.Length));
            _entryLookup[key] = cacheEntry;

            if (isNew)
                EntryAdded?.Invoke(this, cacheEntry);
            else
                EntryUpdated?.Invoke(this, cacheEntry);

            ApplyCachePolicy();
            return true;
        }

        /// <summary>
        /// Gets the value associated with a key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <param name="stream">A stream of data from the cache. Will be <c>null</c> when <paramref name="key" /> does not exist in the cache.</param>
        /// <returns><c>true</c> if the cache contains the key; otherwise <c>false</c>.</returns>
        public bool TryGetValue(string key, out Stream stream)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            var hasValue = ContainsKey(key);
            stream = hasValue ? GetValue(key) : null;

            return hasValue;
        }

        /// <summary>
        /// Gets the value associated with a key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>A tuple of two values. A boolean determines whether <paramref name="key" /> is present in the cache. If <paramref name="key" /> is present, the <see cref="Stream" /> value will be provided, otherwise it will be <c>null</c>.</returns>
        public (bool hasValue, Stream stream) TryGetValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            var hasValue = ContainsKey(key);
            var stream = hasValue ? GetValue(key) : null;

            return (hasValue, stream);
        }

        /// <summary>
        /// Asynchronously gets the value associated with a key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>A tuple of two values. A boolean determines whether <paramref name="key" /> is present in the cache. If <paramref name="key" /> is present, the <see cref="Stream" /> value will be provided, otherwise it will be <c>null</c>.</returns>
        public async Task<(bool hasValue, Stream stream)> TryGetValueAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));

            var hasValue = await ContainsKeyAsync(key).ConfigureAwait(false);
            Stream stream = null;
            if (hasValue)
                stream = await GetValueAsync(key).ConfigureAwait(false);

            return (hasValue, stream);
        }

        /// <summary>
        /// Releases all resources held by the cache. Additionally clears the cache directory.
        /// </summary>
        public void Dispose() => Dispose(true);

        /// <summary>
        /// Releases all resources held by the cache. Additionally clears the cache directory.
        /// </summary>
        /// <param name="disposing"><c>true</c> if managed resources are to be disposed. <c>false</c> will not dispose any resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (!disposing)
                return;

            _cts.Cancel();
            Clear();
            _disposed = true;
        }

        /// <summary>
        /// Applies the cache policy to all values held in the cache.
        /// </summary>
        protected void ApplyCachePolicy()
        {
            var expiredEntries = Policy.GetExpiredEntries(_entryLookup.Values, MaximumStorageCapacity);
            foreach (var expiredEntry in expiredEntries)
            {
                var key = expiredEntry.Key;
                var filePath = _fileLookup[key];
                File.Delete(filePath);
                _fileLookup.TryRemove(key, out var tmpFilePath);
                _entryLookup.TryRemove(key, out var lookupEntry);
                EntryRemoved?.Invoke(this, lookupEntry);
            }
        }

        /// <summary>
        /// Retrives a fully qualified path used to store the cached value.
        /// </summary>
        /// <param name="hash">A hash of the contents of the cache.</param>
        /// <returns>A fully qualified path for a cached value.</returns>
        protected virtual string GetPath(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                throw new ArgumentNullException(nameof(hash));
            if (hash.Length != 64)
                throw new ArgumentException("The hash must be a 32 character long representation of a 256-bit hash.", nameof(hash));
            var allValidChars = hash.All(IsValidHexChar);
            if (!allValidChars)
                throw new ArgumentException("The hash must be string containing only hexadecimal characters that represent a 256-bit hash", nameof(hash));

            var firstDir = hash.Substring(0, 2);
            var secondDir = hash.Substring(2, 2);

            return Path.Combine(CachePath.FullName, firstDir, secondDir, hash);
        }

        /// <summary>
        /// Convenience method used to determine whether a character is a valid hexadecimal character.
        /// </summary>
        /// <param name="c">A unicode character.</param>
        /// <returns><c>true</c> if the value is a hexadecimal character; otherwise <c>false</c>.</returns>
        protected static bool IsValidHexChar(char c) => byte.TryParse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tmp);

        private bool _disposed;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, ICacheEntry> _entryLookup = new ConcurrentDictionary<string, ICacheEntry>();
        private readonly ConcurrentDictionary<string, string> _fileLookup = new ConcurrentDictionary<string, string>();

        private readonly static TimeSpan _zero = new TimeSpan(0);
    }
}