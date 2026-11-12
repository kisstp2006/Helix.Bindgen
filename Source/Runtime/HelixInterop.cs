// What the C# code Helix.Bindgen writes uses at the boundary with native modules: the values handed across, the
// errors of a call, the native ids of the types, wrappers of native objects and the handlers of native events. It is
// compiled into the assembly that holds the generated code (the engine's, or a test's).
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Helix.Interop
{
    /// <summary>A string handed out by native code: UTF-8 bytes valid until the thread's next such call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct InteropString
    {
        public byte* data;
        public int size;
    }

    /// <summary>A string handed to native code: UTF-16 code units valid until the thread's next such call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct InteropString16
    {
        public char* data;
        public int size;
    }

    /// <summary>An array handed out by native code: the items' bytes valid until the thread's next such call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct InteropArray
    {
        public void* data;
        public int count;
    }

    /// <summary>A native object: its id (0 is null) and its dynamic type's native id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InteropObject
    {
        public ulong id;
        public uint type;
        public uint reserved;
    }

    /// <summary>The id a wrapper is made with.</summary>
    public readonly struct ObjectId
    {
        public readonly ulong Value;
        public ObjectId(ulong value) => Value = value;
    }

    public static unsafe class Native
    {
        public const int MissingObject = 1;
        public const int NullArgument = 2;

        /// <summary>The exception of a failed call (the engine replaces it with its own exception types).</summary>
        public static Func<int, string, Exception> MakeException = (status, what) => status == NullArgument
            ? new ArgumentNullException(what)
            : new NullReferenceException($"{what}: the object is missing (destroyed or null)");

        /// <summary>Where exceptions thrown in handlers that native code called end (they never cross into it).</summary>
        public static Action<Exception> ReportException = e => Console.Error.WriteLine(e);

        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fail(int status, string what) => throw MakeException(status, what);

        public static string ReadString(InteropString s) => ReadString(s.data, s.size);

        public static string ReadString(byte* data, int size) => size <= 0 ? "" : Encoding.UTF8.GetString(data, size);

        public static T[] ReadArray<T>(InteropArray a) where T : unmanaged => ReadArray((T*)a.data, a.count);

        // The buffers values handed to native code stay in until the thread's next call that hands one out.
        [ThreadStatic] static void* t_StringBuffer;
        [ThreadStatic] static int t_StringCapacity;
        [ThreadStatic] static void* t_ArrayBuffer;
        [ThreadStatic] static int t_ArrayCapacity;

        static void* Buffer(ref void* buffer, ref int capacity, int bytes)
        {
            if (bytes > capacity)
            {
                int size = Math.Max(bytes, Math.Max(64, capacity * 2));
                buffer = NativeMemory.Realloc(buffer, (nuint)size);
                capacity = size;
            }
            return buffer;
        }

        public static void PassString16(string text, InteropString16* result)
        {
            int n = text == null ? 0 : text.Length;
            char* data = (char*)Buffer(ref t_StringBuffer, ref t_StringCapacity, Math.Max(2, n * 2));
            if (n > 0)
                text.AsSpan().CopyTo(new Span<char>(data, n));
            result->data = data;
            result->size = n;
        }

        public static void PassArray<T>(T[] items, InteropArray* result) where T : unmanaged
        {
            int n = items == null ? 0 : items.Length;
            void* data = Buffer(ref t_ArrayBuffer, ref t_ArrayCapacity, Math.Max(1, n * sizeof(T)));
            if (n > 0)
                items.AsSpan().CopyTo(new Span<T>(data, n));
            result->data = data;
            result->count = n;
        }

        public static T[] ReadArray<T>(T* data, int count) where T : unmanaged
        {
            if (count <= 0)
                return Array.Empty<T>();
            var result = new T[count];
            new ReadOnlySpan<T>(data, count).CopyTo(result);
            return result;
        }
    }

    /// <summary>C# types and the native ids of the same types (filled by each module's generated loader).</summary>
    public static class Types
    {
        static readonly Dictionary<Type, uint> s_ToNative = new();
        static readonly Dictionary<uint, Type> s_FromNative = new();

        public static void Register(Type type, uint nativeId)
        {
            lock (s_ToNative)
            {
                s_ToNative[type] = nativeId;
                s_FromNative[nativeId] = type;
            }
        }

        public static uint Of(Type type)
        {
            if (type == null)
                return 0;
            lock (s_ToNative)
                return s_ToNative.TryGetValue(type, out uint id) ? id : throw new ArgumentException($"{type} is not a type of a loaded module");
        }

        public static uint Of<T>() => Of(typeof(T));

        public static Type FromNative(uint nativeId)
        {
            lock (s_ToNative)
                return s_FromNative.TryGetValue(nativeId, out Type type) ? type : null;
        }
    }

    /// <summary>The wrappers of native objects: one per id while it is alive, made by the factory of its dynamic type.</summary>
    public static class Objects
    {
        static readonly Dictionary<uint, Func<ObjectId, object>> s_ByNative = new();
        static readonly Dictionary<Type, Func<ObjectId, object>> s_ByType = new();
        static readonly Dictionary<ulong, WeakReference<object>> s_Wrappers = new();
        static readonly ConditionalWeakTable<object, StrongBox<ulong>> s_Ids = new();

        public static void RegisterFactory(uint nativeType, Type type, Func<ObjectId, object> factory)
        {
            lock (s_Wrappers)
            {
                s_ByNative[nativeType] = factory;
                s_ByType[type] = factory;
            }
        }

        /// <summary>A wrapper made by its own constructor (a native object it created).</summary>
        public static void Adopt(object wrapper, ulong id)
        {
            if (id == 0)
                return;
            lock (s_Wrappers)
            {
                s_Wrappers[id] = new WeakReference<object>(wrapper);
                s_Ids.AddOrUpdate(wrapper, new StrongBox<ulong>(id));
            }
        }

        /// <summary>The native id of a wrapper (0 for null and for objects that wrap nothing).</summary>
        public static ulong IdOf(object wrapper)
        {
            if (wrapper == null)
                return 0;
            lock (s_Wrappers)
                return s_Ids.TryGetValue(wrapper, out StrongBox<ulong> id) ? id.Value : 0;
        }

        /// <summary>A wrapper as native code takes it: its id and its type's native id.</summary>
        public static InteropObject Pass(object wrapper)
        {
            ulong id = IdOf(wrapper);
            return id == 0 ? default : new InteropObject { id = id, type = Types.Of(wrapper.GetType()) };
        }

        /// <summary>A new GC handle of a C# object for native code (0 for null); native code frees it.</summary>
        public static ulong NewHandle(object target) => target == null ? 0 : (ulong)GCHandle.ToIntPtr(GCHandle.Alloc(target));

        public static T Wrap<T>(InteropObject o) where T : class => Wrap<T>(o.id, o.type);

        public static T Wrap<T>(ulong id, uint nativeType) where T : class
        {
            if (id == 0)
                return null;
            lock (s_Wrappers)
            {
                if (s_Wrappers.TryGetValue(id, out WeakReference<object> weak) && weak.TryGetTarget(out object known) && known is T same)
                    return same;
                if (!s_ByNative.TryGetValue(nativeType, out Func<ObjectId, object> factory) && !s_ByType.TryGetValue(typeof(T), out factory))
                    throw new InvalidOperationException($"no wrapper for native type {nativeType} or {typeof(T)}");
                object made = factory(new ObjectId(id));
                s_Wrappers[id] = new WeakReference<object>(made);
                s_Ids.AddOrUpdate(made, new StrongBox<ulong>(id));
                return (T)made;
            }
        }

        public static unsafe T[] WrapArray<T>(InteropArray a) where T : class
        {
            if (a.count <= 0)
                return Array.Empty<T>();
            var items = new InteropObject[a.count];
            new ReadOnlySpan<InteropObject>(a.data, a.count).CopyTo(items);
            var result = new T[a.count];
            for (int i = 0; i < items.Length; i++)
                result[i] = Wrap<T>(items[i]);
            return result;
        }

        public static unsafe T[] WrapArray<T>(InteropObject* data, int count) where T : class => WrapArray<T>(new InteropArray { data = data, count = count });

        /// <summary>The ids of wrappers (0 for nulls), for arrays going to native code.</summary>
        public static ulong[] Ids<T>(T[] items, Func<T, ulong> id) where T : class
        {
            if (items == null)
                return Array.Empty<ulong>();
            var ids = new ulong[items.Length];
            for (int i = 0; i < items.Length; i++)
                ids[i] = items[i] == null ? 0 : id(items[i]);
            return ids;
        }
    }

    /// <summary>The handlers given to native events: the delegate, its GC handle and what the native side returned.</summary>
    public static class Events
    {
        sealed class Entry
        {
            public ulong Self;
            public int Event;
            public Delegate Handler;
            public uint Id;
            public IntPtr Native;
            public GCHandle Handle;
        }

        static readonly List<Entry> s_Entries = new();

        public static void Add(ulong self, int eventIndex, Delegate handler, uint id, IntPtr native, GCHandle handle)
        {
            lock (s_Entries)
                s_Entries.Add(new Entry { Self = self, Event = eventIndex, Handler = handler, Id = id, Native = native, Handle = handle });
        }

        /// <summary>The last registration of the handler (as C# events remove the last one added).</summary>
        public static bool Take(ulong self, int eventIndex, Delegate handler, out uint id, out IntPtr native, out GCHandle handle)
        {
            lock (s_Entries)
            {
                for (int i = s_Entries.Count - 1; i >= 0; i--)
                {
                    Entry e = s_Entries[i];
                    if (e.Self == self && e.Event == eventIndex && e.Handler.Equals(handler))
                    {
                        s_Entries.RemoveAt(i);
                        (id, native, handle) = (e.Id, e.Native, e.Handle);
                        return true;
                    }
                }
            }
            (id, native, handle) = (0, IntPtr.Zero, default);
            return false;
        }
    }
}
