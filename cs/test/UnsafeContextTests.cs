﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Linq;
using FASTER.core;
using NUnit.Framework;

namespace FASTER.test.UnsafeContext
{
    //** These tests ensure the basics are fully covered - taken from BasicFASTERTests

    [TestFixture]
    internal class BasicUnsafeContextTests
    {
        private FasterKV<KeyStruct, ValueStruct> fht;
        private ClientSession<KeyStruct, ValueStruct, InputStruct, OutputStruct, Empty, Functions> fullSession;
        private UnsafeContext<KeyStruct, ValueStruct, InputStruct, OutputStruct, Empty, Functions> uContext;
        private IDevice log;
        private string path;
        TestUtils.DeviceType deviceType;

        [SetUp]
        public void Setup()
        {
            path = TestUtils.MethodTestDir + "/";

            // Clean up log files from previous test runs in case they weren't cleaned up
            TestUtils.DeleteDirectory(path, wait: true);
        }

        private void Setup(long size, LogSettings logSettings, TestUtils.DeviceType deviceType)
        {
            string filename = path + TestContext.CurrentContext.Test.Name + deviceType.ToString() + ".log";
            log = TestUtils.CreateTestDevice(deviceType, filename);
            logSettings.LogDevice = log;
            fht = new FasterKV<KeyStruct, ValueStruct>(size, logSettings);
            fullSession = fht.For(new Functions()).NewSession<Functions>();
            uContext = fullSession.GetUnsafeContext();
        }

        [TearDown]
        public void TearDown()
        {
            uContext?.Dispose();
            uContext = null;
            fullSession?.Dispose();
            fullSession = null;
            fht?.Dispose();
            fht = null;
            log?.Dispose();
            log = null;
            TestUtils.DeleteDirectory(path);
        }

        private void AssertCompleted(Status expected, Status actual)
        {
            if (actual.IsPending)
                (actual, _) = CompletePendingResult();
            Assert.AreEqual(expected, actual);
        }

        private (Status status, OutputStruct output) CompletePendingResult()
        {
            uContext.CompletePendingWithOutputs(out var completedOutputs);
            return TestUtils.GetSinglePendingResult(completedOutputs);
        }

        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void NativeInMemWriteRead([Values] TestUtils.DeviceType deviceType)
        {
            Setup(128, new LogSettings { PageSizeBits = 10, MemorySizeBits = 12, SegmentSizeBits = 22 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;
                OutputStruct output = default;

                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);

                AssertCompleted(new (StatusCode.Found), status);
                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void NativeInMemWriteReadDelete([Values] TestUtils.DeviceType deviceType)
        {
            Setup(128, new LogSettings { PageSizeBits = 10, MemorySizeBits = 12, SegmentSizeBits = 22 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;
                OutputStruct output = default;

                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);
                AssertCompleted(new(StatusCode.Found), status);

                uContext.Delete(ref key1, Empty.Default, 0);

                status = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);
                AssertCompleted(new(StatusCode.NotFound), status);

                var key2 = new KeyStruct { kfield1 = 14, kfield2 = 15 };
                var value2 = new ValueStruct { vfield1 = 24, vfield2 = 25 };

                uContext.Upsert(ref key2, ref value2, Empty.Default, 0);
                status = uContext.Read(ref key2, ref input, ref output, Empty.Default, 0);

                AssertCompleted(new(StatusCode.Found), status);
                Assert.AreEqual(value2.vfield1, output.value.vfield1);
                Assert.AreEqual(value2.vfield2, output.value.vfield2);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }


        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void NativeInMemWriteReadDelete2()
        {
            // Just set this one since Write Read Delete already does all four devices
            deviceType = TestUtils.DeviceType.MLSD;

            const int count = 10;

            // Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            Setup(128, new LogSettings { MemorySizeBits = 29 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;
                OutputStruct output = default;

                for (int i = 0; i < 10 * count; i++)
                {
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = 14 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = 24 };

                    uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                }

                for (int i = 0; i < 10 * count; i++)
                {
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = 14 };
                    uContext.Delete(ref key1, Empty.Default, 0);
                }

                for (int i = 0; i < 10 * count; i++)
                {
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = 14 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = 24 };

                    var status = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);
                    AssertCompleted(new(StatusCode.NotFound), status);

                    uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                }

