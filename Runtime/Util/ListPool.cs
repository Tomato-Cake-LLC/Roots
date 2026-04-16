#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Roots {
    public static class ListPool<T> {
        private static readonly ConcurrentBag<List<T>> _pool = new();

        public struct PooledList : IDisposable, IEnumerable<T>, IReadOnlyList<T> {
            private List<T> _list;
            private bool disposed;

            public readonly List<T> list {
                get {
                    if (disposed) {
                        throw new ObjectDisposedException("PooledList");
                    }
                    return _list;
                }
            }

            public readonly int Count => list.Count;

            public PooledList(List<T> list) {
                disposed = false;
                _list = list;
                _list.Clear();
            }

            public void Dispose() {
                _list.Clear();
                _pool.Add(_list);
                disposed = true;
            }

            public void Add(T item) => list.Add(item);

            readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => list.GetEnumerator();
            readonly IEnumerator IEnumerable.GetEnumerator() => list.GetEnumerator();

            public readonly T this[int index] {
                get => list[index];
                set => list[index] = value;
            }
        }

        public static PooledList Take() {
            if (!_pool.TryTake(out var list)) {
                list = new List<T>();
            }
            return new PooledList(list);
        }
    }
}
