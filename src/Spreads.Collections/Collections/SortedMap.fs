﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.


namespace Spreads.Collections

// TODO Pooling and cursor counter to avoid disposing of a SM with outstanding cursors

open System
open System.Linq
open System.Diagnostics
open System.Collections
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices
open System.Reflection

open Spreads
open Spreads.Buffers
open Spreads.Serialization
open Spreads.Collections

// NB: IsSyncronized = false means completely thread unsafe. When it is false, the only cost should be checking it and incrementing next order version.
// IsSyncronized is set to true whenever we create a cursor from a thread different from the constructor thread. 
// IsSyncronized is true by default - better to be safe than sorry.


// NB: Why regular keys? Because we do not care about daily or hourly data, but there are 1440 (480) minutes in a day (trading hours)
// with the same diff between each consequitive minute. The number is much bigger with seconds and msecs so that
// memory saving is meaningful, while vectorized calculations on values benefit from fast comparison of regular keys.
// Ticks (and seconds for illiquid instruments) are not regular, but they are never equal among different instruments.


/// Mutable sorted thread-safe IMutableSeries<'K,'V> implementation similar to SCG.SortedList<'K,'V>
[<AllowNullLiteral>]
[<Sealed>]
[<DebuggerTypeProxy(typeof<IDictionaryDebugView<_,_>>)>]
[<DebuggerDisplay("SortedMap: Count = {Count}")>]
type SortedMap<'K,'V>
  internal(dictionary:IDictionary<'K,'V> option, capacity:int option, comparerOpt:KeyComparer<'K> option) as this=
  inherit ContainerSeries<'K,'V, SortedMapCursor<'K,'V>>()
  static do
    SortedMap<'K,'V>.Init()

  // data fields
  [<DefaultValueAttribute>]
  val mutable internal version : int64
  [<DefaultValueAttribute>]
  val mutable internal size : int
  [<DefaultValueAttribute>]
  val mutable internal keys : 'K array
  [<DefaultValueAttribute>]
  val mutable internal values : 'V array

  static let empty = lazy (let sm = new SortedMap<'K,'V>() in sm.Complete();sm)

  [<DefaultValueAttribute>] 
  val mutable internal orderVersion : int64
  [<DefaultValueAttribute>] 
  val mutable internal nextVersion : int64
  
  // util fields
  let comparer : KeyComparer<'K> = 
    if comparerOpt.IsNone || Comparer<'K>.Default.Equals(comparerOpt.Value) then
      KeyComparer<'K>.Default
    else comparerOpt.Value // do not try to replace with KeyComparer if a comparer was given

  [<DefaultValueAttribute>] 
  val mutable isKeyReferenceType : bool
  
  let mutable couldHaveRegularKeys : bool = comparer.IsDiffable

  // TODO Remove this
  let mutable diffCalc : KeyComparer<'K> = comparer
  
  let mutable rkStep_ : int64 = 0L
  let mutable rkLast = Unchecked.defaultof<'K>

  [<DefaultValueAttribute>] 
  val mutable isSynchronized : bool
  [<DefaultValueAttribute>] 
  val mutable isReadOnly : bool
  let ownerThreadId : int = Thread.CurrentThread.ManagedThreadId
  let mutable mapKey = String.Empty

  [<DefaultValueAttribute>] 
  val mutable internal subscriberCount : int
  [<DefaultValueAttribute>] 
  val mutable internal isReadyToDispose : int

  do
    // NB: There is no single imaginable reason not to have it true by default!
    // Uncontended performance is close to non-synced.
    this.isSynchronized <- true
    this.isKeyReferenceType <- not <| typeof<'K>.GetTypeInfo().IsValueType

    let tempCap = if capacity.IsSome && capacity.Value > 0 then capacity.Value else 2
    this.keys <- 
      if couldHaveRegularKeys then 
        // regular keys are the first and the second value, their diff is the step
        // NB: Buffer pools could return a buffer greater than the requested length,
        // but for regular keys we always need a fixed-length array of size 2, so we allocate a new one.
        // TODO wrap the corefx buffer and for len = 2 use a special self-adjusting ObjectPool, because these 
        // arrays are not short-lived and could accumulate in gen 1+ easily.
        Array.zeroCreate 2
      else BufferPool<'K>.Rent(tempCap) 
    this.values <- BufferPool<'V>.Rent(tempCap)

    if dictionary.IsSome && dictionary.Value.Count > 0 then
      match dictionary.Value with
      // TODO SCM
      | :? SortedMap<'K,'V> as map ->
        if map.IsReadOnly then Trace.TraceWarning("TODO: reuse arrays of immutable map")
        let mutable entered = false
        try
          entered <- enterWriteLockIf &map.Locker true
          couldHaveRegularKeys <- map.IsRegular
          this.SetCapacity(map.size)
          this.size <- map.size
          Array.Copy(map.keys, 0, this.keys, 0, map.size)
          Array.Copy(map.values, 0, this.values, 0, map.size)
        finally
          exitWriteLockIf &map.Locker entered
      | _ ->
        // TODO ICollection interface to IMutableSeries
        let locked, sr = match dictionary.Value with | :? ICollection as col -> col.IsSynchronized, col.SyncRoot | _ -> false, null
        let entered = enterLockIf sr locked
        try
          if capacity.IsSome && capacity.Value < dictionary.Value.Count then 
            raise (ArgumentException("capacity is less then dictionary this.size"))
          else
            this.SetCapacity(dictionary.Value.Count)
        
          let tempKeys = BufferPool<_>.Rent(dictionary.Value.Keys.Count)
          dictionary.Value.Keys.CopyTo(tempKeys, 0)
          dictionary.Value.Values.CopyTo(this.values, 0)
          // NB IDictionary guarantees there is no duplicates
          Array.Sort(tempKeys, this.values, 0, dictionary.Value.Keys.Count, comparer)
          this.size <- dictionary.Value.Count

          if couldHaveRegularKeys && this.size > 1 then // if could be regular based on initial check of comparer type
            let isReg, step, regularKeys = this.rkCheckArray tempKeys this.size (comparer)
            couldHaveRegularKeys <- isReg
            if couldHaveRegularKeys then 
              this.keys <- regularKeys
              BufferPool<_>.Return(tempKeys, true) |> ignore
              rkLast <- this.rkKeyAtIndex2 (this.size - 1)
            else
              this.keys <- tempKeys
          else
            this.keys <- tempKeys
        finally
          exitLockIf sr entered
        

  static member internal Init() =
    let converter = {
      new ArrayBasedMapConverter<'K,'V, SortedMap<'K,'V>>() with
        member __.Read(ptr : IntPtr, [<Out>]value: byref<SortedMap<'K,'V>> ) = 
          let totalSize = Marshal.ReadInt32(ptr)
          let version = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 4L))
          let mapSize = Marshal.ReadInt32(new IntPtr(ptr.ToInt64() + 8L))
          let mapVersion = Marshal.ReadInt64(new IntPtr(ptr.ToInt64() + 12L))
          let isRegular = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 12L + 8L)) > 0uy
          let isReadOnly = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 12L + 8L + 1L)) > 0uy
          value <- 
            if mapSize > 0 then
              let ptr = new IntPtr(ptr.ToInt64() + 8L + 14L)
              let mutable keysArray = Unchecked.defaultof<'K[]>
              let mutable keysCount = 0
              let keysLen = CompressedArrayBinaryConverter<'K>.Instance.Read(ptr, &keysArray, &keysCount, false)
              let keys = 
                if isRegular then
                  let arr = Array.sub keysArray 0 2
                  BufferPool<_>.Return keysArray |> ignore
                  arr
                else keysArray
              let ptr = new IntPtr(ptr.ToInt64() + (int64 keysLen))
              let mutable valuesArray = Unchecked.defaultof<'V[]>
              let mutable valuesCount = 0
              let valuesLen = CompressedArrayBinaryConverter<'V>.Instance.Read(ptr, &valuesArray, &valuesCount, false)
              Debug.Assert((totalSize = 8 + 14 + keysLen + valuesLen))
              let sm : SortedMap<'K,'V> = SortedMap.OfSortedKeysAndValues(keys, valuesArray, mapSize, KeyComparer<'K>.Default, false, isRegular)
              sm.version <- mapVersion
              sm.nextVersion <- mapVersion
              sm.orderVersion <- mapVersion
              sm.isReadOnly <- isReadOnly
              sm
            else 
              let sm = new SortedMap<'K,'V> ()
              sm.version <- mapVersion
              sm.nextVersion <- mapVersion
              sm.orderVersion <- mapVersion
              sm.isReadOnly <- isReadOnly
              sm
          totalSize
    }
    Serialization.TypeHelper<SortedMap<'K,'V>>.RegisterConverter(converter, true);
  //#region Private & Internal members

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.rkGetStep() =
    #if DEBUG
    Trace.Assert(this.size > 1)
    #endif
    if rkStep_ > 0L then rkStep_
    elif this.size > 1 then
      rkStep_ <- diffCalc.Diff(this.keys.[1], this.keys.[0])
      rkStep_
    else raise (InvalidOperationException("Cannot calculate regular keys step for a single element in a map or an empty map"))
  
  // temp solution, cannot call inlined method from do in ctor
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.rkKeyAtIndex2 (idx:int) : 'K =
    let step = this.rkGetStep()
    diffCalc.Add(this.keys.[0], (int64 idx) * step)
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.rkKeyAtIndex (idx:int) : 'K =
    let step = this.rkGetStep()
    diffCalc.Add(this.keys.[0], (int64 idx) * step)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.rkIndexOfKey (key:'K) : int =
    #if DEBUG
    Trace.Assert(this.size > 1)
    #endif

    let diff = diffCalc.Diff(key, this.keys.[0])
    let step = this.rkGetStep()
    let idxL : int64 = (diff / step)
    let modulo = (diff - step * idxL)
    let idx = idxL

//    https://msdn.microsoft.com/en-us/library/2cy9f6wb(v=vs.110).aspx
//    The index of the specified value in the specified array, if value is found.
//    If value is not found and value is less than one or more elements in array, 
//    a negative number which is the bitwise complement of the index of the first 
//    element that is larger than value. If value is not found and value is greater 
//    than any of the elements in array, a negative number which is the bitwise 
//    complement of (the index of the last element plus 1).

    // TODO test it for diff <> step, bug-prone stuff here
    if modulo = 0L then
      if idx < 0L then
        ~~~0 // -1 for searches, insert will take ~~~
      elif idx >= int64 this.size then
        ~~~this.size
      else int idx
    else
      if idx <= 0L && diff < 0L then
        ~~~0 // -1 for searches, insert will take ~~~
      elif idx >= int64 this.size then
        ~~~this.size
      else
        ~~~((int idx)+1)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.rkMaterialize () =
    let step = this.rkGetStep()
    Array.init this.values.Length (fun i -> if i < this.size then diffCalc.Add(this.keys.[0], (int64 i)*step) else Unchecked.defaultof<'K>)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.rkCheckArray (sortedArray:'K[]) (size:int) (dc:IKeyComparer<'K>) : bool * int * 'K array = 
    if size > sortedArray.Length then raise (ArgumentException("size is greater than sortedArray length"))
    if size < 1 then
      true, 0, [|Unchecked.defaultof<'K>;Unchecked.defaultof<'K>|]
    elif size < 2 then
      true, 0, [|sortedArray.[0];Unchecked.defaultof<'K>|]
    elif size < 3 then 
      true, int <| dc.Diff(sortedArray.[1], sortedArray.[0]), [|sortedArray.[0];sortedArray.[1]|]
    else
      let firstDiff = dc.Diff(sortedArray.[1], sortedArray.[0])
      let mutable isReg = true
      let mutable n = 2
      while isReg && n < size do
        let newDiff = dc.Diff(sortedArray.[n], sortedArray.[n-1])
        if newDiff <> firstDiff then
          isReg <- false
        n <- n + 1
      if isReg then
        true, int firstDiff, [|sortedArray.[0];sortedArray.[1]|]
      else
        false, 0, Unchecked.defaultof<'K[]>

  // need this for the SortedMapCursor
  member inline private this.SetRkLast(rkl) = rkLast <- rkl
  member private this.Clone() = new SortedMap<'K,'V>(Some(this :> IDictionary<'K,'V>), None, Some(comparer))

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline internal this.CheckNull(key) =
    if this.isKeyReferenceType && EqualityComparer<'K>.Default.Equals(key, Unchecked.defaultof<'K>) then raise (ArgumentNullException("key"))
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline internal this.GetKeyByIndexUnchecked(index) =
    if couldHaveRegularKeys && this.size > 1 then this.rkKeyAtIndex index
    else this.keys.[index]

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline internal this.GetKeyByIndex(index) =
    if uint32 index >= uint32 this.size then raise (ArgumentOutOfRangeException("index"))
    this.GetKeyByIndexUnchecked(index)
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.GetPairByIndexUnchecked(index) =
    if couldHaveRegularKeys && this.size > 1 then
      Trace.Assert(uint32 index < uint32 this.size, "Index must be checked before calling GetPairByIndexUnchecked")
      KeyValuePair(this.rkKeyAtIndex index, this.values.[index])
    else KeyValuePair(this.keys.[index], this.values.[index]) 
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.CompareToFirst (k:'K) =
    comparer.Compare(k, this.keys.[0]) // keys.[0] is always the first key even for regular keys
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member inline private this.CompareToLast (k:'K) =
    if couldHaveRegularKeys && this.size > 1 then
      #if DEBUG
      Trace.Assert(not <| Unchecked.equals rkLast Unchecked.defaultof<'K>)
      #endif
      comparer.Compare(k, rkLast)
    else comparer.Compare(k, this.keys.[this.size-1])

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.EnsureCapacity(min) = 
    let mutable num = this.values.Length * 2 
    if num > 2146435071 then num <- 2146435071
    if num < min then num <- min // either double or min if min > 2xprevious
    this.SetCapacity(num)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  //[<ReliabilityContractAttribute(Consistency.MayCorruptInstance, Cer.MayFail)>]
  member inline private this.Insert(index:int, k, v) =
    if this.isReadOnly then invalidOp "SortedMap is not mutable"
    // key is always new, checks are before this method
    // already inside a lock statement in a caller method if synchronized
   
    if this.size = this.values.Length then this.EnsureCapacity(this.size + 1)
    #if DEBUG
    Trace.Assert(index <= this.size, "index must be <= this.size")
    Trace.Assert(couldHaveRegularKeys || (this.values.Length = this.keys.Length), "keys and values must have equal length for non-regular case")
    #endif
    // for values it is alway the same operation
    if index < this.size then Array.Copy(this.values, index, this.values, index + 1, this.size - index);
    this.values.[index] <- v
    // for regular keys must do some math to check if they will remain regular after the insertion
    // treat sizes 1,2(after insertion) as non-regular because they are always both regular and not 
    if couldHaveRegularKeys then
      if this.size > 1 then
        let step = this.rkGetStep()
        if comparer.Compare(diffCalc.Add(rkLast, step), k) = 0 then
          // adding next regular, only rkLast changes
          rkLast <- k
        elif comparer.Compare(diffCalc.Add(this.keys.[0], -step), k) = 0 then
          this.keys.[1] <- this.keys.[0]
          this.keys.[0] <- k // change first key and size++ at the bottom
          //rkLast is unchanged
        else
          let diff = diffCalc.Diff(k, this.keys.[0])
          let idxL : int64 = (diff / step)
          let modIsOk = (diff - step * idxL) = 0L // gives 13% boost for add compared to diff % step
          let idx = int idxL 
          if modIsOk && idx > -1 && idx < this.size then
            // error for regular keys, this means we insert existing key
            let msg = "Existing key check must be done before insert. SortedMap code is wrong."
            Environment.FailFast(msg, new ApplicationException(msg))            
          else
            // insertting more than 1 away from end or before start, with a hole
            this.keys <- this.rkMaterialize() 
            couldHaveRegularKeys <- false
      else
        if index < this.size then
          Array.Copy(this.keys, index, this.keys, index + 1, this.size - index);
        else 
          rkLast <- k
        this.keys.[index] <- k
        
    // couldHaveRegularKeys could be set to false inside the previous block even if it was true before
    if not couldHaveRegularKeys then
      if index < this.size then
        Array.Copy(this.keys, index, this.keys, index + 1, this.size - index);
      this.keys.[index] <- k
      // do not check if could regularize back, it is very rare 
      // that an irregular becomes a regular one, and such check is always done on
      // bucket switch in SHM (TODO really? check) and before serialization
      // the 99% use case is when we load data from a sequential stream or deserialize a map with already regularized keys
    this.size <- this.size + 1
    this.NotifyUpdate(true)


  member this.Complete() =
    let mutable entered = false
    try
      entered <- enterWriteLockIf &this.Locker true
      //if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if not this.isReadOnly then 
          this.isReadOnly <- true
          // immutable doesn't need sync
          Volatile.Write(&this.isSynchronized, false)
          this.NotifyUpdate(false)
          //if this.subscribersCounter > 0 then this.onUpdateEvent.Trigger(false)
    finally
      //Interlocked.Increment(&this.version) |> ignore
      exitWriteLockIf &this.Locker entered

  override this.IsReadOnly with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.isReadOnly)
  override this.IsIndexed with get() = false

  member this.IsSynchronized 
    with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> Volatile.Read(&this.isSynchronized))
    and set(synced:bool) =
      let wasSynced = Volatile.Read(&this.isSynchronized)
      readLockIf &this.nextVersion &this.version wasSynced (fun _ ->
        // NB: multiple set of the same value is ok as long as all write method
        // read this.isSynchronized into a local variable before writing
        // (all write methods should use entered var and not touch this.isSynced after entering locks)
        Volatile.Write(&this.isSynchronized, synced)
        if synced && not wasSynced then this.nextVersion <- this.version
      )

  member internal this.MapKey with get() = mapKey and set(key:string) = mapKey <- key

  member this.IsRegular 
    with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> couldHaveRegularKeys) 
    and private set (v) = couldHaveRegularKeys <- v

  member this.RegularStep 
    with get() = 
      readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> try this.rkGetStep() with | _ -> 0L)

  member this.Version 
    with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.version)
    and internal set v = 
      let mutable entered = false
      try
        entered <- enterWriteLockIf &this.Locker true
        this.version <- v // NB setter only for deserializer
        this.nextVersion <- v
      finally
        exitWriteLockIf &this.Locker entered

  //#endregion


  //#region Public members

  member private this.SetCapacity(value) =
    match value with
    | c when c = this.values.Length -> ()
    | c when c < this.size -> raise (ArgumentOutOfRangeException("Small capacity"))
    | c when c > 0 -> 
      if this.isReadOnly then invalidOp "SortedMap is read-only"
      
      // first, take new buffers. this could cause out-of-memory
      let kArr : 'K array = 
        if couldHaveRegularKeys then
          Trace.Assert(this.keys.Length = 2)
          Unchecked.defaultof<_>
        else
          BufferPool<_>.Rent(c)
      let vArr : 'V array = BufferPool<_>.Rent(c)

      try
        // TODO this needs review. Looks like very overcompicated for almost imaginary edge case.
        // Basically, we want to protect from async exceptions
        // such as Thread.Abort, Out-of-memory, SO. The region in the finally should be executed entirely
        //RuntimeHelpers.PrepareConstrainedRegions()
        //try ()
        //finally
        if not couldHaveRegularKeys then
          Array.Copy(this.keys, 0, kArr, 0, this.size)
          let toReturn = this.keys
          this.keys <- kArr
          BufferPool<_>.Return(toReturn, true) |> ignore
        Array.Copy(this.values, 0, vArr, 0, this.size)
        let toReturn = this.values
        this.values <- vArr
        BufferPool<_>.Return(toReturn, true) |> ignore
      with
      // NB see enterWriteLockIf comment and https://github.com/dotnet/corefx/issues/1345#issuecomment-147569967
      // If we were able to get new arrays without OOM but got some out-of-band exception during
      // copying, then we corrupted state and should die
      | _ as ex -> Environment.FailFast(ex.Message, ex)
    | _ -> ()

  member this.Capacity
    with get() = 
      readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
        this.values.Length
      )
    and set(value) =
      let mutable entered = false
      try
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        this.SetCapacity(value)
      finally
        exitWriteLockIf &this.Locker entered

  override this.Comparer with get() = comparer

  member this.Clear() =
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if couldHaveRegularKeys then
        Trace.Assert(this.keys.Length = 2)
        Array.Clear(this.keys, 0, 2)
      else
        Array.Clear(this.keys, 0, this.size)
      Array.Clear(this.values, 0, this.size)
      this.size <- 0
      increment &this.orderVersion
    finally
      Interlocked.Increment(&this.version) |> ignore
      exitWriteLockIf &this.Locker entered

  member this.Count with get() = this.size

  override this.IsEmpty with get() = this.size = 0

  override this.Keys 
    with get() =
      {new IList<'K> with
        member x.Count with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.size)
        member x.IsReadOnly with get() = true
        member x.Item 
          with get index : 'K = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.GetKeyByIndex(index))
          and set index value = raise (NotSupportedException("Keys collection is read-only"))
        member x.Add(k) = raise (NotSupportedException("Keys collection is read-only"))
        member x.Clear() = raise (NotSupportedException("Keys collection is read-only"))
        member x.Contains(key) = this.ContainsKey(key)
        member x.CopyTo(array, arrayIndex) =
          let mutable entered = false
          try
            entered <- enterWriteLockIf &this.Locker this.IsSynchronized
            if couldHaveRegularKeys && this.size > 2 then
              Array.Copy(this.rkMaterialize(), 0, array, arrayIndex, this.size)
            else
              Array.Copy(this.keys, 0, array, arrayIndex, this.size)
          finally
            exitWriteLockIf &this.Locker entered

        member x.IndexOf(key:'K) = this.IndexOfKey(key)
        member x.Insert(index, value) = raise (NotSupportedException("Keys collection is read-only"))
        member x.Remove(key:'K) = raise (NotSupportedException("Keys collection is read-only"))
        member x.RemoveAt(index:int) = raise (NotSupportedException("Keys collection is read-only"))
        member x.GetEnumerator() = x.GetEnumerator() :> IEnumerator
        member x.GetEnumerator() : IEnumerator<'K> = 
          let index = ref 0
          let eVersion = ref this.version
          let currentKey : 'K ref = ref Unchecked.defaultof<'K>
          { new IEnumerator<'K> with
            member e.Current with get() = currentKey.Value
            member e.Current with get() = box e.Current
            member e.MoveNext() =
              let nextIndex = index.Value + 1
              readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
                if eVersion.Value <> this.version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                if index.Value < this.size then
                  currentKey := 
                    if couldHaveRegularKeys && this.size > 1 then diffCalc.Add(this.keys.[0], (int64 !index)*this.rkGetStep()) 
                    else this.keys.[!index]
                  index := nextIndex
                  true
                else
                  index := this.size + 1
                  currentKey := Unchecked.defaultof<'K>
                  false
              )
            member e.Reset() =
              readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
                if eVersion.Value <> this.version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                index := 0
                currentKey := Unchecked.defaultof<'K>
              )
            member e.Dispose() = 
              index := 0
              currentKey := Unchecked.defaultof<'K>
          }
      } :> IEnumerable<_>

  override this.Values
    with get() =
      { new IList<'V> with
        member x.Count with get() = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.size)
        member x.IsReadOnly with get() = true
        member x.Item 
          with get index : 'V = readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ -> this.values.[index])
          and set index value = raise (NotSupportedException("Values collection is read-only"))
        member x.Add(k) = raise (NotSupportedException("Values colelction is read-only"))
        member x.Clear() = raise (NotSupportedException("Values colelction is read-only"))
        member x.Contains(value) = this.ContainsValue(value)
        member x.CopyTo(array, arrayIndex) =
          let mutable entered = false
          try
            entered <- enterWriteLockIf &this.Locker this.IsSynchronized
            Array.Copy(this.values, 0, array, arrayIndex, this.size)
          finally
            exitWriteLockIf &this.Locker entered
          
        member x.IndexOf(value:'V) = this.IndexOfValue(value)
        member x.Insert(index, value) = raise (NotSupportedException("Values collection is read-only"))
        member x.Remove(value:'V) = raise (NotSupportedException("Values collection is read-only"))
        member x.RemoveAt(index:int) = raise (NotSupportedException("Values collection is read-only"))
        member x.GetEnumerator() = x.GetEnumerator() :> IEnumerator
        member x.GetEnumerator() : IEnumerator<'V> = 
          let index = ref 0
          let eVersion = ref this.version
          let currentValue : 'V ref = ref Unchecked.defaultof<'V>
          { new IEnumerator<'V> with
            member e.Current with get() = currentValue.Value
            member e.Current with get() = box e.Current
            member e.MoveNext() =
              let nextIndex = index.Value + 1
              readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
                if eVersion.Value <> this.version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                if index.Value < this.size then
                  currentValue := this.values.[index.Value]
                  index := nextIndex
                  true
                else
                  index := this.size + 1
                  currentValue := Unchecked.defaultof<'V>
                  false
              )
            member e.Reset() =
              readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
                if eVersion.Value <> this.version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                index := 0
                currentValue := Unchecked.defaultof<'V>
              )
            member e.Dispose() = 
              index := 0
              currentValue := Unchecked.defaultof<'V>
          }
        }  :> IEnumerable<_>

  member this.ContainsKey(key) = this.IndexOfKey(key) >= 0

  member this.ContainsValue(value) = this.IndexOfValue(value) >= 0

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member internal this.IndexOfKeyUnchecked(key:'K) : int =
    if couldHaveRegularKeys && this.size > 1 then this.rkIndexOfKey key
    else Array.BinarySearch(this.keys, 0, this.size, key, comparer)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.IndexOfKey(key:'K) : int =
    this.CheckNull(key)
    readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
      this.IndexOfKeyUnchecked(key)
    )

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.IndexOfValue(value:'V) : int =
    readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
      let mutable res = 0
      let mutable found = false
      let valueComparer = Comparer<'V>.Default;
      while not found do
          if valueComparer.Compare(value,this.values.[res]) = 0 then
              found <- true
          else res <- res + 1
      if found then res else -1
    )


  override this.First
    with get() =
      readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
        this.FirstUnchecked
      )

  member private this.FirstUnchecked
    with get() =
      KeyValuePair(this.keys.[0], this.values.[0])
      
  override this.Last
    with get() =
      readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
        this.LastUnchecked
      )

  member private this.LastUnchecked
    with get() =
      if couldHaveRegularKeys && this.size > 1 then
        Trace.Assert(comparer.Compare(rkLast, diffCalc.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep())) = 0)
        KeyValuePair(rkLast, this.values.[this.size - 1])
      else KeyValuePair(this.keys.[this.size - 1], this.values.[this.size - 1])
  
  member this.Item
      with get key =
        this.CheckNull(key)
        readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
        // first/last optimization (only last here)
        if this.size = 0 then
          raise (KeyNotFoundException())
        else
          let lc = this.CompareToLast key
          if lc < 0 then
            let index = this.IndexOfKeyUnchecked(key)
            if index >= 0 then this.values.[index]
            else raise (KeyNotFoundException())
          elif lc = 0 then // key = last key
            this.values.[this.size-1]
          else raise (KeyNotFoundException())              
        )
      and
        //[<ReliabilityContractAttribute(Consistency.MayCorruptInstance, Cer.MayFail)>]
        set k v =
          if this.isKeyReferenceType && EqualityComparer<'K>.Default.Equals(k, Unchecked.defaultof<'K>) then raise (ArgumentNullException("key"))

          let mutable keepOrderVersion = false
          let mutable entered = false
          #if DEBUG
          let mutable finished = false
          #endif
          try
            try ()
            finally
              entered <- enterWriteLockIf &this.Locker this.isSynchronized
              if entered then Interlocked.Increment(&this.nextVersion) |> ignore
            // first/last optimization (only last here)
            if this.size = 0 then
              this.Insert(0, k, v)
              keepOrderVersion <- true
            else
              let lc = this.CompareToLast k
              if lc = 0 then // key = last key
                this.values.[this.size-1] <- v
                this.NotifyUpdate(true)
              elif lc > 0 then // adding last value, Insert won't copy arrays if enough capacity
                this.Insert(this.size, k, v)
                keepOrderVersion <- true
              else
                let index = this.IndexOfKeyUnchecked(k)
                if index >= 0 then // contains key 
                  this.values.[index] <- v
                  this.NotifyUpdate(true)
                else
                  this.Insert(~~~index, k, v)
            #if DEBUG
            finished <- true
            #endif
          finally
            Interlocked.Increment(&this.version) |> ignore
            if not keepOrderVersion then increment(&this.orderVersion)
            exitWriteLockIf &this.Locker entered
            #if DEBUG
            if not finished then Environment.FailFast("SM.Item set must always succeed")
            if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
            #else
            if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
            #endif


  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.Add(key, value) : unit =
    this.CheckNull(key)
    
    let mutable keepOrderVersion = false
    let mutable added = false
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.size = 0 then
        this.Insert(0, key, value)
        keepOrderVersion <- true
      else
        // last optimization gives near 2x performance boost
        let lc = this.CompareToLast key
        if lc = 0 then // key = last key
          //exitWriteLockIf &this.locker entered
          raise (ArgumentException("SortedMap.Add: key already exists: " + key.ToString()))
        elif lc > 0 then // adding last value, Insert won't copy arrays if enough capacity
          this.Insert(this.size, key, value)
          keepOrderVersion <- true
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then // contains key
            //exitWriteLockIf &this.locker entered
            raise (ArgumentException("SortedMap.Add: key already exists: " + key.ToString()))
          else
            this.Insert(~~~index, key, value)
      added <- true
    finally
      if not keepOrderVersion then increment(&this.orderVersion)
      if added then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.AddLast(key, value):unit =
    let mutable entered = false
    let mutable added = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.size = 0 then
        this.Insert(0, key, value)
      else
        let c = this.CompareToLast key
        if c > 0 then 
          this.Insert(this.size, key, value)
        else
          let exn = OutOfOrderKeyException(this.LastUnchecked.Key, key, "SortedMap.AddLast: New key is smaller or equal to the largest existing key")
          raise (exn)
      added <- true
    finally
      exitWriteLockIf &this.Locker entered
      if added then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif

  // TODO lockless AddLast for temporary Append implementation
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.AddLastUnchecked(key, value) : unit =
      if this.size = 0 then
        this.Insert(0, key, value)
      else
        let c = this.CompareToLast key
        if c > 0 then 
          this.Insert(this.size, key, value)
        else
          Environment.FailFast("SortedMap.AddLastUnchecked: New key is smaller or equal to the largest existing key")

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.AddFirst(key, value):unit =
    let mutable keepOrderVersion = false
    let mutable entered = false
    let mutable added = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.size = 0 then
        this.Insert(0, key, value)
        keepOrderVersion <- true
      else
        let c = this.CompareToFirst key
        if c < 0 then
            this.Insert(0, key, value)
        else 
          let exn = OutOfOrderKeyException(this.FirstUnchecked.Key, key, "SortedMap.AddFirst: New key is larger or equal to the smallest existing key")
          raise (exn)
      added <- true
    finally
      if added then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      if not keepOrderVersion then increment(&this.orderVersion)
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member private this.RemoveAt(index):unit =
    if uint32 index >= uint32 this.size then raise (ArgumentOutOfRangeException("index"))
    let newSize = this.size - 1
    // TODO review, check for off by 1 bugs, could had lost focus at 3 AM
    // keys
    if couldHaveRegularKeys && this.size > 2 then // will have >= 2 after removal
      if index = 0 then
        this.keys.[0] <- (diffCalc.Add(this.keys.[0], this.rkGetStep())) // change first key to next and size--
        this.keys.[1] <- (diffCalc.Add(this.keys.[0], this.rkGetStep())) // add step to the new first value
      elif index = newSize then 
        rkLast <- diffCalc.Add(this.keys.[0], (int64 (newSize-1))*this.rkGetStep()) // removing last, only size--
      else
        // removing within range,  creating a hole
        this.keys <- this.rkMaterialize()
        couldHaveRegularKeys <- false
    elif couldHaveRegularKeys && this.size = 2 then // will have single value with undefined step
      if index = 0 then
        this.keys.[0] <- this.keys.[1]
        this.keys.[1] <- Unchecked.defaultof<'K>
      elif index = 1 then
        rkLast <- this.keys.[0]
      rkStep_ <- 0L

    if not couldHaveRegularKeys || this.size = 1 then
      if index < this.size then
        Array.Copy(this.keys, index + 1, this.keys, index, newSize - index) // this.size
      this.keys.[newSize] <- Unchecked.defaultof<'K>
      
    // values
    if index < newSize then
      Array.Copy(this.values, index + 1, this.values, index, newSize - index) //this.size
    this.values.[newSize] <- Unchecked.defaultof<'V>

    this.size <- newSize

    this.NotifyUpdate(true)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.Remove(key): bool =
    this.CheckNull(key)
    let mutable removed = false
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.isReadOnly then invalidOp "SortedMap is read-only"
      let index = this.IndexOfKeyUnchecked(key)
      if index >= 0 then 
        this.RemoveAt(index)
        increment &this.orderVersion
        removed <- true
      index >= 0
    finally
      if removed then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif


  member this.RemoveFirst([<Out>]result: byref<KVP<'K,'V>>):bool =
    let mutable removed = false
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.size > 0 then
        result <- KeyValuePair(this.keys.[0], this.values.[0])
        this.RemoveAt(0)
        increment &this.orderVersion
        removed <- true
        true
      else false
    finally
      if removed then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif


  member this.RemoveLast([<Out>]result: byref<KeyValuePair<'K, 'V>>):bool =
    let mutable removed = false
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.size > 0 then
        result <-
          if couldHaveRegularKeys && this.size > 1 then
            Trace.Assert(comparer.Compare(rkLast, diffCalc.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep())) = 0)
            KeyValuePair(rkLast, this.values.[this.size - 1])
          else KeyValuePair(this.keys.[this.size - 1], this.values.[this.size - 1])
        this.RemoveAt(this.size - 1)
        increment &this.orderVersion
        removed <- true
        true
      else false
    finally
      if removed then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif

  /// Removes all elements that are to `direction` from `key`
  member this.RemoveMany(key:'K,direction:Lookup) : bool =
    this.CheckNull(key)
    let mutable removed = false
    let mutable entered = false
    try
      try ()
      finally
        entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if entered then Interlocked.Increment(&this.nextVersion) |> ignore
      if this.isReadOnly then invalidOp "SortedMap is read-only"
      if this.size = 0 then false
      else
        let mutable kvp = Unchecked.defaultof<_>
        let pivotIndex = this.TryFindWithIndex(key, direction, &kvp)
        // pivot should be removed, after calling TFWI pivot is always inclusive
        match direction with
        | Lookup.EQ -> 
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then 
            this.RemoveAt(index)
            increment &this.orderVersion
            removed <- true
            true
          else false
        | Lookup.LT | Lookup.LE ->
          if pivotIndex = -1 then // pivot is not here but to the left, keep all elements
            false
          elif pivotIndex >=0 then // remove elements below pivot and pivot
            this.size <- this.size - (pivotIndex + 1)
            
            if couldHaveRegularKeys then
              this.keys.[0] <- (diffCalc.Add(this.keys.[0], int64 (pivotIndex+1)))
              if this.size > 1 then 
                this.keys.[1] <- (diffCalc.Add(this.keys.[0], this.rkGetStep())) 
              else
                this.keys.[1] <- Unchecked.defaultof<'K>
                rkStep_ <- 0L
            else
              Array.Copy(this.keys, pivotIndex + 1, this.keys, 0, this.size) // move this.values to 
              Array.fill this.keys this.size (this.values.Length - this.size) Unchecked.defaultof<'K>

            Array.Copy(this.values, pivotIndex + 1, this.values, 0, this.size)
            Array.fill this.values this.size (this.values.Length - this.size) Unchecked.defaultof<'V>
            
            increment &this.orderVersion
            removed <- true
            true
          else
            raise (ApplicationException("wrong result of TryFindWithIndex with LT/LE direction"))
        | Lookup.GT | Lookup.GE ->
          if pivotIndex = -2 then // pivot is not here but to the right, keep all elements
            false
          elif pivotIndex >= 0 then // remove elements above and including pivot
            this.size <- pivotIndex
            if couldHaveRegularKeys then
              if this.size > 1 then
                rkLast <- diffCalc.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep()) // -1 is correct, the size is updated on the previous line
              else
                this.keys.[1] <- Unchecked.defaultof<'K>
                rkStep_ <- 0L
                if this.size = 1 then rkLast <- this.keys.[0] 
                else rkLast <- Unchecked.defaultof<_>
            if not couldHaveRegularKeys then
              Array.fill this.keys pivotIndex (this.values.Length - pivotIndex) Unchecked.defaultof<'K>
            Array.fill this.values pivotIndex (this.values.Length - pivotIndex) Unchecked.defaultof<'V>
            this.SetCapacity(this.size)

            increment &this.orderVersion
            removed <- true
            true
          else
            raise (ApplicationException("wrong result of TryFindWithIndex with GT/GE direction"))
        | _ -> failwith "wrong direction"
    finally
      this.NotifyUpdate(true)
      if removed then Interlocked.Increment(&this.version) |> ignore elif entered then Interlocked.Decrement(&this.nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this.version <> this.nextVersion then raise (ApplicationException("this.version <> this.nextVersion"))
      #else
      if entered && this.version <> this.nextVersion then Environment.FailFast("this.version <> this.nextVersion")
      #endif
    
  /// Returns the index of found KeyValuePair or a negative value:
  /// -1 if the non-found key is smaller than the first key
  /// -2 if the non-found key is larger than the last key
  /// -3 if the non-found key is within the key range (for EQ direction only)
  /// -4 empty
  /// Example: (-1) [...current...(-3)...map ...] (-2)
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member internal this.TryFindWithIndex(key:'K, direction:Lookup, [<Out>]result: byref<KeyValuePair<'K, 'V>>) : int =
    if this.size = 0 then -4
    else
      match direction with
      | Lookup.EQ ->
        let lastIdx = this.size-1
        if this.size > 0 && this.CompareToLast(key) = 0 then // key = last key
          result <- 
            if couldHaveRegularKeys && this.size > 1 then
              #if DEBUG
              Trace.Assert(comparer.Compare(rkLast, diffCalc.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep())) = 0)
              #endif
              KeyValuePair(rkLast, this.values.[this.size - 1])
            else KeyValuePair(this.keys.[this.size - 1], this.values.[this.size - 1])
          lastIdx
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then
            result <-  this.GetPairByIndexUnchecked(index)
            index
          else
            let index2 = ~~~index
            if index2 >= this.Count then // there are no elements larger than key, all this.keys are smaller
              -2 // the key could be in the next bucket
            elif index2 = 0 then //it is the index of the first element that is larger than value
              -1 // all this.keys in the map are larger than the desired key
            else
              -3
      | Lookup.LT ->
        let lastIdx = this.size-1
        let lc = if this.size > 0 then this.CompareToLast(key) else -2
        if lc = 0 then // key = last key
          if this.size > 1 then
            result <- this.GetPairByIndexUnchecked(lastIdx - 1) // return item beforelast
            lastIdx - 1
          else -1
        elif lc > 0 then // key greater than the last
          result <- this.GetPairByIndexUnchecked(lastIdx) // return the last item 
          lastIdx
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index > 0 then
            result <- this.GetPairByIndexUnchecked(index - 1)
            index - 1
          elif index = 0 then
              -1 // 
          else
            let index2 = ~~~index
            if index2 >= this.Count then // there are no elements larger than key
              result <- this.GetPairByIndexUnchecked(this.Count - 1) // last element is the one that LT key
              this.Count - 1
            elif index2 = 0 then
              -1
            else //  it is the index of the first element that is larger than value
              result <- this.GetPairByIndexUnchecked(index2 - 1)
              index2 - 1
      | Lookup.LE ->
        let lastIdx = this.size-1
        let lc = if this.size > 0 then this.CompareToLast(key) else -2
        if lc >= 0 then // key = last key or greater than the last key
          result <- this.GetPairByIndexUnchecked(lastIdx)
          lastIdx
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then
            result <- this.GetPairByIndexUnchecked(index) // equal
            index
          else
            let index2 = ~~~index
            if index2 >= this.size then // there are no elements larger than key
              result <- this.GetPairByIndexUnchecked(this.size - 1)
              this.size - 1
            elif index2 = 0 then
              -1
            else //  it is the index of the first element that is larger than value
              result <- this.GetPairByIndexUnchecked(index2 - 1)
              index2 - 1
      | Lookup.GT ->
        let lc = if this.size > 0 then comparer.Compare(key, this.keys.[0]) else 2
        if lc = 0 then // key = first key
          if this.size > 1 then
            result <- this.GetPairByIndexUnchecked(1) // return item after first
            1
          else -2 // cannot get greater than a single value when k equals to it
        elif lc < 0 then
          result <- this.GetPairByIndexUnchecked(0) // return first
          0
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 && index < this.Count - 1 then
            result <- this.GetPairByIndexUnchecked(index + 1)
            index + 1
          elif index >= this.Count - 1 then
            -2
          else
            let index2 = ~~~index
            if index2 >= this.Count then // there are no elements larger than key
              -2
            else //  it is the index of the first element that is larger than value
              result <- this.GetPairByIndexUnchecked(index2)
              index2
      | Lookup.GE ->
        let lc = if this.size > 0 then comparer.Compare(key, this.keys.[0]) else 2
        if lc <= 0 then // key = first key or smaller than the first key
          result <- this.GetPairByIndexUnchecked(0)
          0
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 && index < this.Count then
            result <- this.GetPairByIndexUnchecked(index) // equal
            index
          else
            let index2 = ~~~index
            if index2 >= this.Count then // there are no elements larger than key
              -2
            else //  it is the index of the first element that is larger than value
              result <- this.GetPairByIndexUnchecked(index2)
              index2
      | _ -> raise (ApplicationException("Wrong lookup direction"))

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  override this.TryFind(key:'K, direction:Lookup, [<Out>] result: byref<KeyValuePair<'K, 'V>>) =
    this.CheckNull(key)
    let res() = 
      let mutable kvp = Unchecked.defaultof<_>
      let idx = this.TryFindWithIndex(key, direction, &kvp)
      if idx >= 0 then ValueTuple<_,_>(true, kvp)
      else ValueTuple<_,_>(false, kvp)
    let tupleResult = readLockIf &this.nextVersion &this.version this.isSynchronized res
    result <- tupleResult.Item2
    tupleResult.Item1

  /// Return true if found exact key
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member this.TryGetValue(key, [<Out>]value: byref<'V>) : bool =
    this.CheckNull(key)
    let res() = 
      // first/last optimization
      if this.size = 0 then
        ValueTuple<_,_>(false, Unchecked.defaultof<'V>)
      else
        let lc = this.CompareToLast key
        if lc = 0 then // key = last key
          ValueTuple<_,_>(true, this.values.[this.size-1])
        else
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then
            ValueTuple<_,_>(true, this.values.[index])
          else
            ValueTuple<_,_>(false, Unchecked.defaultof<'V>)
    let tupleResult = readLockIf &this.nextVersion &this.version this.isSynchronized res
    value <- tupleResult.Item2
    tupleResult.Item1

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  override this.TryGetFirst([<Out>] res: byref<KeyValuePair<'K, 'V>>) = 
    try
      res <- this.First
      true
    with
    | _ -> 
      res <- Unchecked.defaultof<KeyValuePair<'K, 'V>>
      false
           
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>] 
  override this.TryGetLast([<Out>] res: byref<KeyValuePair<'K, 'V>>) = 
    try
      res <- this.Last
      true
    with
    | _ -> 
      res <- Unchecked.defaultof<KeyValuePair<'K, 'V>>
      false

  override this.GetCursor() =
    if Thread.CurrentThread.ManagedThreadId <> ownerThreadId then
      // NB: via property with locks
      this.IsSynchronized <- true 
    let mutable entered = false
    try
      entered <- enterWriteLockIf &this.Locker this.isSynchronized
      // if source is already read-only, MNA will always return false
      if this.isReadOnly then new SortedMapCursor<'K,'V>(this) :> ICursor<'K,'V>
      else 
        let c = new BaseCursorAsync<'K,'V,_>(Func<_>(this.GetEnumerator))
        //let c = new SortedMapCursor<'K,'V>(this)
        c :> ICursor<'K,'V>
    finally
      exitWriteLockIf &this.Locker entered

  override this.GetContainerCursor() = this.GetEnumerator()

  // .NETs foreach optimization must return struct
  member this.GetEnumerator() : SortedMapCursor<_,_> =
    if Thread.CurrentThread.ManagedThreadId <> ownerThreadId then 
      // NB: via property with locks
      this.IsSynchronized <- true
    readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
      new SortedMapCursor<'K,'V>(this)
    )

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  member internal this.GetSMCursor() =
    if Thread.CurrentThread.ManagedThreadId <> ownerThreadId then
      // NB: via property with locks
      this.IsSynchronized <- true
    readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
      new SortedMapCursor<'K,'V>(this)
    )

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
  override this.GetAt(idx:int) =
    readLockIf &this.nextVersion &this.version this.isSynchronized (fun _ ->
      if idx >= 0 && idx < this.size then this.values.[idx] else raise (ArgumentOutOfRangeException("idx", "Idx is out of range in SortedMap GetAt method."))
    )


  /// Make the capacity equal to the size
  member this.TrimExcess() = this.Capacity <- this.size

  member private this.Dispose(disposing:bool) =
    if BufferPool.IsPreservedBuffer<'V>() then BufferPool.DisposePreservedBuffers<'V>(this.values, 0, this.size)

    if not couldHaveRegularKeys then BufferPool<_>.Return(this.keys, true) |> ignore
    BufferPool<_>.Return(this.values, true) |> ignore
    if disposing then GC.SuppressFinalize(this)
  
  member this.Dispose() = this.Dispose(true)
  override this.Finalize() = this.Dispose(false)

  //#endregion


  //#region Interfaces

  interface IArrayBasedMap<'K,'V> with
    member this.Length with get() = this.size
    member this.Version with get() = this.version
    member this.IsRegular with get() = this.IsRegular
    member this.IsReadOnly with get() = this.IsReadOnly
    member this.Keys with get() = this.keys
    member this.Values with get() = this.values

  interface IDisposable with
    member this.Dispose() = this.Dispose(true)

  interface IEnumerable with
    member this.GetEnumerator() = this.GetEnumerator() :> IEnumerator

  interface IEnumerable<KeyValuePair<'K,'V>> with
    member this.GetEnumerator() : IEnumerator<KeyValuePair<'K,'V>> = 
      this.GetEnumerator() :> IEnumerator<KeyValuePair<'K,'V>>

  interface ICollection  with
    member this.SyncRoot = this.SyncRoot
    member this.CopyTo(array, arrayIndex) =
      let mutable entered = false
      try
        try ()
        finally
          entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if array = null then raise (ArgumentNullException("array"))
        if arrayIndex < 0 || arrayIndex > array.Length then raise (ArgumentOutOfRangeException("arrayIndex"))
        if array.Length - arrayIndex < this.Count then raise (ArgumentException("ArrayPlusOffTooSmall"))
        for index in 0..this.size do
          let kvp = KeyValuePair(this.GetKeyByIndexUnchecked(index), this.values.[index])
          array.SetValue(kvp, arrayIndex + index)
      finally
        exitWriteLockIf &this.Locker entered
    member this.Count = this.Count
    member this.IsSynchronized with get() =  this.isSynchronized

  interface IDictionary<'K,'V> with
    member this.Count = this.Count
    member this.IsReadOnly with get() = this.IsReadOnly
    member this.Item
      with get key = this.Item(key)
      and set key value = this.[key] <- value
    member this.Keys with get() = this.Keys :?> ICollection<'K>
    member this.Values with get() = this.Values :?> ICollection<'V>
    member this.Clear() = this.Clear()
    member this.ContainsKey(key) = this.ContainsKey(key)
    member this.Contains(kvp:KeyValuePair<'K,'V>) = this.ContainsKey(kvp.Key)
    member this.CopyTo(array, arrayIndex) =
      let mutable entered = false
      try
        try ()
        finally
          entered <- enterWriteLockIf &this.Locker this.isSynchronized
        if array = null then raise (ArgumentNullException("array"))
        if arrayIndex < 0 || arrayIndex > array.Length then raise (ArgumentOutOfRangeException("arrayIndex"))
        if array.Length - arrayIndex < this.Count then raise (ArgumentException("ArrayPlusOffTooSmall"))
        for index in 0..this.Count do
          let kvp = KeyValuePair(this.keys.[index], this.values.[index])
          array.[arrayIndex + index] <- kvp
      finally
        exitWriteLockIf &this.Locker entered
    member this.Add(key, value) = this.Add(key, value)
    member this.Add(kvp:KeyValuePair<'K,'V>) = this.Add(kvp.Key, kvp.Value)
    member this.Remove(key) = this.Remove(key)
    member this.Remove(kvp:KeyValuePair<'K,'V>) = this.Remove(kvp.Key)
    member this.TryGetValue(key, [<Out>]value: byref<'V>) : bool = this.TryGetValue(key, &value)


  interface IReadOnlySeries<'K,'V> with
    // the rest is in BaseSeries
    member this.Item with get k = this.Item(k)
    
  interface IMutableSeries<'K,'V> with
    member this.Complete() = this.Complete()
    member this.Version with get() = this.Version
    member this.Count with get() = int64(this.size)
    member this.Item with get k = this.Item(k) and set (k:'K) (v:'V) = this.[k] <- v
    member this.Add(k, v) = this.Add(k,v)
    member this.AddLast(k, v) = this.AddLast(k, v)
    member this.AddFirst(k, v) = this.AddFirst(k, v)
    member this.Remove(k) = this.Remove(k)
    member this.RemoveFirst([<Out>] result: byref<KeyValuePair<'K, 'V>>) = this.RemoveFirst(&result)
    member this.RemoveLast([<Out>] result: byref<KeyValuePair<'K, 'V>>) = this.RemoveLast(&result)
    member this.RemoveMany(key:'K,direction:Lookup) = this.RemoveMany(key, direction)

    // TODO move to type memeber, cheack if IReadOnlySeries is SM and copy arrays in one go
    // TODO atomic append with single version increase, now it is a sequence of remove/add mutations
    member this.Append(appendMap:IReadOnlySeries<'K,'V>, option:AppendOption) =
      let hasEqOverlap (old:IReadOnlySeries<'K,'V>) (append:IReadOnlySeries<'K,'V>) : bool =
        if comparer.Compare(append.First.Key, old.Last.Key) > 0 then false
        else
          let oldC = old.GetCursor()
          let appC = append.GetCursor();
          let mutable cont = true
          let mutable overlapOk = 
            oldC.MoveAt(append.First.Key, Lookup.EQ) 
              && appC.MoveFirst() 
              && comparer.Compare(oldC.CurrentKey, appC.CurrentKey) = 0
              && Unchecked.equals oldC.CurrentValue appC.CurrentValue
          while overlapOk && cont do
            if oldC.MoveNext() then
              overlapOk <-
                appC.MoveNext() 
                && comparer.Compare(oldC.CurrentKey, appC.CurrentKey) = 0
                && Unchecked.equals oldC.CurrentValue appC.CurrentValue
            else cont <- false
          overlapOk
      if appendMap.IsEmpty then
        0
      else
        let mutable entered = false
        try
          entered <- enterWriteLockIf &this.Locker this.isSynchronized
          match option with
          | AppendOption.ThrowOnOverlap ->
            if this.IsEmpty || comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
              let mutable c = 0
              for i in appendMap do
                c <- c + 1
                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
              c
            else invalidOp "values overlap with existing"
          | AppendOption.DropOldOverlap ->
            if this.IsEmpty || comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
              let mutable c = 0
              for i in appendMap do
                c <- c + 1
                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
              c
            else
              let removed = this.RemoveMany(appendMap.First.Key, Lookup.GE)
              Trace.Assert(removed)
              let mutable c = 0
              for i in appendMap do
                c <- c + 1
                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
              c
          | AppendOption.IgnoreEqualOverlap ->
            if this.IsEmpty || comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
              let mutable c = 0
              for i in appendMap do
                c <- c + 1
                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
              c
            else
              let isEqOverlap = hasEqOverlap this appendMap
              if isEqOverlap then
                let appC = appendMap.GetCursor();
                if appC.MoveAt(this.LastUnchecked.Key, Lookup.GT) then
                  this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
                  let mutable c = 1
                  while appC.MoveNext() do
                    this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
                    c <- c + 1
                  c
                else 0
              else invalidOp "overlapping values are not equal" // TODO unit test
          | AppendOption.RequireEqualOverlap ->
            if this.IsEmpty then
              let mutable c = 0
              for i in appendMap do
                c <- c + 1
                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
              c
            elif comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
              invalidOp "values do not overlap with existing"
            else
              let isEqOverlap = hasEqOverlap this appendMap
              if isEqOverlap then
                let appC = appendMap.GetCursor();
                if appC.MoveAt(this.LastUnchecked.Key, Lookup.GT) then
                  this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
                  let mutable c = 1
                  while appC.MoveNext() do
                    this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
                    c <- c + 1
                  c
                else 0
              else invalidOp "overlapping values are not equal" // TODO unit test
          | _ -> failwith "Unknown AppendOption"
        finally
          exitWriteLockIf &this.Locker entered

  //#endregion

  //#region Constructors


  new() = new SortedMap<_,_>(None, None, None)
  new(dictionary:IDictionary<'K,'V>) = new SortedMap<_,_>(Some(dictionary), Some(dictionary.Count), None)
  new(minimumCapacity:int) = new SortedMap<_,_>(None, Some(minimumCapacity), None)

  // do not expose ctors with comparer to public
  internal new(comparer:IComparer<'K>) = new SortedMap<_,_>(None, None, Some(KeyComparer<'K>.Create(comparer)))
  internal new(dictionary:IDictionary<'K,'V>,comparer:IComparer<'K>) = new SortedMap<_,_>(Some(dictionary), Some(dictionary.Count), Some(KeyComparer<'K>.Create(comparer)))
  internal new(minimumCapacity:int,comparer:IComparer<'K>) = new SortedMap<_,_>(None, Some(minimumCapacity), Some(KeyComparer<'K>.Create(comparer)))

  internal new(comparer:KeyComparer<'K>) = new SortedMap<_,_>(None, None, Some(comparer))
  internal new(dictionary:IDictionary<'K,'V>,comparer:KeyComparer<'K>) = new SortedMap<_,_>(Some(dictionary), Some(dictionary.Count), Some(comparer))
  internal new(minimumCapacity:int,comparer:KeyComparer<'K>) = new SortedMap<_,_>(None, Some(minimumCapacity), Some(comparer))

  static member internal OfSortedKeysAndValues(keys:'K[], values:'V[], size:int, comparer:IComparer<'K>, doCheckSorted:bool, isAlreadyRegular) =
    if keys.Length < size && not isAlreadyRegular then raise (new ArgumentException("Keys array is smaller than provided size"))
    if values.Length < size then raise (new ArgumentException("Values array is smaller than provided size"))
    let sm = new SortedMap<'K,'V>(comparer)
    if doCheckSorted then
      for i in 1..size-1 do
        if comparer.Compare(keys.[i-1], keys.[i]) >= 0 then raise (new ArgumentException("Keys are not sorted"))

    // at this point IsRegular means could be regular
    if sm.IsRegular && not isAlreadyRegular then
      let isReg, step, firstArr = sm.rkCheckArray keys size sm.Comparer
      if isReg then
        sm.keys <- firstArr
      else 
        sm.IsRegular <- false
        sm.keys <- keys
    elif sm.IsRegular && isAlreadyRegular then
      Trace.Assert(keys.Length >= 2)
      sm.keys <- keys
    elif not sm.IsRegular && not isAlreadyRegular then
      sm.IsRegular <- false
      sm.keys <- keys
    else raise (InvalidOperationException("Keys are marked as already regular, but comparer doesn't cupport regular keys"))
    
    sm.size <- size
    sm.values <- values
    if sm.size > 0 then sm.SetRkLast(sm.GetKeyByIndexUnchecked(sm.size - 1))
    sm

  /// Create a SortedMap using the first `size` elements of the provided keys and values.
  static member OfSortedKeysAndValues(keys:'K[], values:'V[], size:int, comparer:IComparer<'K>) =
    if comparer = Unchecked.defaultof<_> then raise (ArgumentNullException("comparer"))
    else SortedMap.OfSortedKeysAndValues(keys, values, size, comparer, true, false)

  /// Create a SortedMap using the first `size` elements of the provided keys and values, with default comparer.
  static member OfSortedKeysAndValues(keys:'K[], values:'V[], size:int) =
    let comparer =
      KeyComparer<'K>.Default
    SortedMap.OfSortedKeysAndValues(keys, values, size, comparer, true, false)

  static member OfSortedKeysAndValues(keys:'K[], values:'V[]) =
    if keys.Length <> values.Length then raise (new ArgumentException("Keys and values arrays are of different sizes"))
    SortedMap.OfSortedKeysAndValues(keys, values, values.Length)

  //#endregion


  /// Checks if keys of two maps are equal
  static member internal KeysAreEqual(smA:SortedMap<'K,_>,smB:SortedMap<'K,_>) : bool =
    if not (smA.size = smB.size) then false
    elif smA.IsRegular && smB.IsRegular 
      && smA.RegularStep = smB.RegularStep
      && smA.Comparer.Equals(smB.Comparer) // TODO test custom comparer equality, custom comparers must implement equality
      && smA.Comparer.Compare(smA.keys.[0], smB.keys.[0]) = 0 then // if steps are equal we could skip checking second elements in keys
      true
    else
      // this is very slow to be used in any "optimization", should use BytesExtensions.UnsafeCompare
      System.Linq.Enumerable.SequenceEqual(smA.keys, smB.keys)

  static member Empty = empty.Value


and
  public SortedMapCursor<'K,'V> =
    struct
      val mutable internal source : SortedMap<'K,'V>
      val mutable internal index : int
      val mutable internal currentKey : 'K
      val mutable internal currentValue: 'V
      val mutable internal cursorVersion : int64
      val mutable internal isBatch : bool
      new(source:SortedMap<'K,'V>) = 
        { source = source;
          index = -1;
          currentKey = Unchecked.defaultof<_>;
          currentValue = Unchecked.defaultof<_>;
          cursorVersion = source.orderVersion;
          isBatch = false;
        }
    end

    member this.CurrentKey with get() = this.currentKey
    member this.CurrentValue with get() = this.currentValue
    member this.Current with get() : KVP<'K,'V> = KeyValuePair(this.currentKey, this.currentValue)
    member this.Source: ISeries<'K,'V> = this.source :> ISeries<'K,'V>      
    member this.IsContinuous with get() = false
    member this.Comparer: KeyComparer<'K> = this.source.Comparer
    member this.TryGetValue(key: 'K, value: byref<'V>): bool = this.source.TryGetValue(key, &value)
    
    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    member this.MoveNext() =
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue

      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.cursorVersion = this.source.orderVersion then
            newIndex <- this.index + 1
            if newIndex < this.source.size then
              newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
              newValue <- this.source.values.[newIndex]
              true
            else
              false
          else // source order change
            // NB: we no longer recover on order change, some cursor require special logic to recover
            ThrowHelper.ThrowOutOfOrderKeyException(this.currentKey, "SortedMap order was changed since last move. Catch OutOfOrderKeyException and use its CurrentKey property together with MoveAt(key, Lookup.GT) to recover.")
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then       
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
      result


    member this.CurrentBatch: IReadOnlySeries<'K,'V> = 
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////

          if this.isBatch then
            Trace.Assert(this.index = this.source.size - 1)
            Trace.Assert(this.source.isReadOnly)
            this.source :> IReadOnlySeries<'K,'V>
          else raise (InvalidOperationException("SortedMap cursor is not at a batch position"))

        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      result

    member this.MoveNextBatch(cancellationToken: CancellationToken): Task<bool> =
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue
      let mutable newIsBatch = this.isBatch

      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////

          if (this.source.isReadOnly) && (this.index = -1) && this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- this.source.size - 1 // at the last element of the batch
            newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            newValue <- this.source.values.[newIndex]
            newIsBatch <- true
            trueTask
          else falseTask

        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result.Result then
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
        this.isBatch <- newIsBatch
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    member this.MovePrevious() = 
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue

      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.index = -1 then
            if this.source.size > 0 then
              this.cursorVersion <- this.source.orderVersion
              newIndex <- this.source.size - 1
              newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
              newValue <- this.source.values.[newIndex]
              true
            else
              false
          elif this.cursorVersion = this.source.orderVersion then
            if this.index > 0 && this.index < this.source.size then
              newIndex <- this.index - 1
              newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
              newValue <- this.source.values.[newIndex]
              true
            else
              false
          else
            ThrowHelper.ThrowOutOfOrderKeyException(this.currentKey, "SortedMap order was changed since last move. Catch OutOfOrderKeyException and use its CurrentKey property together with MoveAt(key, Lookup.LT) to recover.")
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    member this.MoveAt(key:'K, lookup:Lookup) =
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          let mutable kvp = Unchecked.defaultof<_>
          let position = this.source.TryFindWithIndex(key, lookup, &kvp)
          if position >= 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- position
            newKey <- kvp.Key
            newValue <- kvp.Value
            true
          else
            false
      /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    member this.MoveFirst() =
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- 0
            newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            newValue <- this.source.values.[newIndex]
            true
          else
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    member this.MoveLast() =
      let mutable newIndex = this.index
      let mutable newKey = this.currentKey
      let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source.isSynchronized
        let version = if doSpin then Volatile.Read(&this.source.version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- this.source.size - 1
            newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            newValue <- this.source.values.[newIndex]
            true
          else
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source.nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.currentKey <- newKey
        this.currentValue <- newValue
      result

    //[<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
    //member this.MoveNext(cancellationToken:CancellationToken): Task<bool> =
    //  if this.MoveNext() then trueTask
    //  elif this.source.IsReadOnly then
    //    if this.MoveNext() then trueTask else falseTask
    //  else
    //    if this.asyncCursor = Unchecked.defaultof<_> then this.asyncCursor <- new AsyncCursor<_,_,_>(this.source, this)
    //    this.asyncCursor.MoveNext(cancellationToken)


    member this.Clone() = 
      let mutable copy = this
      copy

    member this.Reset() = 
      this.cursorVersion <- this.source.orderVersion
      this.currentKey <- Unchecked.defaultof<'K>
      this.currentValue <- Unchecked.defaultof<'V>
      this.index <- -1

    member this.Dispose() = this.Reset()

    interface IDisposable with
      member this.Dispose() = this.Dispose()

    interface IEnumerator<KVP<'K,'V>> with    
      member this.Reset() = this.Reset()
      member this.MoveNext():bool = this.MoveNext()
      member this.Current with get(): KVP<'K, 'V> = this.Current
      member this.Current with get(): obj = this.Current :> obj

    interface IAsyncEnumerator<KVP<'K,'V>> with
      member this.MoveNext(cancellationToken:CancellationToken): Task<bool> =
        if this.source.IsReadOnly then
          if this.MoveNext() then trueTask else falseTask
        else raise (NotSupportedException("Use BaseCursorAsync instead"))
        //this.MoveNext(cancellationToken:CancellationToken)

    interface ICursor<'K,'V> with
      member this.Comparer with get() = this.source.Comparer
      member this.CurrentBatch = this.CurrentBatch
      member this.MoveNextBatch(cancellationToken: CancellationToken): Task<bool> = this.MoveNextBatch(cancellationToken)
      member this.MoveAt(index:'K, lookup:Lookup) = this.MoveAt(index, lookup)
      member this.MoveFirst():bool = this.MoveFirst()
      member this.MoveLast():bool =  this.MoveLast()
      member this.MovePrevious():bool = this.MovePrevious()
      member this.CurrentKey with get():'K = this.CurrentKey
      member this.CurrentValue with get():'V = this.CurrentValue
      member this.Source with get() = this.source :> IReadOnlySeries<'K,'V>
      member this.Clone() = this.Clone() :> ICursor<'K,'V>
      member this.IsContinuous with get() = false
      member this.TryGetValue(key, [<Out>]value: byref<'V>) : bool = this.source.TryGetValue(key, &value)

    interface ISpecializedCursor<'K,'V, SortedMapCursor<'K,'V>> with
      member this.Initialize() = 
        let c = this.Clone()
        c.Reset()
        c
      member this.Clone() = this.Clone()

type internal ChunksContainer<'K,'V>
  (comparer : IComparer<'K>, synced: bool)  =
  let t = new SortedMap<_,_>(comparer, IsSynchronized = synced)
  interface IReadOnlySeries<'K,SortedMap<'K,'V>> with
    member x.Updated = t.Updated
    member x.Comparer = t.Comparer
    member x.First = t.First
    member x.GetAt(idx) = t.GetAt(idx)
    member x.GetCursor() = t.GetCursor()
    member x.GetEnumerator(): IAsyncEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>> = 
      t.GetEnumerator() :> IAsyncEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>>
    member x.GetEnumerator(): IEnumerator = 
      t.GetEnumerator() :> IEnumerator
    member x.GetEnumerator(): IEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>> = 
      t.GetEnumerator() :> IEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>>
    member x.IsEmpty = t.IsEmpty
    member x.IsIndexed = false
    member x.IsReadOnly = t.IsReadOnly
    member x.Item
      with get (key) = t.Item(key)
    member x.Keys = t.Keys
    member x.Last = t.Last 
    member x.Subscribe(observer) = t.Subscribe(observer)
    member x.TryFind(key, direction, value) = t.TryFind(key, direction, &value)
    member x.TryGetFirst(value) = t.TryGetFirst(&value)
    member x.TryGetLast(value) = t.TryGetLast(&value)
    member x.Values = t.Values 
    
    
  interface IMutableChunksSeries<'K,'V,SortedMap<'K,'V>> with
    member x.Dispose() = ()
    member x.Flush() = ()
    member x.Id = ""
    
    member x.Item
      with get (key) = t.Item(key) 
      and set (key) v =
        if v.size > 0 then 
          let ov = t.orderVersion
          t.[key] <- v
          t.orderVersion <- ov
        else t.Remove(key) |> ignore
        t.version <- v.version
    member x.RemoveMany(key, keyChunk, direction) =
      match direction with
      | Lookup.EQ -> (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).[key] = keyChunk
      | Lookup.LT | Lookup.LE -> 
        let res = t.RemoveMany(key, Lookup.LT)
        (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).[key] <- keyChunk
        res
      | Lookup.GT | Lookup.GE -> 
        let res = t.RemoveMany(key, Lookup.GT)
        (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).[key] <- keyChunk
        res
      | _ -> failwith "mute F# warning"
    member x.Version = t.version // small v, field
   