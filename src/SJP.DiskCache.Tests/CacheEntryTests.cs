using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SJP.DiskCache.Tests
{
    [TestFixture]
    public class CacheEntryTests
    {
        [Test]
        public void Ctor_GivenNullString_ThrowsArgNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new CacheEntry(null, 123));
        }

        [Test]
        public void Ctor_GivenEmptyString_ThrowsArgNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new CacheEntry(string.Empty, 123));
        }

        [Test]
        public void Ctor_GivenWhiteSpaceString_ThrowsArgNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new CacheEntry("   ", 123));
        }

        [Test]
        public void Ctor_GivenZeroSize_ThrowsArgException()
        {
            Assert.Throws<ArgumentException>(() => new CacheEntry("test", 0));
        }

        [Test]
        public void Key_OnObjectCreate_SetToCtorArg()
        {
            const string expected = "test";
            var entry = new CacheEntry(expected, 123);

            Assert.AreEqual(expected, entry.Key);
        }

        [Test]
        public void Size_OnObjectCreate_SetToCtorArg()
        {
            const ulong expected = 123;
            var entry = new CacheEntry("test", expected);

            Assert.AreEqual(expected, entry.Size);
        }

        [Test]
        public void LastAccessed_OnObjectCreate_InitializedToCreationTime()
        {
            var entry = new CacheEntry("test", 123);

            var start = entry.CreationTime;
            var end = entry.LastAccessed;

            var diff = end - start;
            Assert.Less(diff, TimeSpan.FromMilliseconds(50));
        }

        [Test]
        public void AccessCount_WhenAccessed_IsIncremented()
        {
            var entry = new CacheEntry("test", 123);
            Assert.AreEqual(0, entry.AccessCount);

            entry.Refresh();
            Assert.AreEqual(1, entry.AccessCount);

            entry.Refresh();
            Assert.AreEqual(2, entry.AccessCount);
        }

        [Test]
        public void LastAccessed_OnPropertyGet_IncreasesOverTime()
        {
            var entry = new CacheEntry("test", 123);

            var start = entry.CreationTime;
            Task.Delay(1000).Wait();
            var end = entry.LastAccessed;

            Assert.Greater(end, start);
        }

        [Test]
        public async Task Refresh_WhenInvoked_ResetsLastAccessed()
        {
            var entry = new CacheEntry("test", 123);

            var accessedTime = entry.LastAccessed;
            await Task.Delay(100).ConfigureAwait(false);
            entry.Refresh();
            var laterAccessTime = entry.LastAccessed;

            Assert.Greater(laterAccessTime, accessedTime);
        }
    }
}
