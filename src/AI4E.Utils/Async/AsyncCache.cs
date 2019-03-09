/* License
 * --------------------------------------------------------------------------------------------------------------------
 * This file is part of the AI4E distribution.
 *   (https://github.com/AI4E/AI4E.Utils)
 * Copyright (c) 2018-2019 Andreas Truetschel and contributors.
 * 
 * MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 * --------------------------------------------------------------------------------------------------------------------
 */

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AI4E.Utils.Async
{
    /// <summary>
    /// Represents an updatable cache with asynchronous update operation.
    /// </summary>
    /// <typeparam name="T">The type of cached value.</typeparam>
    /// <remarks>
    /// Update operations may be called concurrently.
    /// This type is thread-safe.
    /// </remarks>
    public sealed class AsyncCache<T> : IDisposable
    {
        private readonly Func<CancellationToken, Task<T>> _operation;
        private TaskCompletionSource<T> _tcs;
        private Task _currentUpdate;
        private CancellationTokenSource _currentUpdateCancellationSource;
        private readonly object _mutex = new object();
        private volatile CancellationTokenSource _disposalCancellationSource;

        /// <summary>
        /// Creates a new instance of the <see cref="AsyncCache{T}"/> type with the specified update operation.
        /// </summary>
        /// <param name="operation">The asynchronous update operation.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="operation"/> is <c>null</c>.</exception>
        public AsyncCache(Func<CancellationToken, Task<T>> operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            _operation = operation;
            _disposalCancellationSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Creates a new instance of the <see cref="AsyncCache{T}"/> type with the specified update operation.
        /// </summary>
        /// <param name="operation">The asynchronous update operation.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="operation"/> is <c>null</c>.</exception>
        public AsyncCache(Func<Task<T>> operation) : this(_ => operation()) { }

        /// <summary>
        /// Gets a task thats result is the cached value.
        /// </summary>
        public Task<T> Task
        {
            get
            {
                CheckObjectDisposed();

                lock (_mutex)
                {
                    if (_tcs == null)
                    {
                        DoUpdateInternal();
                    }

                    return _tcs.Task;
                }
            }
        }

        private void DoUpdateInternal()
        {
            if (_tcs != null)
            {
                Debug.Assert(_currentUpdateCancellationSource != null);
                Debug.Assert(_currentUpdate != null);

                _currentUpdateCancellationSource.Cancel();
                _currentUpdateCancellationSource.Dispose();
                _currentUpdate.HandleExceptions();
            }

            if (_tcs == null || _tcs.Task.Status != TaskStatus.WaitingForActivation)
            {
                _tcs = new TaskCompletionSource<T>();
            }

            _currentUpdateCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(_disposalCancellationSource.Token);
            _currentUpdate = UpdateInternalAsync(_currentUpdateCancellationSource);
        }

        private async Task UpdateInternalAsync(CancellationTokenSource cancellationTokenSource)
        {
            CheckObjectDisposed(out var disposedCancellationSource);
            var cancellation = cancellationTokenSource.Token;

            try
            {
                var result = await _operation(cancellation);

                lock (_mutex)
                {
                    if (cancellationTokenSource == _currentUpdateCancellationSource)
                    {
                        if (disposedCancellationSource.IsCancellationRequested)
                        {
                            _tcs.SetException(new ObjectDisposedException(GetType().FullName));
                        }
                        else
                        {
                            _tcs.SetResult(result);
                        }
                    }
                }
            }
            catch (OperationCanceledException exc)
            {
                lock (_mutex)
                {
                    if (cancellationTokenSource == _currentUpdateCancellationSource)
                    {
                        if (disposedCancellationSource.IsCancellationRequested)
                        {
                            _tcs.SetException(new ObjectDisposedException(GetType().FullName));
                        }
                        else
                        {
                            _tcs.SetCanceled();
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                lock (_mutex)
                {
                    if (cancellationTokenSource == _currentUpdateCancellationSource)
                    {
                        _tcs.SetException(exc);
                    }
                }
            }
        }

        /// <summary>
        /// Initiated a cache update.
        /// </summary>
        public void Update()
        {
            CheckObjectDisposed();

            lock (_mutex)
            {
                DoUpdateInternal();
            }
        }

        /// <summary>
        /// Initiated a cache update and returns a task thats result contains the cached value.
        /// </summary>
        /// <param name="cancellation">A <see cref="CancellationToken"/> used to cancel the asychronous operation or <see cref="CancellationToken.None"/>.</param>
        /// <returns>A <see cref="Task{TResult}"/> thats result contains the cached value.</returns>
        public Task<T> UpdateAsync(CancellationToken cancellation = default)
        {
            CheckObjectDisposed();

            Task<T> task;

            lock (_mutex)
            {
                DoUpdateInternal();
                task = _tcs.Task;
            }

            return task.WithCancellation(cancellation);
        }

        /// <summary>
        /// Disposed of the current instance.
        /// </summary>
        public void Dispose()
        {
            var disposedCancellationSource = Interlocked.Exchange(ref _disposalCancellationSource, null);
            disposedCancellationSource?.Cancel();
            disposedCancellationSource?.Dispose();

            lock (_mutex)
            {
                if (_tcs != null)
                {
                    Debug.Assert(_currentUpdateCancellationSource != null);
                    Debug.Assert(_currentUpdate != null);

                    _currentUpdateCancellationSource.Cancel();
                    _currentUpdateCancellationSource.Dispose();
                    _currentUpdate.HandleExceptions();
                }
            }
        }

        /// <summary>
        /// Gets an awaiter used to await the instance.
        /// </summary>
        /// <returns>The task awaiter.</returns>
        public TaskAwaiter<T> GetAwaiter()
        {
            return Task.GetAwaiter();
        }

        private void CheckObjectDisposed(out CancellationTokenSource disposalCancellationSource)
        {
            disposalCancellationSource = _disposalCancellationSource; // Volatile read op

            if (disposalCancellationSource == null || disposalCancellationSource.IsCancellationRequested)
                ThrowObjectDisposed();
        }

        private void CheckObjectDisposed()
        {
            CheckObjectDisposed(out _);
        }

        private void ThrowObjectDisposed()
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }
}