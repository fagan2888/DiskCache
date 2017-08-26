﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace SJP.DiskCache
{
    public class SlidingTimespanCachePolicy : ICachePolicy
    {
        public SlidingTimespanCachePolicy(TimeSpan timeSpan)
        {
            if (timeSpan < _zero)
                throw new ArgumentOutOfRangeException("Expiration time spans must be non-negative. The given timespan was instead " + timeSpan.ToString(), nameof(timeSpan));

            ExpirationTimespan = timeSpan;
        }

        public TimeSpan ExpirationTimespan { get; }

        public IEnumerable<ICacheEntry> GetExpiredEntries(IEnumerable<ICacheEntry> entries, ulong maximumStorageCapacity)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            var currentTime = DateTime.Now;
            return entries
                .Where(e => (currentTime - e.LastAccessed) > ExpirationTimespan)
                .OrderBy(e => e.LastAccessed); // given that they're removed, remove least recently used first
        }

        private readonly static TimeSpan _zero = new TimeSpan(0);
    }
}