                for (int i = 0; i < 10 * count; i++)
                {
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = 14 };
                    var status = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);
                    AssertCompleted(new(StatusCode.Found), status);
                }
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public unsafe void NativeInMemWriteRead2()
        {
            // Just use this one instead of all four devices since InMemWriteRead covers all four devices
            deviceType = TestUtils.DeviceType.MLSD;

            int count = 200;

            // Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            Setup(128, new LogSettings { MemorySizeBits = 29 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;

                Random r = new(10);
                for (int c = 0; c < count; c++)
                {
                    var i = r.Next(10000);
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                }

                r = new Random(10);

                for (int c = 0; c < count; c++)
                {
                    var i = r.Next(10000);
                    OutputStruct output = default;
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };

                    if (uContext.Read(ref key1, ref input, ref output, Empty.Default, 0).IsPending)
                    {
                        uContext.CompletePending(true);
                    }

                    Assert.AreEqual(value.vfield1, output.value.vfield1);
                    Assert.AreEqual(value.vfield2, output.value.vfield2);
                }

                // Clean up and retry - should not find now
                fht.Log.ShiftBeginAddress(fht.Log.TailAddress, truncateLog: true);

                r = new Random(10);
                for (int c = 0; c < count; c++)
                {
                    var i = r.Next(10000);
                    OutputStruct output = default;
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    Assert.IsFalse(uContext.Read(ref key1, ref input, ref output, Empty.Default, 0).Found);
                }
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public unsafe void TestShiftHeadAddress([Values] TestUtils.DeviceType deviceType)
        {
            InputStruct input = default;
            const int RandSeed = 10;
            const int RandRange = 10000;
            const int NumRecs = 200;

            Random r = new(RandSeed);
            var sw = Stopwatch.StartNew();

            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                for (int c = 0; c < NumRecs; c++)
                {
                    var i = r.Next(RandRange);
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                }

                r = new Random(RandSeed);
                sw.Restart();

                for (int c = 0; c < NumRecs; c++)
                {
                    var i = r.Next(RandRange);
                    OutputStruct output = default;
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };

                    if (!uContext.Read(ref key1, ref input, ref output, Empty.Default, 0).IsPending)
                    {
                        Assert.AreEqual(value.vfield1, output.value.vfield1);
                        Assert.AreEqual(value.vfield2, output.value.vfield2);
                    }
                }
                uContext.CompletePending(true);

                // Shift head and retry - should not find in main memory now
                fht.Log.FlushAndEvict(true);

                r = new Random(RandSeed);
                sw.Restart();

                for (int c = 0; c < NumRecs; c++)
                {
                    var i = r.Next(RandRange);
                    OutputStruct output = default;
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    Status foundStatus = uContext.Read(ref key1, ref input, ref output, Empty.Default, 0);
                    Assert.IsTrue(foundStatus.IsPending);
                }

                uContext.CompletePendingWithOutputs(out var outputs, wait: true);
                int count = 0;
                while (outputs.Next())
                {
                    count++;
                    Assert.AreEqual(outputs.Current.Key.kfield1, outputs.Current.Output.value.vfield1);
                    Assert.AreEqual(outputs.Current.Key.kfield2, outputs.Current.Output.value.vfield2);
                }
                outputs.Dispose();
                Assert.AreEqual(NumRecs, count);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public unsafe void NativeInMemRMWRefKeys([Values] TestUtils.DeviceType deviceType)
        {
            InputStruct input = default;
            OutputStruct output = default;

            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                var nums = Enumerable.Range(0, 1000).ToArray();
                var rnd = new Random(11);
                for (int i = 0; i < nums.Length; ++i)
                {
                    int randomIndex = rnd.Next(nums.Length);
                    int temp = nums[randomIndex];
                    nums[randomIndex] = nums[i];
                    nums[i] = temp;
                }

                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    input = new InputStruct { ifield1 = i, ifield2 = i + 1 };
                    uContext.RMW(ref key1, ref input, Empty.Default, 0);
                }
                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    input = new InputStruct { ifield1 = i, ifield2 = i + 1 };
                    if (uContext.RMW(ref key1, ref input, ref output, Empty.Default, 0).IsPending)
                    {
                        uContext.CompletePending(true);
                    }
                    else
                    {
                        Assert.AreEqual(2 * i, output.value.vfield1);
                        Assert.AreEqual(2 * (i + 1), output.value.vfield2);
                    }
                }

                Status status;
                KeyStruct key;

                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];

                    key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    ValueStruct value = new() { vfield1 = i, vfield2 = i + 1 };

                    status = uContext.Read(ref key, ref input, ref output, Empty.Default, 0);

                    AssertCompleted(new(StatusCode.Found), status);
                    Assert.AreEqual(2 * value.vfield1, output.value.vfield1);
                    Assert.AreEqual(2 * value.vfield2, output.value.vfield2);
                }

                key = new KeyStruct { kfield1 = nums.Length, kfield2 = nums.Length + 1 };
                status = uContext.Read(ref key, ref input, ref output, Empty.Default, 0);
                AssertCompleted(new(StatusCode.NotFound), status);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Tests the overload where no reference params used: key,input,userContext,serialNo
        [Test]
        [Category("FasterKV")]
        public unsafe void NativeInMemRMWNoRefKeys([Values] TestUtils.DeviceType deviceType)
        {
            InputStruct input = default;

            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                var nums = Enumerable.Range(0, 1000).ToArray();
                var rnd = new Random(11);
                for (int i = 0; i < nums.Length; ++i)
                {
                    int randomIndex = rnd.Next(nums.Length);
                    int temp = nums[randomIndex];
                    nums[randomIndex] = nums[i];
                    nums[i] = temp;
                }

                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    input = new InputStruct { ifield1 = i, ifield2 = i + 1 };
                    uContext.RMW(ref key1, ref input, Empty.Default, 0);
                }
                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];
                    var key1 = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    input = new InputStruct { ifield1 = i, ifield2 = i + 1 };
                    uContext.RMW(key1, input);  // no ref and do not set any other params
                }

                OutputStruct output = default;
                Status status;
                KeyStruct key;

                for (int j = 0; j < nums.Length; ++j)
                {
                    var i = nums[j];

                    key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    ValueStruct value = new() { vfield1 = i, vfield2 = i + 1 };

                    status = uContext.Read(ref key, ref input, ref output, Empty.Default, 0);

                    AssertCompleted(new(StatusCode.Found), status);
                    Assert.AreEqual(2 * value.vfield1, output.value.vfield1);
                    Assert.AreEqual(2 * value.vfield2, output.value.vfield2);
                }

                key = new KeyStruct { kfield1 = nums.Length, kfield2 = nums.Length + 1 };
                status = uContext.Read(ref key, ref input, ref output, Empty.Default, 0);
                AssertCompleted(new(StatusCode.NotFound), status);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Tests the overload of .Read(key, input, out output,  context, serialNo)
        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void ReadNoRefKeyInputOutput([Values] TestUtils.DeviceType deviceType)
        {
            InputStruct input = default;

            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(key1, input, out OutputStruct output, Empty.Default, 111);
                AssertCompleted(new(StatusCode.Found), status);

                // Verify the read data
                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Test the overload call of .Read (key, out output, userContext, serialNo)
        [Test]
        [Category("FasterKV")]
        public void ReadNoRefKey([Values] TestUtils.DeviceType deviceType)
        {
            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(key1, out OutputStruct output, Empty.Default, 1);
                AssertCompleted(new(StatusCode.Found), status);

                // Verify the read data
                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }


        // Test the overload call of .Read (ref key, ref output, userContext, serialNo)
        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void ReadWithoutInput([Values] TestUtils.DeviceType deviceType)
        {
            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                OutputStruct output = default;

                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(ref key1, ref output, Empty.Default, 99);
                AssertCompleted(new(StatusCode.Found), status);

                // Verify the read data
                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Test the overload call of .Read (ref key, ref input, ref output, ref recordInfo, userContext: context)
        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void ReadWithoutSerialID()
        {
            // Just checking without Serial ID so one device type is enough
            deviceType = TestUtils.DeviceType.MLSD;

            Setup(128, new LogSettings { MemorySizeBits = 29 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;
                OutputStruct output = default;

                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.Read(ref key1, ref input, ref output, Empty.Default);
                AssertCompleted(new(StatusCode.Found), status);

                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Test the overload call of .Read (key)
        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void ReadBareMinParams([Values] TestUtils.DeviceType deviceType)
        {
            Setup(128, new LogSettings { MemorySizeBits = 22, SegmentSizeBits = 22, PageSizeBits = 10 }, deviceType);
            uContext.ResumeThread();

            try
            {
                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);

                var (status, output) = uContext.Read(key1);
                AssertCompleted(new(StatusCode.Found), status);

                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }

        // Test the ReadAtAddress where ReadFlags = ReadFlags.none
        [Test]
        [Category("FasterKV")]
        [Category("Smoke")]
        public void ReadAtAddressReadFlagsNone()
        {
            // Just functional test of ReadFlag so one device is enough
            deviceType = TestUtils.DeviceType.MLSD;

            Setup(128, new LogSettings { MemorySizeBits = 29 }, deviceType);
            uContext.ResumeThread();

            try
            {
                InputStruct input = default;
                OutputStruct output = default;

                var key1 = new KeyStruct { kfield1 = 13, kfield2 = 14 };
                var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };
                ReadOptions readOptions = new() { StartAddress = fht.Log.BeginAddress };

                uContext.Upsert(ref key1, ref value, Empty.Default, 0);
                var status = uContext.ReadAtAddress(ref input, ref output, ref readOptions, Empty.Default, 0);
                AssertCompleted(new(StatusCode.Found), status);

                Assert.AreEqual(value.vfield1, output.value.vfield1);
                Assert.AreEqual(value.vfield2, output.value.vfield2);
                Assert.AreEqual(key1.kfield1, 13);
                Assert.AreEqual(key1.kfield2, 14);
            }
            finally
            {
                uContext.SuspendThread();
            }
        }
    }
}