﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using NUnit.Framework;
using Spreads.Collections;
using Spreads.Utils;
using System.Collections.Generic;

namespace Spreads.Core.Tests.Collections
{
    // TODO Move to Collections.Tests project
    [TestFixture]
    public class SCMTests
    {
        [Test, Ignore]
        public void EnumerateScmSpeed()
        {
            const int count = 1000000;

            var sl = new SortedList<int, int>();
            var sm = new SortedMap<int, int>();
            var scm = new SortedChunkedMap<int, int>();

            for (int i = 0; i < count; i++)
            {
                if (i % 1000 != 0)
                {
                    sl.Add(i, i);
                    sm.Add(i, i);
                    scm.Add(i, i);
                }
            }

            //var ism = new ImmutableSortedMap<int, int>(sm);

            long sum;

            for (int r = 0; r < 20; r++)
            {
                sum = 0L;
                using (Benchmark.Run("SL", count))
                {
                    foreach (var item in sl)
                    {
                        sum += item.Value;
                    }
                }
                Assert.True(sum > 0);

                sum = 0L;
                using (Benchmark.Run("SM", count))
                {
                    foreach (var item in sm)
                    {
                        sum += item.Value;
                    }
                }
                Assert.True(sum > 0);

                //sum = 0L;
                //using (Benchmark.Run("ISM", count))
                //{
                //    foreach (var item in ism)
                //    {
                //        sum += item.Value;
                //    }
                //}
                //Assert.True(sum > 0);

                sum = 0L;
                using (Benchmark.Run("SCM", count))
                {
                    foreach (var item in scm)
                    {
                        sum += item.Value;
                    }
                }
                Assert.True(sum > 0);
            }

            Benchmark.Dump();
        }
    }
}