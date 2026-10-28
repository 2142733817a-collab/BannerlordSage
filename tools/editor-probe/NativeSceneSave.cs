using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BannerlordSage.EditorProbe
{
    internal static class NativeSceneSave
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CopyString(IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DeleteString(IntPtr value, byte flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SaveAs(IntPtr manager, IntPtr sceneName, IntPtr modulePath, [MarshalAs(UnmanagedType.I1)] bool quickSave);

        internal sealed class EngineString : IDisposable
        {
            public readonly IntPtr Pointer;
            public EngineString(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
                IntPtr source = Marshal.AllocHGlobal(32), chars = Marshal.AllocHGlobal(bytes.Length);
                Pointer = Marshal.AllocHGlobal(32);
                try
                {
                    Marshal.Copy(new byte[32], 0, source, 32);
                    Marshal.Copy(bytes, 0, chars, bytes.Length);
                    Marshal.WriteIntPtr(source, 8, chars);
                    Marshal.WriteInt32(source, 16, bytes.Length - 1);
                    var copy = (CopyString)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0xe09fd0), typeof(CopyString));
                    copy(Pointer, source);
                }
                catch { Marshal.FreeHGlobal(Pointer); throw; }
                finally { Marshal.FreeHGlobal(chars); Marshal.FreeHGlobal(source); }
            }
            public void Dispose()
            {
                var destroy = (DeleteString)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0xe09bf0), typeof(DeleteString));
                destroy(Pointer, 0);
                Marshal.FreeHGlobal(Pointer);
            }
        }

        public static void Save(string sceneName, string modulePath)
        {
            // Editing_manager::save_scene_as is the engine function called by the
            // official Save As command. Inputs are engine-owned rglString copies.
            IntPtr manager = Marshal.ReadIntPtr(NativeTerrain.Address(0x18defa0));
            if (manager == IntPtr.Zero) throw new InvalidOperationException("The editing manager is not initialized.");
            using (var name = new EngineString(sceneName))
            using (var module = new EngineString(modulePath.Replace('\\', '/').TrimEnd('/') + "/"))
            {
                var save = (SaveAs)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0x3c32c0), typeof(SaveAs));
                save(manager, name.Pointer, module.Pointer, false);
            }
        }
    }
}
