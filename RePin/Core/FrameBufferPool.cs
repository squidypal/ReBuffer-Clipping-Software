using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ReBuffer.Core
{
    /// <summary>
    /// Custom buffer pool that returns exact-size buffers for frame data.
    /// Unlike ArrayPool which rounds up to power of 2 (wasting 50% memory for video frames),
    /// this pool allocates exactly the size needed.
    /// </summary>
    public sealed class FrameBufferPool : IDisposable
    {
        private readonly ConcurrentBag<byte[]> _pool = new();
        private readonly int _bufferSize;
        private readonly int _maxPoolSize;
        private int _currentPoolSize;
        private int _totalAllocations;
        private int _poolHits;
        private bool _disposed;

        /// <summary>
        /// Gets the exact buffer size this pool manages.
        /// </summary>
        public int BufferSize => _bufferSize;

        /// <summary>
        /// Gets the current number of buffers in the pool.
        /// </summary>
        public int CurrentPoolSize => _currentPoolSize;

        /// <summary>
        /// Gets the total number of allocations made (for diagnostics).
        /// </summary>
        public int TotalAllocations => _totalAllocations;

        /// <summary>
        /// Gets the number of times a buffer was returned from pool (cache hits).
        /// </summary>
        public int PoolHits => _poolHits;

        /// <summary>
        /// Gets the pool hit rate as a percentage.
        /// </summary>
        public double HitRate => _totalAllocations > 0
            ? (_poolHits * 100.0) / _totalAllocations
            : 0;

        /// <summary>
        /// Creates a new frame buffer pool.
        /// </summary>
        /// <param name="bufferSize">Exact size of buffers to allocate.</param>
        /// <param name="maxPoolSize">Maximum number of buffers to keep in pool.</param>
        public FrameBufferPool(int bufferSize, int maxPoolSize = 8)
        {
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be positive.");
            if (maxPoolSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "Max pool size must be positive.");

            _bufferSize = bufferSize;
            _maxPoolSize = maxPoolSize;
        }

        /// <summary>
        /// Rents a buffer from the pool. Always returns a buffer of exactly BufferSize bytes.
        /// </summary>
        /// <returns>A byte array of exactly BufferSize length.</returns>
        public byte[] Rent()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FrameBufferPool));

            Interlocked.Increment(ref _totalAllocations);

            if (_pool.TryTake(out var buffer))
            {
                Interlocked.Decrement(ref _currentPoolSize);
                Interlocked.Increment(ref _poolHits);
                return buffer;
            }

            // Allocate new buffer of exact size
            return new byte[_bufferSize];
        }

        /// <summary>
        /// Returns a buffer to the pool. Buffer must be exactly BufferSize bytes.
        /// </summary>
        /// <param name="buffer">The buffer to return.</param>
        /// <param name="clearBuffer">Whether to clear the buffer before returning (default: false for performance).</param>
        public void Return(byte[] buffer, bool clearBuffer = false)
        {
            if (_disposed) return;
            if (buffer == null) return;

            // Only accept buffers of the correct size
            if (buffer.Length != _bufferSize) return;

            // Only pool up to max size
            if (_currentPoolSize >= _maxPoolSize) return;

            if (clearBuffer)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            _pool.Add(buffer);
            Interlocked.Increment(ref _currentPoolSize);
        }

        /// <summary>
        /// Pre-allocates buffers to warm up the pool.
        /// Call this before starting recording to avoid allocation during capture.
        /// </summary>
        /// <param name="count">Number of buffers to pre-allocate.</param>
        public void Warmup(int count)
        {
            if (_disposed) return;

            count = Math.Min(count, _maxPoolSize - _currentPoolSize);
            for (int i = 0; i < count; i++)
            {
                _pool.Add(new byte[_bufferSize]);
                Interlocked.Increment(ref _currentPoolSize);
            }
        }

        /// <summary>
        /// Clears all buffers from the pool to free memory.
        /// </summary>
        public void Clear()
        {
            while (_pool.TryTake(out _))
            {
                Interlocked.Decrement(ref _currentPoolSize);
            }
        }

        /// <summary>
        /// Gets diagnostic information about the pool.
        /// </summary>
        public string GetDiagnostics()
        {
            return $"FrameBufferPool: Size={_bufferSize:N0} bytes, " +
                   $"Pooled={_currentPoolSize}/{_maxPoolSize}, " +
                   $"Allocations={_totalAllocations:N0}, " +
                   $"HitRate={HitRate:F1}%";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
