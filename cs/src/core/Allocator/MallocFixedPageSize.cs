﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

#pragma warning disable 0162

#define CALLOC

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    internal class CountdownWrapper
    {
        // Separate event for sync code and tcs for async code: Do not block on async code.
        private readonly CountdownEvent syncEvent;
        private readonly TaskCompletionSource<int> asyncTcs;
        int remaining;

        internal CountdownWrapper(int count, bool isAsync)
        {
            if (isAsync)
            {
                this.asyncTcs = new TaskCompletionSource<int>();
                this.remaining = count;
                return;
            }
            this.syncEvent = new CountdownEvent(count);
        }

        internal bool IsCompleted => this.syncEvent is null ? this.remaining == 0 : this.syncEvent.IsSet;

        internal void Wait() => this.syncEvent.Wait();
        internal async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            using var reg = cancellationToken.Register(() => this.asyncTcs.TrySetCanceled());
            await this.asyncTcs.Task.ConfigureAwait(false);
        }

        internal void Decrement()
        {
            if (this.asyncTcs is not null)
            {
                Debug.Assert(this.remaining > 0);
                if (Interlocked.Decrement(ref this.remaining) == 0)
                    this.asyncTcs.TrySetResult(0);
                return;
            }
            this.syncEvent.Signal();
        }
    }

    /// <summary>
    /// Memory allocator for objects
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class MallocFixedPageSize<T> : IDisposable
    {
        private const bool ForceUnpinnedAllocation = false;

        private const int PageSizeBits = 16;
        private const int PageSize = 1 << PageSizeBits;
        private const int PageSizeMask = PageSize - 1;
        private const int LevelSizeBits = 12;
        private const int LevelSize = 1 << LevelSizeBits;

        private readonly T[][] values = new T[LevelSize][];
#if !NET5_0_OR_GREATER
        private readonly GCHandle[] handles = new GCHandle[LevelSize];
#endif
        private readonly IntPtr[] pointers = new IntPtr[LevelSize];

        private readonly int RecordSize;

        private volatile int writeCacheLevel;

        private volatile int count;

        private readonly bool IsPinned;
        private const bool ReturnPhysicalAddress = false;

        private int checkpointCallbackCount;
        private SemaphoreSlim checkpointSemaphore;

        private ConcurrentQueue<long> freeList;

        /// <summary>
        /// Create new instance
        /// </summary>
        public unsafe MallocFixedPageSize()
        {
            freeList = new ConcurrentQueue<long>();
            if (ForceUnpinnedAllocation)
                IsPinned = false;
            else
                IsPinned = Utility.IsBlittable<T>();

#if NET5_0_OR_GREATER
            values[0] = GC.AllocateArray<T>(PageSize, IsPinned);
            if (IsPinned)
            {
                pointers[0] = (IntPtr)Unsafe.AsPointer(ref values[0][0]);
                RecordSize = Marshal.SizeOf(values[0][0]);
            }
#else
            values[0] = new T[PageSize];
            if (IsPinned)
            {
                handles[0] = GCHandle.Alloc(values[0], GCHandleType.Pinned);
                pointers[0] = handles[0].AddrOfPinnedObject();
                RecordSize = Marshal.SizeOf(values[0][0]);
            }
#endif

#if !(CALLOC)
            Array.Clear(values[0], 0, PageSize);
#endif

            writeCacheLevel = -1;
            Interlocked.MemoryBarrier();

            BulkAllocate(); // null pointer
        }

        /// <summary>
        /// Get physical address
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetPhysicalAddress(long address)
        {
            if (ReturnPhysicalAddress)
            {
                return address;
            }
            else
            {
                return
                    (long)pointers[address >> PageSizeBits]
                  + (long)(address & PageSizeMask) * RecordSize;
            }
        }

        /// <summary>
        /// Get object
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(long index)
        {
            if (ReturnPhysicalAddress)
                throw new FasterException("Physical pointer returned by allocator: de-reference pointer to get records instead of calling Get");

            return ref values
                [index >> PageSizeBits]
                [index & PageSizeMask];
        }


        /// <summary>
        /// Set object
        /// </summary>
        /// <param name="index"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(long index, ref T value)
        {
            if (ReturnPhysicalAddress)
                throw new FasterException("Physical pointer returned by allocator: de-reference pointer to set records instead of calling Set (otherwise, set ForceUnpinnedAllocation to true)");

            values
                [index >> PageSizeBits]
                [index & PageSizeMask]
                = value;
        }



        /// <summary>
        /// Free object
        /// </summary>
        /// <param name="pointer"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(long pointer)
        {
            if (!ReturnPhysicalAddress)
            {
                values[pointer >> PageSizeBits][pointer & PageSizeMask] = default;
            }

            freeList.Enqueue(pointer);
        }

        private const int kAllocateChunkSize = 16;

        /// <summary>
        /// Warning: cannot mix 'n' match use of
        /// Allocate and BulkAllocate
        /// </summary>
        /// <returns></returns>
        public unsafe long BulkAllocate()
        {
            // Determine insertion index.
            // ReSharper disable once CSharpWarnings::CS0420
#pragma warning disable 420
            int index = Interlocked.Add(ref count, kAllocateChunkSize) - kAllocateChunkSize;
#pragma warning restore 420

            int offset = index & PageSizeMask;
            int baseAddr = index >> PageSizeBits;

            // Handle indexes in first batch specially because they do not use write cache.
            if (baseAddr == 0)
            {
                // If index 0, then allocate space for next level.
                if (index == 0)
                {
#if NET5_0_OR_GREATER
                    var tmp = GC.AllocateArray<T>(PageSize, IsPinned);
                    pointers[1] = (IntPtr)Unsafe.AsPointer(ref tmp[0]);
#else
                    var tmp = new T[PageSize];
                    if (IsPinned)
                    {
                        handles[1] = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                        pointers[1] = handles[1].AddrOfPinnedObject();
                    }
#endif

#if !(CALLOC)
                    Array.Clear(tmp, 0, PageSize);
#endif
                    values[1] = tmp;
                    Interlocked.MemoryBarrier();
                }

                // Return location.
                if (ReturnPhysicalAddress)
                    return (((long)pointers[0]) + index * RecordSize);
                else
                    return index;
            }

            // See if write cache contains corresponding array.
            var cache = writeCacheLevel;
            T[] array;

            if (cache != -1)
            {
                // Write cache is correct array only if index is within [arrayCapacity, 2*arrayCapacity).
                if (cache == baseAddr)
                {
                    // Return location.
                    if (ReturnPhysicalAddress)
                        return ((long)pointers[baseAddr]) + (long)offset * RecordSize;
                    else
                        return index;
                }
            }

            // Write cache did not work, so get level information from index.
            // int level = GetLevelFromIndex(index);

            // Spin-wait until level has an allocated array.
            var spinner = new SpinWait();
            while (true)
            {
                array = values[baseAddr];
                if (array != null)
                {
                    break;
                }
                spinner.SpinOnce();
            }

            // Perform extra actions if inserting at offset 0 of level.
            if (offset == 0)
            {
                // Update write cache to point to current level.
                writeCacheLevel = baseAddr;
                Interlocked.MemoryBarrier();

                // Allocate for next page
                int newBaseAddr = baseAddr + 1;

#if NET5_0_OR_GREATER
                var tmp = GC.AllocateArray<T>(PageSize, IsPinned);
                if (IsPinned) pointers[newBaseAddr] = (IntPtr)Unsafe.AsPointer(ref tmp[0]);
#else
                var tmp = new T[PageSize];
                if (IsPinned)
                {
                    handles[newBaseAddr] = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                    pointers[newBaseAddr] = handles[newBaseAddr].AddrOfPinnedObject();
                }
#endif

#if !(CALLOC)
                Array.Clear(tmp, 0, PageSize);
#endif
                values[newBaseAddr] = tmp;

                Interlocked.MemoryBarrier();
            }

            // Return location.
            if (ReturnPhysicalAddress)
                return ((long)pointers[baseAddr]) + (long)offset * RecordSize;
            else
                return index;
        }

        /// <summary>
        /// Allocate
        /// </summary>
        /// <returns></returns>
        public unsafe long Allocate()
        {
            if (freeList.TryDequeue(out long result))
                return result;

            // Determine insertion index.
            // ReSharper disable once CSharpWarnings::CS0420
#pragma warning disable 420
            int index = Interlocked.Increment(ref count) - 1;
#pragma warning restore 420

            int offset = index & PageSizeMask;
            int baseAddr = index >> PageSizeBits;

            // Handle indexes in first batch specially because they do not use write cache.
            if (baseAddr == 0)
            {
                // If index 0, then allocate space for next level.
                if (index == 0)
                {
#if NET5_0_OR_GREATER
                    var tmp = GC.AllocateArray<T>(PageSize, IsPinned);
                    if (IsPinned) pointers[1] = (IntPtr)Unsafe.AsPointer(ref tmp[0]);
#else
                    var tmp = new T[PageSize];
                    if (IsPinned)
                    {
                        handles[1] = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                        pointers[1] = handles[1].AddrOfPinnedObject();
                    }
#endif

#if !(CALLOC)
                    Array.Clear(tmp, 0, PageSize);
#endif
                    values[1] = tmp;
                    Interlocked.MemoryBarrier();
                }

                // Return location.
                if (ReturnPhysicalAddress)
                    return ((long)pointers[0]) + index * RecordSize;
                else
                    return index;
            }

            // See if write cache contains corresponding array.
            var cache = writeCacheLevel;
            T[] array;

            if (cache != -1)
            {
                // Write cache is correct array only if index is within [arrayCapacity, 2*arrayCapacity).
                if (cache == baseAddr)
                {
                    // Return location.
                    if (ReturnPhysicalAddress)
                        return ((long)pointers[baseAddr]) + (long)offset * RecordSize;
                    else
                        return index;
                }
            }

            // Write cache did not work, so get level information from index.
            // int level = GetLevelFromIndex(index);

            // Spin-wait until level has an allocated array.
            var spinner = new SpinWait();
            while (true)
            {
                array = values[baseAddr];
                if (array != null)
                {
                    break;
                }
                spinner.SpinOnce();
            }

            // Perform extra actions if inserting at offset 0 of level.
            if (offset == 0)
            {
                // Update write cache to point to current level.
                writeCacheLevel = baseAddr;
                Interlocked.MemoryBarrier();

                // Allocate for next page
                int newBaseAddr = baseAddr + 1;

#if NET5_0_OR_GREATER
                var tmp = GC.AllocateArray<T>(PageSize, IsPinned);
                if (IsPinned) pointers[newBaseAddr] = (IntPtr)Unsafe.AsPointer(ref tmp[0]);
#else
                var tmp = new T[PageSize];
                if (IsPinned)
                {
                    handles[newBaseAddr] = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                    pointers[newBaseAddr] = handles[newBaseAddr].AddrOfPinnedObject();
                }
#endif

#if !(CALLOC)
                Array.Clear(tmp, 0, PageSize);
#endif
                values[newBaseAddr] = tmp;

                Interlocked.MemoryBarrier();
            }

            // Return location.
            if (ReturnPhysicalAddress)
                return ((long)pointers[baseAddr]) + (long)offset * RecordSize;
            else
                return index;
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
#if !NET5_0_OR_GREATER
            for (int i = 0; i < values.Length; i++)
            {
                if (IsPinned && handles[i].IsAllocated) handles[i].Free();
            }
#endif
            count = 0;
        }


#region Checkpoint

        /// <summary>
        /// Is checkpoint complete
        /// </summary>
        /// <returns></returns>
        public bool IsCheckpointCompleted()
        {
            return checkpointCallbackCount == 0;
        }

        /// <summary>
        /// Is checkpoint completed
        /// </summary>
        /// <returns></returns>
        public async ValueTask IsCheckpointCompletedAsync(CancellationToken token = default)
        {
            var s = checkpointSemaphore;
            await s.WaitAsync(token).ConfigureAwait(false);
            s.Release();
        }

        /// <summary>
        /// Public facing persistence API
        /// </summary>
        /// <param name="device"></param>
        /// <param name="offset"></param>
        /// <param name="numBytesWritten"></param>
        public void BeginCheckpoint(IDevice device, ulong offset, out ulong numBytesWritten)
            => BeginCheckpoint(device, offset, out numBytesWritten, false, default, default);

        /// <summary>
        /// Internal persistence API
        /// </summary>
        /// <param name="device"></param>
        /// <param name="offset"></param>
        /// <param name="numBytesWritten"></param>
        /// <param name="useReadCache"></param>
        /// <param name="skipReadCache"></param>
        /// <param name="epoch"></param>
        internal unsafe void BeginCheckpoint(IDevice device, ulong offset, out ulong numBytesWritten, bool useReadCache, SkipReadCache skipReadCache, LightEpoch epoch)
        {
            int localCount = count;
            int recordsCountInLastLevel = localCount & PageSizeMask;
            int numCompleteLevels = localCount >> PageSizeBits;
            int numLevels = numCompleteLevels + (recordsCountInLastLevel > 0 ? 1 : 0);
            checkpointCallbackCount = numLevels;
            checkpointSemaphore = new SemaphoreSlim(0);
            uint alignedPageSize = PageSize * (uint)RecordSize;
            uint lastLevelSize = (uint)recordsCountInLastLevel * (uint)RecordSize;

            int sectorSize = (int)device.SectorSize;
            numBytesWritten = 0;
            for (int i = 0; i < numLevels; i++)
            {
                OverflowPagesFlushAsyncResult result = default;
                
                uint writeSize = (uint)((i == numCompleteLevels) ? (lastLevelSize + (sectorSize - 1)) & ~(sectorSize - 1) : alignedPageSize);

                if (!useReadCache)
                {
                    device.WriteAsync(pointers[i], offset + numBytesWritten, writeSize, AsyncFlushCallback, result);
                }
                else
                {
                    result.mem = new SectorAlignedMemory((int)writeSize, (int)device.SectorSize);
                    bool prot = false;
                    if (!epoch.ThisInstanceProtected())
                    {
                        prot = true;
                        epoch.Resume();
                    }

                    Buffer.MemoryCopy((void*)pointers[i], result.mem.aligned_pointer, writeSize, writeSize);
                    int j = 0;
                    if (i == 0) j += kAllocateChunkSize*RecordSize;
                    for (; j < writeSize; j += sizeof(HashBucket))
                    {
                        skipReadCache((HashBucket*)(result.mem.aligned_pointer + j));
                    }
                    
                    if (prot) epoch.Suspend();

                    device.WriteAsync((IntPtr)result.mem.aligned_pointer, offset + numBytesWritten, writeSize, AsyncFlushCallback, result);
                }
                numBytesWritten += writeSize;
            }
        }

        private unsafe void AsyncFlushCallback(uint errorCode, uint numBytes, object context)
        {
            if (errorCode != 0)
            {
                Trace.TraceError("AsyncFlushCallback error: {0}", errorCode);
            }

            var mem = ((OverflowPagesFlushAsyncResult)context).mem;
            mem?.Dispose();

            if (Interlocked.Decrement(ref checkpointCallbackCount) == 0)
            {
                checkpointSemaphore.Release();
            }
        }

        /// <summary>
        /// Max valid address
        /// </summary>
        /// <returns></returns>
        public int GetMaxValidAddress()
        {
            return count;
        }

        /// <summary>
        /// Get page size
        /// </summary>
        /// <returns></returns>
        public int GetPageSize()
        {
            return PageSize;
        }
#endregion

#region Recover
        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="device"></param>
        /// <param name="buckets"></param>
        /// <param name="numBytes"></param>
        /// <param name="offset"></param>
        public void Recover(IDevice device, ulong offset, int buckets, ulong numBytes)
        {
            BeginRecovery(device, offset, buckets, numBytes, out _);
        }

        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="device"></param>
        /// <param name="buckets"></param>
        /// <param name="numBytes"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="offset"></param>
        public async ValueTask<ulong> RecoverAsync(IDevice device, ulong offset, int buckets, ulong numBytes, CancellationToken cancellationToken)
        {
            BeginRecovery(device, offset, buckets, numBytes, out ulong numBytesRead, isAsync: true);
            await this.recoveryCountdown.WaitAsync(cancellationToken).ConfigureAwait(false);
            return numBytesRead;
        }

        /// <summary>
        /// Check if recovery complete
        /// </summary>
        /// <param name="waitUntilComplete"></param>
        /// <returns></returns>
        public bool IsRecoveryCompleted(bool waitUntilComplete = false)
        {
            bool completed = this.recoveryCountdown.IsCompleted;
            if (!completed && waitUntilComplete)
            {
                this.recoveryCountdown.Wait();
                return true;
            }
            return completed;
        }

        // Implementation of asynchronous recovery
        private CountdownWrapper recoveryCountdown;

        internal unsafe void BeginRecovery(IDevice device,
                                    ulong offset,
                                    int buckets,
                                    ulong numBytesToRead,
                                    out ulong numBytesRead,
                                    bool isAsync = false)
        {
            // Allocate as many records in memory
            while (count < buckets)
            {
                Allocate();
            }

            int numRecords = (int)numBytesToRead / RecordSize;
            int recordsCountInLastLevel = numRecords & PageSizeMask;
            int numCompleteLevels = numRecords >> PageSizeBits;
            int numLevels = numCompleteLevels + (recordsCountInLastLevel > 0 ? 1 : 0);

            this.recoveryCountdown = new CountdownWrapper(numLevels, isAsync);

            numBytesRead = 0;
            uint alignedPageSize = (uint)PageSize * (uint)RecordSize;
            uint lastLevelSize = (uint)recordsCountInLastLevel * (uint)RecordSize;
            for (int i = 0; i < numLevels; i++)
            {
                //read a full page
                uint length = (uint)PageSize * (uint)RecordSize;
                OverflowPagesReadAsyncResult result = default;
                device.ReadAsync(offset + numBytesRead, pointers[i], length, AsyncPageReadCallback, result);
                numBytesRead += (i == numCompleteLevels) ? lastLevelSize : alignedPageSize;
            }
        }

        private unsafe void AsyncPageReadCallback(uint errorCode, uint numBytes, object context)
        {
            if (errorCode != 0)
            {
                System.Diagnostics.Trace.TraceError("AsyncPageReadCallback error: {0}", errorCode);
            }
            this.recoveryCountdown.Decrement();
        }
#endregion
    }
}
