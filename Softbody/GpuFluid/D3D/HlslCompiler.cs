// 0.16.0: compiles the GPU fluid's HLSL at run time with Windows' own d3dcompiler_47.dll (no Unity, no shader bundle), and
// reads which register each resource of a kernel landed in from the bytecode's RDEF chunk (the compiler gives every kernel
// its own numbering of the resources it uses). netstandard2.0: compiled by the renderer fork and the offline tests alike.
#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Softbody.FluidGpu
{
    public enum HlslBindType { CBuffer = 0, TBuffer = 1, Texture = 2, Sampler = 3, RwTyped = 4, Structured = 5, RwStructured = 6, ByteAddress = 7, RwByteAddress = 8, Append = 9, Consume = 10, RwStructuredCounter = 11 }

    public struct HlslBinding
    {
        public string Name;
        public HlslBindType Type;
        public int Slot;
        public bool IsUav => Type == HlslBindType.RwTyped || Type == HlslBindType.RwStructured || Type == HlslBindType.RwByteAddress ||
                             Type == HlslBindType.Append || Type == HlslBindType.Consume || Type == HlslBindType.RwStructuredCounter;
        public bool IsSrv => Type == HlslBindType.TBuffer || Type == HlslBindType.Texture || Type == HlslBindType.Structured || Type == HlslBindType.ByteAddress;
        public override string ToString() => Name + " " + Type + " " + Slot;
    }

    public static class HlslCompiler
    {
        const uint OptimizationLevel3 = 1u << 15;

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int D3DCompile(byte[] srcData, UIntPtr srcDataSize, byte[] sourceName, IntPtr defines, IntPtr include,
            byte[] entryPoint, byte[] target, uint flags1, uint flags2, out IntPtr code, out IntPtr errorMsgs);

        static byte[] Z(string s) { var b = Encoding.ASCII.GetBytes(s); Array.Resize(ref b, b.Length + 1); return b; }

        /// <summary>Compiles one kernel (cs_5_0). Throws with the compiler's messages when it fails.</summary>
        public static byte[] Compile(string source, string entry, string sourceName = "FluidSim.hlsl")
        {
            var src = Encoding.UTF8.GetBytes(source);
            int hr = D3DCompile(src, (UIntPtr)src.Length, Z(sourceName), IntPtr.Zero, IntPtr.Zero, Z(entry), Z("cs_5_0"),
                OptimizationLevel3, 0, out var code, out var errors);
            string messages = errors != IntPtr.Zero ? BlobString(errors) : null;
            if (hr < 0 || code == IntPtr.Zero)
            {
                if (code != IntPtr.Zero) Release(code);
                throw new InvalidOperationException($"compiling {entry} failed (0x{hr:X8}): {messages}");
            }
            try { return BlobBytes(code); }
            finally { Release(code); }
        }

        // ID3DBlob: IUnknown (0 QueryInterface, 1 AddRef, 2 Release), 3 GetBufferPointer, 4 GetBufferSize
        delegate IntPtr GetPointerFn(IntPtr self);
        delegate UIntPtr GetSizeFn(IntPtr self);
        delegate uint ReleaseFn(IntPtr self);

        static T Method<T>(IntPtr com, int index) where T : class
        {
            var vtable = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, index * IntPtr.Size), typeof(T));
        }

        static byte[] BlobBytes(IntPtr blob)
        {
            var ptr = Method<GetPointerFn>(blob, 3)(blob);
            int size = (int)(ulong)Method<GetSizeFn>(blob, 4)(blob);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }

        static string BlobString(IntPtr blob)
        {
            try { return Encoding.ASCII.GetString(BlobBytes(blob)).TrimEnd('\0'); }
            finally { Release(blob); }
        }

        static void Release(IntPtr com) => Method<ReleaseFn>(com, 2)(com);

        /// <summary>The resources a compiled kernel binds, with their registers (from the bytecode's RDEF chunk).</summary>
        public static List<HlslBinding> Reflect(byte[] dxbc)
        {
            if (dxbc.Length < 32 || dxbc[0] != 'D' || dxbc[1] != 'X' || dxbc[2] != 'B' || dxbc[3] != 'C') throw new InvalidOperationException("not DXBC bytecode");
            int chunks = BitConverter.ToInt32(dxbc, 28);
            for (int c = 0; c < chunks; c++)
            {
                int off = BitConverter.ToInt32(dxbc, 32 + 4 * c);
                if (dxbc[off] != 'R' || dxbc[off + 1] != 'D' || dxbc[off + 2] != 'E' || dxbc[off + 3] != 'F') continue;
                int data = off + 8;
                int bindCount = BitConverter.ToInt32(dxbc, data + 8), bindOffset = BitConverter.ToInt32(dxbc, data + 12);
                int minor = dxbc[data + 16], major = dxbc[data + 17];
                int stride = major > 5 || (major == 5 && minor >= 1) ? 40 : 32;
                var list = new List<HlslBinding>(bindCount);
                for (int b = 0; b < bindCount; b++)
                {
                    int d = data + bindOffset + b * stride;
                    int nameOff = BitConverter.ToInt32(dxbc, d);
                    int end = data + nameOff;
                    while (dxbc[end] != 0) end++;
                    list.Add(new HlslBinding
                    {
                        Name = Encoding.ASCII.GetString(dxbc, data + nameOff, end - data - nameOff),
                        Type = (HlslBindType)BitConverter.ToInt32(dxbc, d + 4),
                        Slot = BitConverter.ToInt32(dxbc, d + 20),
                    });
                }
                return list;
            }
            throw new InvalidOperationException("the bytecode has no RDEF chunk");
        }
    }
}
