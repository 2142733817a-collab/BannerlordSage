using System;
using System.IO;
using System.Runtime.InteropServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace BannerlordSage.EditorProbe
{
    internal static class NativeHeightmap
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Node { public int X; public int Y; }
        // Same backend as TerrainInspector::on_importHeightmapButton_clicked.
        // Mode zero imports absolute height; the node rectangle is inclusive.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ImportMap(IntPtr path, ref Node first, ref Node last, IntPtr terrain, IntPtr unusedArgument,
            double scale, double offset, int imageWidth, int imageHeight, int bitDepth, int mode, [MarshalAs(UnmanagedType.I1)] bool delta);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Refresh(IntPtr terrain);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FinishTerrainEdit(IntPtr controller, int flags);

        public static object Import(Scene scene, string path, double scale, double offset, string outputDirectory)
        {
            if (!scene.ContainsTerrain) throw new InvalidOperationException("Generate terrain before importing a heightmap.");
            NativeTerrain.FindInspector(); // Verifies native hash, inspector identity and Qt thread.
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 1000 ||
                double.IsNaN(offset) || double.IsInfinity(offset) || Math.Abs(offset) > 1000)
                throw new InvalidOperationException("Height scale/offset is outside the bounded range.");
            string full = Path.GetFullPath(path ?? "");
            string root = Path.GetFullPath(outputDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Heightmaps must be local PNG files in the bridge output directory.");
            var info = new FileInfo(full);
            if (!info.Exists || info.Length < 33 || info.Length > 16 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Invalid or oversized heightmap file.");
            byte[] header = new byte[29];
            using (var stream = File.OpenRead(full)) if (stream.Read(header, 0, header.Length) != header.Length) throw new IOException("Truncated PNG.");
            byte[] signature = {137,80,78,71,13,10,26,10};
            for (int i = 0; i < signature.Length; i++) if (header[i] != signature[i]) throw new InvalidOperationException("Heightmap is not a PNG.");
            if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R' || header[24] != 16 || header[25] != 0)
                throw new InvalidOperationException("Use a 16-bit single-channel grayscale heightmap PNG.");
            int width = BigEndian(header, 16), height = BigEndian(header, 20);
            if (width < 2 || height < 2 || width > 2049 || height > 2049) throw new InvalidOperationException("Heightmap dimensions must be 2 to 2049 pixels.");
            Vec2i nodes;
            float size;
            int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            if (nodes.X < 1 || nodes.Y < 1 || nodes.X > 16 || nodes.Y > 16) throw new InvalidOperationException("Unsupported terrain node count.");
            IntPtr manager = Marshal.ReadIntPtr(NativeTerrain.Address(0x18defa0));
            if (manager == IntPtr.Zero) throw new InvalidOperationException("Editing manager is not initialized.");
            IntPtr terrain = Marshal.ReadIntPtr(manager, 0x10d0), nativeScene = Marshal.ReadIntPtr(manager, 0x10c0);
            if (terrain == IntPtr.Zero || nativeScene == IntPtr.Zero || Marshal.ReadIntPtr(terrain, 0x68) != nativeScene ||
                Marshal.ReadInt32(terrain, 0x14) != nodes.X || Marshal.ReadInt32(terrain, 0x18) != nodes.Y)
                throw new InvalidOperationException("Active editable terrain does not match the scene.");
            IntPtr editController = IntPtr.Add(manager, 0x460);
            if (Marshal.ReadIntPtr(editController, 0x568) != manager)
                throw new InvalidOperationException("The terrain edit controller does not match its verified owner.");
            var first = new Node { X = 0, Y = 0 };
            var last = new Node { X = nodes.X - 1, Y = nodes.Y - 1 };
            using (var filename = new NativeSceneSave.EngineString(full.Replace('\\', '/')))
            {
                var import = (ImportMap)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0x890170), typeof(ImportMap));
                // The verified x64 ABI retains an unused fifth argument; scale is
                // argument six (entry stack +0x30), offset is argument seven.
                if (!import(filename.Pointer, ref first, ref last, terrain, IntPtr.Zero, scale, offset, width, height, 16, 0, false))
                    throw new InvalidOperationException("The native heightmap importer rejected the file. Inspect before retrying.");
            }
            // The official inspector commits the editable height changes after
            // import, before refreshing the renderer. Omitting this leaves the
            // editor's persisted edit state stale even when the preview is correct.
            var finish = (FinishTerrainEdit)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0x4987e0), typeof(FinishTerrainEdit));
            finish(editController, 0);
            // Finish the same dirty/terrain refresh work performed by the inspector.
            Marshal.WriteInt32(nativeScene, 0x2c0c, Marshal.ReadInt32(nativeScene, 0x2c0c) + 1);
            Marshal.WriteByte(terrain, 0x170, 1);
            Marshal.WriteInt32(terrain, 0x10, 0);
            var refresh = (Refresh)Marshal.GetDelegateForFunctionPointer(NativeTerrain.Address(0x6ca7d0), typeof(Refresh));
            refresh(terrain);
            return new { imported = true, heightmapPath = full, width, height, bitDepth = 16, scale, offset, terrain = NativeTerrain.Read(scene) };
        }

        private static int BigEndian(byte[] bytes, int offset)
        { return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]; }
    }
}
