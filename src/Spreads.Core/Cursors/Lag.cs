// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Spreads.Cursors.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable once CheckNamespace
namespace Spreads
{
    // TODO Lag1/Previous could use just a single cursor and be much faster

    public struct Lag<TKey, TValue, TCursor> :
        ICursorSeries<TKey, (TValue, (TKey, TValue)), Lag<TKey, TValue, TCursor>>
        where TCursor : ISpecializedCursor<TKey, TValue, TCursor>
    {
        #region Cursor state

        // This region must contain all cursor state that is passed via constructor.
        // No additional state must be created.
        // All state elements should be assigned in Initialize and Clone methods
        // All inner cursors must be disposed in the Dispose method but references to them must be kept (they could be used as factories)
        // for re-initialization.

        // NB must be mutable, could be a struct
        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        internal LagStepImpl<TKey, TValue, TCursor> _cursor;

        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        internal LagStepImpl<TKey, TValue, TCursor> _lookUpCursor;

        internal CursorState State { get; set; }

        #endregion Cursor state

        #region Constructors

        internal Lag(TCursor cursor, int width = 1, int step = 1) : this()
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (step <= 0) throw new ArgumentOutOfRangeException(nameof(width));

            _cursor = new LagStepImpl<TKey, TValue, TCursor>(cursor, width, step);
        }

        #endregion Constructors

        #region Lifetime management

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lag<TKey, TValue, TCursor> Clone()
        {
            var instance = new Lag<TKey, TValue, TCursor>
            {
                _cursor = _cursor.Clone(),
                State = State
            };
            return instance;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lag<TKey, TValue, TCursor> Initialize()
        {
            var instance = new Lag<TKey, TValue, TCursor>
            {
                _cursor = _cursor.Initialize(),
                State = CursorState.Initialized
            };
            return instance;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            // NB keep cursor state for reuse
            // dispose is called on the result of Initialize(), the cursor from
            // constructor could be uninitialized but contain some state, e.g. _value for this FillCursor
            _cursor.Dispose();
            _lookUpCursor.Dispose();
            State = CursorState.None;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            _cursor.Reset();
            State = CursorState.Initialized;
        }

        ICursor<TKey, (TValue, (TKey, TValue))> ICursor<TKey, (TValue, (TKey, TValue))>.Clone()
        {
            return Clone();
        }

        #endregion Lifetime management

        #region ICursor members

        /// <inheritdoc />
        public KeyValuePair<TKey, (TValue, (TKey, TValue))> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return new KeyValuePair<TKey, (TValue, (TKey, TValue))>(CurrentKey, CurrentValue); }
        }

        /// <inheritdoc />
        public TKey CurrentKey
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _cursor.CurrentKey; }
        }

        /// <inheritdoc />
        public (TValue, (TKey, TValue)) CurrentValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (_cursor._cursor.CurrentValue, (_cursor.CurrentValue.CurrentKey, _cursor.CurrentValue.CurrentValue)); }
        }

        /// <inheritdoc />
        public IReadOnlySeries<TKey, (TValue, (TKey, TValue))> CurrentBatch => null;

        /// <inheritdoc />
        public KeyComparer<TKey> Comparer => _cursor.Comparer;

        object IEnumerator.Current => Current;

        /// <summary>
        /// Lag cursor is discrete even if its input cursor is continuous.
        /// </summary>
        public bool IsContinuous => false;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out (TValue, (TKey, TValue)) value)
        {
            if (_lookUpCursor.Equals(default(TCursor)))
            {
                _lookUpCursor = _cursor.Clone();
            }

            if (_lookUpCursor.MoveAt(key, Lookup.EQ))
            {
                value = (_lookUpCursor._cursor.CurrentValue, (_lookUpCursor.CurrentValue.CurrentKey, _lookUpCursor.CurrentValue.CurrentValue));
                return true;
            }

            value = default((TValue, (TKey, TValue)));
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveAt(TKey key, Lookup direction)
        {
            if (State == CursorState.None)
            {
                ThrowHelper.ThrowInvalidOperationException($"ICursorSeries {GetType().Name} is not initialized as a cursor. Call the Initialize() method and *use* (as IDisposable) the returned value to access ICursor MoveXXX members.");
            }
            var moved = _cursor.MoveAt(key, direction);
            if (moved)
            {
                State = CursorState.Moving;
            }
            return moved;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.NoInlining)] // NB NoInlining is important to speed-up MoveNext
        public bool MoveFirst()
        {
            if (State == CursorState.None)
            {
                ThrowHelper.ThrowInvalidOperationException($"ICursorSeries {GetType().Name} is not initialized as a cursor. Call the Initialize() method and *use* (as IDisposable) the returned value to access ICursor MoveXXX members.");
            }
            var moved = _cursor.MoveFirst();
            if (moved)
            {
                State = CursorState.Moving;
            }
            return moved;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.NoInlining)] // NB NoInlining is important to speed-up MovePrevious
        public bool MoveLast()
        {
            if (State == CursorState.None)
            {
                ThrowHelper.ThrowInvalidOperationException($"ICursorSeries {GetType().Name} is not initialized as a cursor. Call the Initialize() method and *use* (as IDisposable) the returned value to access ICursor MoveXXX members.");
            }
            var moved = _cursor.MoveLast();
            if (moved)
            {
                State = CursorState.Moving;
            }
            return moved;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (State < CursorState.Moving) return MoveFirst();
            return _cursor.MoveNext();
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task<bool> MoveNextBatch(CancellationToken cancellationToken)
        {
            if (State == CursorState.None)
            {
                ThrowHelper.ThrowInvalidOperationException($"CursorSeries {GetType().Name} is not initialized as a cursor. Call the Initialize() method and *use* (as IDisposable) the returned value to access ICursor MoveXXX members.");
            }
            return Utils.TaskUtil.FalseTask;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MovePrevious()
        {
            if (State < CursorState.Moving) return MoveLast();
            return _cursor.MovePrevious();
        }

        /// <inheritdoc />
        IReadOnlySeries<TKey, (TValue, (TKey, TValue))> ICursor<TKey, (TValue, (TKey, TValue))>.Source => new Series<TKey, (TValue, (TKey, TValue)), Lag<TKey, TValue, TCursor>>(this);

        /// <summary>
        /// Get a <see cref="Series{TKey,TValue,TCursor}"/> based on this cursor.
        /// </summary>
        public Series<TKey, (TValue, (TKey, TValue)), Lag<TKey, TValue, TCursor>> Source => new Series<TKey, (TValue, (TKey, TValue)), Lag<TKey, TValue, TCursor>>(this);

        /// <inheritdoc />
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        #endregion ICursor members

        #region ICursorSeries members

        /// <inheritdoc />
        public bool IsIndexed => _cursor.Source.IsIndexed;

        /// <inheritdoc />
        public bool IsReadOnly
        {
            // NB this property is repeatedly called from MNA
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _cursor.Source.IsReadOnly; }
        }

        /// <inheritdoc />
        public Task<bool> Updated
        {
            // NB this property is repeatedly called from MNA
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _cursor.Source.Updated; }
        }

        #endregion ICursorSeries members
    }
}