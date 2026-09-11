// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.ITunes.Interop;

internal sealed unsafe class ITunesEventSink : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IDispatchVtbl
    {
        public delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<nint, uint> AddRef;
        public delegate* unmanaged[Stdcall]<nint, uint> Release;
        public delegate* unmanaged[Stdcall]<nint, uint*, int> GetTypeInfoCount;
        public delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int> GetTypeInfo;
        public delegate* unmanaged[Stdcall]<nint, Guid*, nint, uint, uint, nint, int> GetIDsOfNames;
        public delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, nint, nint, nint, uint*, int> Invoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComInstance
    {
        public IDispatchVtbl* Vtbl;
        public nint GcHandle;
    }

    private static readonly nint StaticVtblPtr;

    static ITunesEventSink()
    {
        var vtbl = (IDispatchVtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(ITunesEventSink),
            sizeof(IDispatchVtbl));
        vtbl->QueryInterface = &QueryInterfaceImpl;
        vtbl->AddRef = &AddRefImpl;
        vtbl->Release = &ReleaseImpl;
        vtbl->GetTypeInfoCount = &GetTypeInfoCountImpl;
        vtbl->GetTypeInfo = &GetTypeInfoImpl;
        vtbl->GetIDsOfNames = &GetIDsOfNamesImpl;
        vtbl->Invoke = &InvokeImpl;
        StaticVtblPtr = (nint)vtbl;
    }

    private readonly Action<int> _onEvent;
    private ComInstance* _instance;
    private GCHandle _handle;
    private int _refCount = 1;

    public ITunesEventSink(Action<int> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        this._onEvent = onEvent;
        this._handle = GCHandle.Alloc(this, GCHandleType.Weak);
        this._instance = (ComInstance*)NativeMemory.Alloc((nuint)sizeof(ComInstance));
        this._instance->Vtbl = (IDispatchVtbl*)StaticVtblPtr;
        this._instance->GcHandle = GCHandle.ToIntPtr(this._handle);
    }

    public nint IUnknownPointer => (nint)this._instance;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterfaceImpl(nint thisPtr, Guid* riid, nint* ppv)
    {
        if (ppv == null || riid == null)
        {
            return unchecked((int)0x80070057); // E_INVALIDARG
        }

        if (*riid == ITunesGuids.IidIUnknown ||
            *riid == ITunesGuids.IidIDispatch ||
            *riid == ITunesGuids.DiidIiTunesEvents)
        {
            *ppv = thisPtr;
            AddRefCore((ComInstance*)thisPtr);
            return 0; // S_OK
        }

        *ppv = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    private static uint AddRefCore(ComInstance* inst)
    {
        if (GCHandle.FromIntPtr(inst->GcHandle).Target is ITunesEventSink sink)
        {
            return (uint)Interlocked.Increment(ref sink._refCount);
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRefImpl(nint thisPtr)
    {
        return AddRefCore((ComInstance*)thisPtr);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseImpl(nint thisPtr)
    {
        var inst = (ComInstance*)thisPtr;
        if (GCHandle.FromIntPtr(inst->GcHandle).Target is ITunesEventSink sink)
        {
            return (uint)Interlocked.Decrement(ref sink._refCount);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCountImpl(nint thisPtr, uint* pctinfo)
    {
        if (pctinfo != null)
        {
            *pctinfo = 0;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoImpl(nint thisPtr, uint iTInfo, uint lcid, nint* ppTInfo)
    {
        if (ppTInfo != null)
        {
            *ppTInfo = 0;
        }

        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNamesImpl(nint thisPtr, Guid* riid, nint rgszNames, uint cNames, uint lcid, nint rgDispId)
    {
        return unchecked((int)0x80004001); // E_NOTIMPL
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InvokeImpl(nint thisPtr, int dispIdMember, Guid* riid, uint lcid, ushort wFlags, nint pDispParams, nint pVarResult, nint pExcepInfo, uint* puArgErr)
    {
        var inst = (ComInstance*)thisPtr;
        if (GCHandle.FromIntPtr(inst->GcHandle).Target is ITunesEventSink sink)
        {
            try
            {
                sink._onEvent(dispIdMember);
            }
            catch
            {
                // Do not leak exceptions to native callers
            }
        }

        return 0; // S_OK
    }

    public void Dispose()
    {
        if (this._instance != null)
        {
            if (this._handle.IsAllocated)
            {
                this._handle.Free();
            }

            NativeMemory.Free(this._instance);
            this._instance = null;
        }
    }
}
