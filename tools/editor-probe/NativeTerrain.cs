using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace BannerlordSage.EditorProbe
{
    // Private native ABI, deliberately restricted to the locally examined editor binary.
    // QTerrainInspector moc slot 20 dispatches vtable +0xf0 to TerrainInspector's
    // Generate Terrain handler. Its four input fields are node X/Y, size and log2(dim).
    internal static class NativeTerrain
    {
        private const string SupportedHash = "B87F2439E0869945E6AA4A0ADA3D8EE735D6E12276AECFEA84DCE44379243413";
        private static IntPtr _nativeBase;
        private static bool _verified;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("Qt5Core.dll", EntryPoint = "?thread@QObject@@QEBAPEAVQThread@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ObjectThread(IntPtr obj);
        [DllImport("Qt5Core.dll", EntryPoint = "?currentThread@QThread@@SAPEAV1@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CurrentThread();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GenerateHandler(IntPtr inspector, [MarshalAs(UnmanagedType.I1)] bool clicked);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetEditableNode(IntPtr terrain, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GroundBrush(IntPtr terrain, int layer, ref Vec3 position, IntPtr unused4, IntPtr unused5,
            float radius, IntPtr unused7, IntPtr unused8, IntPtr unused9, IntPtr unused10, IntPtr unused11,
            ref int mode, [MarshalAs(UnmanagedType.I1)] bool allLayers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FinishBrush(IntPtr controller, int layer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RefreshHeightRange(IntPtr terrain);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LookupLayerTemplate(IntPtr unused, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AddLayerNative(IntPtr terrain, IntPtr template, IntPtr unused, IntPtr name, IntPtr material);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InspectorRefresh(IntPtr inspector);
        public static object AddLayer(Scene scene, string template, string name)
        {
            IntPtr inspector = FindInspector();
            if (!System.Text.RegularExpressions.Regex.IsMatch(template ?? "", "^[A-Za-z][A-Za-z0-9_]{0,127}$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[A-Za-z][A-Za-z0-9 _-]{0,63}$"))
                throw new InvalidOperationException("Use an official template and an English layer name.");
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr terrain = RequiredPointer(manager, 0x10d0);
            int before = Marshal.ReadInt32(terrain, 0x78);
            if (before < 1 || before >= 15) throw new InvalidOperationException("Terrain layer count is outside the supported range.");
            using (var source = new NativeSceneSave.EngineString(template))
            using (var label = new NativeSceneSave.EngineString(name))
            {
                var lookup = (LookupLayerTemplate)Marshal.GetDelegateForFunctionPointer(Address(0x1fb370), typeof(LookupLayerTemplate));
                IntPtr material = lookup(IntPtr.Zero, source.Pointer);
                if (material == IntPtr.Zero) throw new InvalidOperationException("Official terrain template does not exist.");
                var add = (AddLayerNative)Marshal.GetDelegateForFunctionPointer(Address(0x5b0210), typeof(AddLayerNative));
                if (add(terrain, source.Pointer, IntPtr.Zero, label.Pointer, material) == IntPtr.Zero ||
                    Marshal.ReadInt32(terrain, 0x78) != before + 1)
                    throw new InvalidOperationException("Layer creation did not complete; inspect before retrying.");
            }
            foreach (int rva in new [] { 0x5bb4f0, 0x5bc0d0 })
                ((InspectorRefresh)Marshal.GetDelegateForFunctionPointer(Address(rva), typeof(InspectorRefresh)))(inspector);
            IntPtr nativeScene = RequiredPointer(manager, 0x10c0);
            Marshal.WriteInt32(nativeScene, 0x2c0c, Marshal.ReadInt32(nativeScene, 0x2c0c) + 1);
            return new { created = true, name, template, layerIndex = before, layerCount = before + 1 };
        }

        public static object PaintPlan(Scene scene, string planPath, string outputDirectory)
        {
            FindInspector();
            var rows = ReadBrushPlan(planPath, outputDirectory);
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            var patterns = ReadBrushPatterns();
            foreach (var row in rows)
            {
                int layer = row.Value<int>("layer"), passes = row.Value<int?>("passes") ?? 8, mode = row.Value<int?>("mode") ?? 0;
                float x = row.Value<float>("x"), y = row.Value<float>("y"), radius = row.Value<float>("radius"), strength = row.Value<float?>("strength") ?? 1;
                float hardness = row.Value<float?>("hardness") ?? 0.5f, angle = row.Value<float?>("angle") ?? 0,
                    target = row.Value<float?>("target") ?? 0;
                string pattern = row.Value<string>("pattern");
                if (layer < 1 || layer >= layers || mode < 0 || mode > 3 || !ValidBrush(x, y, radius, strength, passes, nodes, size) ||
                    !FiniteRange(hardness, 0, 1) || !FiniteRange(angle, 0, 360) || !FiniteRange(target, 0, 1) ||
                    (pattern != null && !patterns.ContainsKey(pattern)))
                    throw new InvalidOperationException("Paint plan contains an invalid layer or brush stroke.");
            }
            int applied = 0;
            foreach (var row in rows)
            {
                ApplyBrush(scene, row.Value<float>("x"), row.Value<float>("y"), row.Value<float>("radius"),
                    row.Value<float?>("strength") ?? 1, row.Value<int?>("passes") ?? 8, row.Value<int>("layer") + 1, row.Value<int?>("mode") ?? 0,
                    row.Value<float?>("hardness") ?? 0.5f,
                    row.Value<string>("pattern") == null ? -1 : patterns[row.Value<string>("pattern")],
                    row.Value<float?>("angle") ?? 0, row.Value<float?>("target") ?? 0);
                applied++;
            }
            return new { painted = true, appliedStrokes = applied, layerCount = layers };
        }

        private static Newtonsoft.Json.Linq.JArray ReadBrushPlan(string planPath, string outputDirectory)
        {
            string full = Path.GetFullPath(planPath ?? "");
            string root = Path.GetFullPath(outputDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(full) || new FileInfo(full).Length > 262144 || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Paint plan must be a bounded JSON file under the bridge output directory.");
            var rows = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(full))["strokes"] as Newtonsoft.Json.Linq.JArray;
            if (rows == null || rows.Count < 1 || rows.Count > 1024) throw new InvalidOperationException("Paint plan requires 1..1024 strokes.");
            return rows;
        }

        public static object HeightBrushPlan(Scene scene, string planPath, string outputDirectory)
        {
            FindInspector();
            var rows = ReadBrushPlan(planPath, outputDirectory);
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            // This separate action cannot accidentally interpret a material plan
            // as elevation edits. Modes are native lower (1) and smooth (2).
            foreach (var row in rows)
            {
                int passes = row.Value<int?>("passes") ?? 1, mode = row.Value<int>("mode");
                float x = row.Value<float>("x"), y = row.Value<float>("y"), radius = row.Value<float>("radius"),
                    strength = row.Value<float>("strength"), hardness = row.Value<float?>("hardness") ?? 0;
                if (row["x"] == null || row["y"] == null || row["radius"] == null || row["strength"] == null || row["mode"] == null ||
                    row["layer"] != null || row["pattern"] != null || row["target"] != null ||
                    (mode != 1 && mode != 2) || !ValidBrush(x, y, radius, strength, passes, nodes, size) ||
                    radius > 8 || strength > 0.25f || passes > 4 || !FiniteRange(hardness, 0, 0.6f))
                    throw new InvalidOperationException("Height plan requires explicit lower/smooth strokes, radius 1-8, strength 0.01-0.25 and 1-4 passes.");
            }
            int lowered = 0, smoothed = 0;
            foreach (var row in rows)
            {
                ApplyBrush(scene, row.Value<float>("x"), row.Value<float>("y"), row.Value<float>("radius"),
                    row.Value<float>("strength"), row.Value<int?>("passes") ?? 1, 0, row.Value<int>("mode"), row.Value<float?>("hardness") ?? 0);
                if (row.Value<int>("mode") == 1) lowered++; else smoothed++;
            }
            return new { appliedStrokes = rows.Count, lowered, smoothed, bounds = RepairHeightBounds(scene) };
        }

        private static bool FiniteRange(float value, float low, float high)
        { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= low && value <= high; }

        // The native pattern loader (0x3ce9b0) builds parallel vectors of
        // rglString paths (+0x2b0, 32 bytes each) and CPU bitmaps (+0x1cb0).
        // Ground paint (0x824800) uses brush+0x1c as the bitmap index.
        private static System.Collections.Generic.Dictionary<string, int> ReadBrushPatterns()
        {
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr paths = RequiredPointer(manager, 0x2b0), end = RequiredPointer(manager, 0x2b8);
            IntPtr bitmaps = RequiredPointer(manager, 0x1cb0), bitmapEnd = RequiredPointer(manager, 0x1cb8);
            long bytes = end.ToInt64() - paths.ToInt64();
            if (bytes < 32 || bytes > 64 * 32 || bytes % 32 != 0 || bitmapEnd.ToInt64() - bitmaps.ToInt64() != bytes / 4)
                throw new InvalidOperationException("Native brush pattern vectors do not match the verified profile.");
            var result = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bytes / 32; i++)
            {
                IntPtr path = IntPtr.Add(paths, i * 32);
                int length = Marshal.ReadInt32(path, 16);
                if (length < 1 || length > 1024) throw new InvalidOperationException("Native brush pattern path is invalid.");
                byte[] utf8 = new byte[length];
                Marshal.Copy(RequiredPointer(path, 8), utf8, 0, length);
                string name = Path.GetFileName(System.Text.Encoding.UTF8.GetString(utf8).Replace('/', Path.DirectorySeparatorChar));
                if (Marshal.ReadIntPtr(bitmaps, i * 8) != IntPtr.Zero && !result.ContainsKey(name)) result.Add(name, i);
            }
            return result;
        }

        public static object LayerStats(Scene scene)
        {
            FindInspector();
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            if (nodes.X < 1 || nodes.Y < 1 || nodes.X > 16 || nodes.Y > 16 || layers < 1 || layers > 15)
                throw new InvalidOperationException("Unsupported layer statistics size.");
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr terrain = RequiredPointer(manager, 0x10d0);
            var getNode = (GetEditableNode)Marshal.GetDelegateForFunctionPointer(Address(0x343c80), typeof(GetEditableNode));
            var result = new System.Collections.Generic.List<object>();
            int total = 0;
            for (int x = 0; x < nodes.X; x++) for (int y = 0; y < nodes.Y; y++)
            {
                IntPtr node = getNode(terrain, x, y);
                if (node == IntPtr.Zero) throw new InvalidOperationException("Layer statistics node is missing.");
                for (int layer = 0; layer < layers; layer++)
                {
                    int slot = layer + 1;
                    int width = Marshal.ReadInt32(node, 0x8b8 + slot * 8), height = Marshal.ReadInt32(node, 0x8bc + slot * 8);
                    if (width < 1 || height < 1 || width > 1025 || height > 1025 || (total += width * height) > 8000000)
                        throw new InvalidOperationException("Layer statistics resolution is unsupported.");
                    float[] values = new float[width * height];
                    Marshal.Copy(RequiredPointer(node, 0x948 + slot * 8), values, 0, values.Length);
                    double sum = 0; float min = float.PositiveInfinity, max = float.NegativeInfinity; int painted = 0;
                    foreach (float value in values) { sum += value; min = Math.Min(min, value); max = Math.Max(max, value); if (value > 0.05f) painted++; }
                    result.Add(new { nodeX = x, nodeY = y, layer, width, height, min, max, mean = sum / values.Length, paintedSamples = painted });
                }
            }
            return new { layerCount = layers, floraInstanceCount = scene.GetFloraInstanceCount(), nodes = result };
        }

        // The native height texture writer (0x6e4040) casts an unclamped normalized
        // float to ushort. Stale node extrema can therefore wrap a valley to a peak
        // or a peak to a hole. Keep the metadata conservative without editing heights.
        public static object RepairHeightBounds(Scene scene)
        {
            if (!scene.ContainsTerrain) return new { repaired = false, containsTerrain = false };
            FindInspector();
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            if (nodes.X < 1 || nodes.Y < 1 || nodes.X > 16 || nodes.Y > 16)
                throw new InvalidOperationException("Unsupported terrain bounds size.");
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr terrain = RequiredPointer(manager, 0x10d0);
            IntPtr nativeScene = RequiredPointer(manager, 0x10c0);
            if (Marshal.ReadIntPtr(terrain, 0x68) != nativeScene || Marshal.ReadIntPtr(nativeScene, 0x99a50) != terrain)
                throw new InvalidOperationException("Height bounds owner does not match the active terrain.");
            var getNode = (GetEditableNode)Marshal.GetDelegateForFunctionPointer(Address(0x343c80), typeof(GetEditableNode));
            var updates = new System.Collections.Generic.List<Tuple<IntPtr, float, float>>();
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            int sampleCount = 0;
            for (int x = 0; x < nodes.X; x++) for (int y = 0; y < nodes.Y; y++)
            {
                IntPtr node = getNode(terrain, x, y);
                if (node == IntPtr.Zero || Marshal.ReadIntPtr(node, 0x10) != terrain)
                    throw new InvalidOperationException("Height bounds node owner is invalid.");
                int width = Marshal.ReadInt32(node, 0x8b8), height = Marshal.ReadInt32(node, 0x8bc);
                if (width < 2 || height < 2 || width > 257 || height > 257 || (sampleCount += width * height) > 4000000)
                    throw new InvalidOperationException("Height bounds sample count is unsupported.");
                float[] values = new float[width * height];
                Marshal.Copy(RequiredPointer(node, 0x948), values, 0, values.Length);
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                foreach (float value in values)
                {
                    if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 10000)
                        throw new InvalidOperationException("Height bounds contain a non-finite or unsupported sample.");
                    low = Math.Min(low, value); high = Math.Max(high, value);
                }
                minimum = Math.Min(minimum, low); maximum = Math.Max(maximum, high);
                float oldLow = ReadFloat(node, 0x78), oldHigh = ReadFloat(node, 0x7c);
                // Scene.GetTerrainNodeData reads the saved definition, which may
                // differ from the editable node; it is not a layout validator.
                if (float.IsNaN(oldLow) || float.IsInfinity(oldLow) || float.IsNaN(oldHigh) || float.IsInfinity(oldHigh) ||
                    oldLow > oldHigh || Math.Abs(oldLow) > 10000 || Math.Abs(oldHigh) > 10000)
                    throw new InvalidOperationException("Editable node height bounds are invalid.");
                // Round outwards with room for the scene XML's three decimal places.
                float safeLow = (float)(Math.Floor((double)low * 100) / 100 - 0.01);
                float safeHigh = (float)(Math.Ceiling((double)high * 100) / 100 + 0.01);
                if (oldLow > safeLow || oldHigh < safeHigh)
                    updates.Add(Tuple.Create(node, Math.Min(oldLow, safeLow), Math.Max(oldHigh, safeHigh)));
            }
            foreach (var update in updates)
            {
                WriteFloat(update.Item1, 0x78, update.Item2);
                WriteFloat(update.Item1, 0x7c, update.Item3);
            }
            if (updates.Count > 0)
            {
                var refresh = (RefreshHeightRange)Marshal.GetDelegateForFunctionPointer(Address(0x5aabf0), typeof(RefreshHeightRange));
                refresh(terrain);
                Marshal.WriteInt32(nativeScene, 0x2c0c, Marshal.ReadInt32(nativeScene, 0x2c0c) + 1);
            }
            return new { repaired = updates.Count > 0, updatedNodes = updates.Count, sampledMin = minimum,
                sampledMax = maximum, minHeight = ReadFloat(terrain, 0x154), maxHeight = ReadFloat(terrain, 0x158), sampleCount };
        }

        private static float ReadFloat(IntPtr owner, int offset)
        { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(owner, offset)), 0); }
        private static void WriteFloat(IntPtr owner, int offset, float value)
        { Marshal.WriteInt32(owner, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0)); }

        public static object SmoothBrush(Scene scene, float x, float y, float radius, float strength, int passes)
        {
            ApplyBrush(scene, x, y, radius, strength, passes, 0, 2);
            return new { smoothed = true, x, y, radius, strength, passes, terrain = Read(scene) };
        }

        private static bool ValidBrush(float x, float y, float radius, float strength, int passes, Vec2i nodes, float size)
        {
            return !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(radius) && !float.IsNaN(strength) &&
                x >= 0 && y >= 0 && x <= nodes.X * size && y <= nodes.Y * size &&
                radius >= 1 && radius <= 20 && strength >= 0.01f && strength <= 1 && passes >= 1 && passes <= 16;
        }

        private static void ApplyBrush(Scene scene, float x, float y, float radius, float strength, int passes, int layer, int mode,
            float hardness = 0.5f, int patternIndex = -1, float angle = 0, float target = 0)
        {
            FindInspector();
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            if (!ValidBrush(x, y, radius, strength, passes, nodes, size))
                throw new InvalidOperationException("Brush requires an in-bounds centre, radius 1-20, strength 0.01-1 and 1-16 passes.");
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr terrain = Marshal.ReadIntPtr(manager, 0x10d0), nativeScene = Marshal.ReadIntPtr(manager, 0x10c0);
            if (terrain == IntPtr.Zero || Marshal.ReadIntPtr(terrain, 0x68) != nativeScene ||
                Marshal.ReadIntPtr(IntPtr.Add(manager, 0x460), 0x568) != manager)
                throw new InvalidOperationException("Terrain brush owner does not match its verified profile.");
            int previousTool = Marshal.ReadInt32(manager, 0x1130), previousDelta = Marshal.ReadInt32(manager, 0x2d4);
            byte previousAffectUpper = Marshal.ReadByte(manager, 0x1459);
            // The official height tool uses Edit_mode_params tool 2, whose brush
            // lives at +0x110. Ground paint mode 2 invokes native height smoothing.
            IntPtr brush = IntPtr.Add(manager, 0x10d8 + (layer == 0 ? 0x110 : 0x178));
            byte[] previousBrush = new byte[0x68]; Marshal.Copy(brush, previousBrush, 0, previousBrush.Length);
            var paint = (GroundBrush)Marshal.GetDelegateForFunctionPointer(Address(0x5a9750), typeof(GroundBrush));
            var finish = (FinishBrush)Marshal.GetDelegateForFunctionPointer(Address(0x4987e0), typeof(FinishBrush));
            var position = new Vec3(x, y, scene.GetTerrainHeight(new Vec2(x, y)));
            try
            {
                Marshal.WriteInt32(manager, 0x1130, layer == 0 ? 2 : 1);
                Marshal.WriteInt32(manager, 0x2d4, BitConverter.ToInt32(BitConverter.GetBytes(0.1f), 0));
                Marshal.WriteInt32(brush, 0, mode);
                WriteFloat(brush, 4, angle);
                Marshal.WriteInt32(brush, 8, BitConverter.ToInt32(BitConverter.GetBytes(radius), 0));
                Marshal.WriteInt32(brush, 0xc, BitConverter.ToInt32(BitConverter.GetBytes(strength), 0));
                WriteFloat(brush, 0x10, hardness);
                WriteFloat(brush, 0x14, target);
                WriteFloat(brush, 0x18, 0); // no maximum per-stroke change
                Marshal.WriteInt32(brush, 0x1c, patternIndex);
                // Explicit strokes must not inherit UI height/slope/direction
                // filters or erase upper layers: overlapping layers are intended.
                Marshal.WriteByte(brush, 0x3c, 0);
                Marshal.WriteByte(brush, 0x48, 0);
                Marshal.WriteByte(brush, 0x54, 0);
                Marshal.WriteByte(manager, 0x1459, 0);
                for (int i = 0; i < passes; i++)
                    paint(terrain, layer, ref position, IntPtr.Zero, IntPtr.Zero, radius, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref mode, false);
                finish(IntPtr.Add(manager, 0x460), layer);
                Marshal.WriteInt32(nativeScene, 0x2c0c, Marshal.ReadInt32(nativeScene, 0x2c0c) + 1);
            }
            finally
            {
                Marshal.Copy(previousBrush, 0, brush, previousBrush.Length);
                Marshal.WriteInt32(manager, 0x1130, previousTool);
                Marshal.WriteInt32(manager, 0x2d4, previousDelta);
                Marshal.WriteByte(manager, 0x1459, previousAffectUpper);
            }
        }

        public static object ReadEditableSamples(Scene scene)
        {
            FindInspector();
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            if (nodes.X < 1 || nodes.Y < 1 || (long)nodes.X * nodes.Y > 16 ||
                float.IsNaN(size) || float.IsInfinity(size) || size <= 0 || size > 1000)
                throw new InvalidOperationException("Sample export supports at most sixteen nodes with a finite size up to 1000.");
            IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
            IntPtr terrain = Marshal.ReadIntPtr(manager, 0x10d0);
            var getNode = (GetEditableNode)Marshal.GetDelegateForFunctionPointer(Address(0x343c80), typeof(GetEditableNode));
            var result = new System.Collections.Generic.List<object>();
            int totalSamples = 0;
            for (int x = 0; x < nodes.X; x++) for (int y = 0; y < nodes.Y; y++)
            {
                IntPtr node = getNode(terrain, x, y);
                if (node == IntPtr.Zero) throw new InvalidOperationException("Editable terrain node is missing.");
                int width = Marshal.ReadInt32(node, 0x8b8), height = Marshal.ReadInt32(node, 0x8bc);
                if (width < 2 || height < 2 || width > 257 || height > 257) throw new InvalidOperationException("Unsupported editable heightmap dimensions.");
                totalSamples += width * height;
                if (totalSamples > 20000) throw new InvalidOperationException("Editable heightmap export exceeds the bounded reply size.");
                IntPtr data = Marshal.ReadIntPtr(node, 0x948);
                if (data == IntPtr.Zero) throw new InvalidOperationException("Editable terrain heightmap is missing.");
                float[] values = new float[width * height];
                Marshal.Copy(data, values, 0, values.Length);
                result.Add(new { x, y, width, height, values, collision = scene.GetTerrainHeightData(x, y) });
            }
            var projected = new System.Collections.Generic.List<object>();
            // Keep camera diagnostics bounded even for large, low-resolution maps.
            float projectionStep = Math.Max(5, Math.Max(nodes.X, nodes.Y) * size / 40);
            for (int ix = 0; ix <= 40 && ix * projectionStep <= nodes.X * size; ix++)
            for (int iy = 0; iy <= 40 && iy * projectionStep <= nodes.Y * size; iy++)
            {
                float x = ix * projectionStep, y = iy * projectionStep;
                float z = scene.GetTerrainHeight(new Vec2(x, y));
                Vec2 screen = TaleWorlds.MountAndBlade.MBEditor.GetEditorSceneView().WorldPointToScreenPoint(new Vec3(x, y, z));
                projected.Add(new { x, y, z, screenX = screen.x, screenY = screen.y });
            }
            return new { nodesX = nodes.X, nodesY = nodes.Y, nodeSize = size, nodes = result, projected };
        }

        private static void VerifyProfile()
        {
            if (_verified) return;
            if (IntPtr.Size != 8) throw new InvalidOperationException("Terrain generation requires the x64 editor.");
            _nativeBase = GetModuleHandle("TaleWorlds.Native.dll");
            if (_nativeBase == IntPtr.Zero) throw new InvalidOperationException("The native editor is not loaded.");
            string file = null;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.BaseAddress == _nativeBase) { file = module.FileName; break; }
            if (file == null) throw new InvalidOperationException("Cannot verify the native editor binary.");
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (hash != SupportedHash)
                throw new InvalidOperationException("Terrain generation has no verified ABI profile for this native editor binary.");
            _verified = true;
        }

        internal static IntPtr Address(int rva) { VerifyProfile(); return IntPtr.Add(_nativeBase, rva); }

        internal static IntPtr FindInspector()
        {
            VerifyProfile();
            // This controller is a parentless QObject, not a QWidget. Follow the
            // verified Qt interface -> EditWindow -> inspector set ownership chain.
            IntPtr qt = Marshal.ReadIntPtr(IntPtr.Add(_nativeBase, 0x18defd8));
            IntPtr window = RequiredPointer(qt, 0xa0);
            IntPtr inspectors = RequiredPointer(window, 0x118);
            IntPtr found = RequiredPointer(inspectors, 0x38);
            IntPtr expected = IntPtr.Add(_nativeBase, 0x13cffb8);
            if (Marshal.ReadIntPtr(found) != expected || Marshal.ReadIntPtr(expected, 0xf0) != IntPtr.Add(_nativeBase, 0x5b8170))
                throw new InvalidOperationException("The native terrain controller does not match its verified profile.");
            if (ObjectThread(found) != CurrentThread())
                throw new InvalidOperationException("Terrain generation must execute on the terrain inspector's Qt thread.");
            return found;
        }

        private static IntPtr RequiredPointer(IntPtr owner, int offset)
        {
            if (owner == IntPtr.Zero) throw new InvalidOperationException("The native terrain controller is not initialized.");
            IntPtr result = Marshal.ReadIntPtr(owner, offset);
            if (result == IntPtr.Zero) throw new InvalidOperationException("The native terrain controller is not initialized.");
            return result;
        }

        public static object Read(Scene scene)
        {
            bool supported = false;
            string diagnostic = null;
            try { FindInspector(); supported = true; }
            catch (Exception error) { diagnostic = error.Message; }
            if (!scene.ContainsTerrain)
                return new { containsTerrain = false, generationSupported = supported, diagnostic };
            Vec2i nodes;
            float size;
            int layers, version, vertices;
            float quad, min, max;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            scene.GetTerrainNodeData(0, 0, out vertices, out quad, out min, out max);
            double sum = 0, curvature = 0;
            double maxAdjacentDelta = 0, maxCurvature = 0;
            var sampleBytes = new MemoryStream();
            long samples = 0, interior = 0;
            float sampledMin = float.PositiveInfinity, sampledMax = float.NegativeInfinity;
            if (nodes.X < 1 || nodes.Y < 1 || nodes.X > 16 || nodes.Y > 16) throw new InvalidOperationException("Unsupported terrain size.");
            for (int x = 0; x < nodes.X; x++) for (int y = 0; y < nodes.Y; y++)
            {
                int count; float step, low, high;
                scene.GetTerrainNodeData(x, y, out count, out step, out low, out high);
                min = Math.Min(min, low); max = Math.Max(max, high);
                if (count < 2 || count > 257) throw new InvalidOperationException("Unsupported terrain resolution.");
                float[] heights = scene.GetTerrainHeightData(x, y);
                if (heights.Length != count * count) throw new InvalidOperationException("Unexpected terrain height sample count.");
                byte[] bytes = new byte[heights.Length * sizeof(float)];
                Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
                sampleBytes.Write(bytes, 0, bytes.Length);
                foreach (float value in heights) { sampledMin = Math.Min(sampledMin, value); sampledMax = Math.Max(sampledMax, value); sum += value; samples++; }
                for (int row = 0; row < count; row++) for (int col = 0; col < count; col++)
                {
                    int i = row * count + col;
                    if (col > 0) maxAdjacentDelta = Math.Max(maxAdjacentDelta, Math.Abs(heights[i] - heights[i-1]));
                    if (row > 0) maxAdjacentDelta = Math.Max(maxAdjacentDelta, Math.Abs(heights[i] - heights[i-count]));
                }
                for (int row = 1; row < count - 1; row++) for (int col = 1; col < count - 1; col++)
                {
                    int i = row * count + col;
                    double laplacian = (heights[i-1] + heights[i+1] + heights[i-count] + heights[i+count]) * 0.25 - heights[i];
                    maxCurvature = Math.Max(maxCurvature, Math.Abs(laplacian));
                    curvature += laplacian * laplacian; interior++;
                }
            }
            string heightHash;
            using (sampleBytes)
            using (var sha = SHA256.Create()) heightHash = BitConverter.ToString(sha.ComputeHash(sampleBytes.ToArray())).Replace("-", "");
            bool? editorAndRenderTerrainMatch = null;
            if (supported)
            {
                IntPtr manager = Marshal.ReadIntPtr(Address(0x18defa0));
                IntPtr editorTerrain = manager == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(manager, 0x10d0);
                IntPtr nativeScene = manager == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(manager, 0x10c0);
                IntPtr renderTerrain = nativeScene == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(nativeScene, 0x99a50);
                editorAndRenderTerrainMatch = editorTerrain != IntPtr.Zero && editorTerrain == renderTerrain;
            }
            return new { containsTerrain = true, generationSupported = supported, diagnostic,
                nodesX = nodes.X, nodesY = nodes.Y, nodeSize = size,
                width = nodes.X * size, length = nodes.Y * size, layerCount = layers,
                nodeVertices = vertices, nodeDimension = vertices - 1, quadLength = quad,
                minElevation = min, maxElevation = max, sampledMinElevation = sampledMin, sampledMaxElevation = sampledMax,
                meanElevation = sum / samples, heightSampleCount = samples, rmsCurvature = Math.Sqrt(curvature / Math.Max(1, interior)),
                maxAdjacentDelta, maxCurvature, heightHash, editorAndRenderTerrainMatch };
        }

        public static object Generate(Scene scene, int nodesX, int nodesY, int? requestedSize, int? requestedDimension, Action<string> log, string layerTemplate = "default")
        {
            if (scene.ContainsTerrain) throw new InvalidOperationException("The scene already has terrain; refusing to replace it.");
            IntPtr inspector = FindInspector();
            int nodeSize = requestedSize ?? Marshal.ReadInt32(inspector, 0x418);
            int currentLevel = Marshal.ReadInt32(inspector, 0x41c);
            if (currentLevel < 1 || currentLevel > 10) throw new InvalidOperationException("Invalid native terrain resolution.");
            int nodeDimension = requestedDimension ?? (1 << currentLevel);
            if (nodesX < 1 || nodesX > 16 || nodesY < 1 || nodesY > 16 || nodeSize < 2 || nodeSize > 1000 ||
                (nodeDimension != 32 && nodeDimension != 64 && nodeDimension != 128 && nodeDimension != 256) ||
                (long)nodesX * nodesY * (nodeDimension + 1) * (nodeDimension + 1) > 4000000)
                throw new InvalidOperationException("Invalid or oversized terrain parameters.");
            int level = 0;
            for (int n = nodeDimension; n > 1; n >>= 1) level++;
            int[] previous = new int[4];
            for (int i = 0; i < 4; i++) previous[i] = Marshal.ReadInt32(inspector, 0x410 + i * 4);
            try
            {
                Marshal.WriteInt32(inspector, 0x410, nodesX);
                Marshal.WriteInt32(inspector, 0x414, nodesY);
                Marshal.WriteInt32(inspector, 0x418, nodeSize);
                Marshal.WriteInt32(inspector, 0x41c, level);
                using (var template = new NativeQtTemplate(log, layerTemplate))
                {
                    var generate = (GenerateHandler)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(_nativeBase, 0x5b8170), typeof(GenerateHandler));
                    generate(inspector, false);
                    if (!scene.ContainsTerrain) throw new InvalidOperationException(template.Diagnostic ?? "The native terrain handler did not create terrain. Inspect before retrying.");
                }
            }
            finally
            {
                if (!scene.ContainsTerrain)
                    for (int i = 0; i < 4; i++) Marshal.WriteInt32(inspector, 0x410 + i * 4, previous[i]);
            }
            if (!scene.ContainsTerrain) throw new InvalidOperationException("The native terrain handler did not create terrain. Inspect before retrying.");
            Vec2i actual;
            float actualSize;
            int layers, version;
            scene.GetTerrainData(out actual, out actualSize, out layers, out version);
            if (actual.X != nodesX || actual.Y != nodesY || Math.Abs(actualSize - nodeSize) > 0.001f)
                throw new InvalidOperationException("Created terrain dimensions differ from the request. Inspect before retrying.");
            return Read(scene);
        }
    }
}
