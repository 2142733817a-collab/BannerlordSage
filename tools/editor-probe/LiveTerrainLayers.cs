using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;
using Formatting = Newtonsoft.Json.Formatting;

namespace BannerlordSage.EditorProbe.Live
{
    public static partial class TerrainLayers
    {
        private static readonly string[] Seasons = { "summer", "fall", "winter", "spring" };
        // Native's grass flag describes rendering. The Battanian forest preset
        // also has shrubs, broad leaves and ground stones for its treeless copy.
        private static readonly HashSet<string> GroundCover = new HashSet<string> { "flora_terrain_drygrass", "flora_dry_plant__mix_grass",
            "flora_plant__mix", "flora_green__mix_flower", "free_valley_flower__mix", "shrub__mix", "wheat_group_summer",
            "rock_new_green", "flora_green__mix_big_leafs", "ground_rocks_group" };
        [DllImport("Qt5Widgets.dll", EntryPoint = "?topLevelWidgets@QApplication@@SA?AV?$QList@PEAVQWidget@@@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr TopLevelWidgets(out IntPtr result);
        [DllImport("Qt5Core.dll", EntryPoint = "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ObjectChildren(IntPtr obj);
        [DllImport("Qt5Core.dll", EntryPoint = "?inherits@QObject@@QEBA_NPEBD@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] private static extern bool ObjectInherits(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport("Qt5Core.dll", EntryPoint = "?objectName@QObject@@QEBA?AVQString@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ObjectName(IntPtr obj, out IntPtr result);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?text@QAction@@QEBA?AVQString@@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ActionText(IntPtr obj, out IntPtr result);
        [DllImport("Qt5Widgets.dll", EntryPoint = "?isChecked@QAction@@QEBA_NXZ", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] private static extern bool ActionChecked(IntPtr obj);
        [DllImport("Qt5Core.dll", EntryPoint = "??1QString@@QEAA@XZ", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DeleteQString(ref IntPtr value);
        [DllImport("Qt5Core.dll", EntryPoint = "?dispose@QListData@@SAXPEAUData@1@@Z", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DisposeQtList(IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct QtArgument { public IntPtr Data; public IntPtr Name; }
        [DllImport("Qt5Core.dll", EntryPoint = "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBDW4ConnectionType@Qt@@VQGenericArgument@@333333333@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)] private static extern bool InvokeQt(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string method, int connection,
            QtArgument a0, QtArgument a1, QtArgument a2, QtArgument a3, QtArgument a4, QtArgument a5, QtArgument a6, QtArgument a7, QtArgument a8, QtArgument a9);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Allocate(IntPtr allocator, UIntPtr bytes, byte flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr allocator, IntPtr memory, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NewBinding(IntPtr memory, int layer, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LayerSeason(IntPtr renderer, int layer, int season);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddBinding(IntPtr renderer, IntPtr binding, int season);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Generate(IntPtr renderer, IntPtr terrain, int layer, [MarshalAs(UnmanagedType.I1)] bool allSeasons);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Aggregate(IntPtr renderer, IntPtr terrain, int layers, [MarshalAs(UnmanagedType.I1)] bool onBodies);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClearDraw(IntPtr collection, int layer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Handler(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyString(IntPtr value, byte flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EditableNode(IntPtr terrain, int x, int y);

        private sealed class Binding
        {
            public IntPtr Pointer;
            public byte[] Bytes;
            public JObject Value;
        }
        private sealed class Material
        {
            public IntPtr Pointer, Buffer;
            public byte[] Header;
            public List<Binding> Bindings;
        }
        private sealed class Layer
        {
            public int Index;
            public string Name;
            public IntPtr Info;
            public Material[] Materials = new Material[4];
        }
        private sealed class Context
        {
            public Scene Scene;
            public Func<int, IntPtr> Address;
            public IntPtr Inspector, Terrain, NativeScene, Renderer, Allocator;
            public int CurrentSeason;
            public List<Layer> Layers = new List<Layer>();
            public Dictionary<string, int> Kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<int, string> Names = new Dictionary<int, string>();
            public T Fn<T>(int rva) where T : class { return Marshal.GetDelegateForFunctionPointer(Address(rva), typeof(T)) as T; }
            public IntPtr Alloc(int bytes, byte flags = 3)
            {
                IntPtr value = Fn<Allocate>(0xe08f30)(Allocator, (UIntPtr)(uint)bytes, flags);
                if (value == IntPtr.Zero) throw new InvalidOperationException("Native flora allocation failed.");
                return value;
            }
            public void Release(IntPtr pointer) { if (pointer != IntPtr.Zero) Fn<Free>(0xe098c0)(Allocator, pointer, 0); }
        }
        private sealed class Replacement
        {
            public Material Old;
            public List<Binding> Next;
            public IntPtr Buffer;
            public List<Tuple<Layer, int, List<IntPtr>>> Owners = new List<Tuple<Layer, int, List<IntPtr>>>();
        }

        public static object Execute(string action, Scene scene, JObject args, string output,
            Func<int, IntPtr> address, Func<IntPtr> findInspector)
        {
            if (action == "terrain_flora_info" && args.Value<bool?>("includeNavigation") == true)
            {
                findInspector();
                return ReadNavigation(scene, args.Value<bool?>("navigationVisible"));
            }
            var c = ReadContext(scene, address, findInspector);
            if (action == "terrain_flora_info" && args.Value<bool?>("includeSurfaceMaterials") == true) return SurfaceMaterialSnapshot(c);
            if (action == "terrain_flora_info" && args.Value<bool?>("includeSurfaceSaveGuard") == true) return SurfaceSaveGuard.Status();
            if (action == "terrain_flora_info" && args.Value<bool?>("includeSurfaceState") == true) return SurfaceState(c);
            if (action == "terrain_flora_info") { JObject value = Snapshot(c); value["planDirectory"] = output; return value; }
            if (action == "terrain_flora_refresh")
            {
                if (args["surfaceSaveGuard"] != null)
                {
                    if (args.Properties().Any(p => p.Name != "surfaceSaveGuard")) throw new InvalidOperationException("Invalid save guard fields.");
                    SurfaceSaveGuard.Configure(c, output, findInspector, args.Value<bool>("surfaceSaveGuard"));
                    return SurfaceSaveGuard.Status();
                }
                if (args.Value<bool?>("surfaceRefresh") == true)
                {
                    if (args.Properties().Any(p => p.Name != "surfaceRefresh")) throw new InvalidOperationException("Invalid surface refresh fields.");
                    return RefreshSurface(c);
                }
                if (args.Properties().Any()) throw new InvalidOperationException("Flora refresh takes no arguments.");
                Refresh(c, c.Layers.Select(x => x.Index));
                return new { refreshed = true, live = true, processId = Process.GetCurrentProcess().Id };
            }
            if (action != "terrain_flora_plan" || args.Properties().Any(x => x.Name != "planPath"))
                throw new InvalidOperationException("Invalid live flora action.");
            JObject plan = ReadPlan(args.Value<string>("planPath"), output);
            if (plan.Value<string>("kind") == "surface_palette_v1") return ApplySurfacePalette(c, plan, output);
            return Apply(c, plan, output);
        }

        private static object RefreshSurface(Context c)
        {
            JObject before = SurfaceState(c);
            int sceneDirty = Marshal.ReadInt32(c.NativeScene, 0x2c0c);
            // A palette expansion exposed additional save-time caches not covered
            // by direct texture upload/flush. Use the complete native brush path,
            // with its whole footprint prechecked as positive zero, once per node.
            TouchNativeSurfaceCaches(c);
            JObject after = SurfaceState(c);
            if (!JToken.DeepEquals(before, after)) throw new InvalidOperationException("Surface refresh changed authoritative terrain buffers; inspect before retrying.");
            Marshal.WriteInt32(c.NativeScene, 0x2c0c, sceneDirty);
            return new { refreshed = true, surface = true, buffersUnchanged = true, nodes = ((JArray)after["nodes"]).Count, processId = Process.GetCurrentProcess().Id };
        }

        // Thread timers are dispatched by the editor's existing Windows message
        // pump. Never invoke native scene code from FileSystemWatcher/worker threads.
        private static class SurfaceSaveGuard
        {
            private const string StopKey = "BannerlordSage.SurfaceSaveGuard.Stop";
            private delegate void TimerProc(IntPtr window, uint message, UIntPtr id, uint time);
            [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, TimerProc callback);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
            [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
            [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
            [DllImport("Qt5Widgets.dll", EntryPoint = "?activeModalWidget@QApplication@@SAPEAVQWidget@@XZ", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr ActiveModalWidget();
            private static UIntPtr _timer;
            private static TimerProc _callback;
            private static Context _context;
            private static Func<IntPtr> _inspector;
            private static string[] _files;
            private static string _stamp, _output, _error, _lastRefresh, _sceneName;
            private static uint _thread;
            private static DateTime _changed;
            private static int _polls, _refreshes;
            private static bool _refreshing;
            public static object Status() { return new { enabled = _timer != UIntPtr.Zero, polls = _polls, refreshes = _refreshes,
                lastRefresh = _lastRefresh, error = _error, processId = Process.GetCurrentProcess().Id }; }
            private static string Stamp()
            {
                return string.Join("|", _files.Select(path => { var f = new FileInfo(path); return f.Exists ? f.LastWriteTimeUtc.Ticks + ":" + f.Length : "missing"; }));
            }
            private static void Stop()
            {
                if (_timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, _timer);
                _timer = UIntPtr.Zero;
            }
            public static void Configure(Context c, string output, Func<IntPtr> inspector, bool enabled)
            {
                if (enabled && _error != null) throw new InvalidOperationException("Inspect the previous save-guard failure before resetting it: " + _error);
                if (enabled && _timer != UIntPtr.Zero && _sceneName == c.Scene.GetName() &&
                    _context.Terrain == c.Terrain && _context.NativeScene == c.NativeScene) return;
                // A rebuilt companion must retire the timer from its previous
                // Assembly.Load generation before installing another one.
                var stop = AppDomain.CurrentDomain.GetData(StopKey) as Action;
                if (stop != null) stop();
                Stop();
                if (!enabled) { _error = null; return; }
                JObject session = JObject.Parse(File.ReadAllText(Path.Combine(output, "session.json")));
                string sceneDir = Path.GetFullPath(session.Value<string>("sceneDir") ?? "");
                if (Path.GetFileName(sceneDir) != c.Scene.GetName() || Path.GetFileName(Path.GetDirectoryName(sceneDir)) != "SceneObj")
                    throw new InvalidOperationException("Save guard requires the bound scene directory.");
                string moduleDir = Path.GetDirectoryName(Path.GetDirectoryName(sceneDir));
                _files = new[] { Path.Combine(sceneDir, "terrain.bin"), Path.Combine(sceneDir, "scene.xscene"),
                    Path.Combine(moduleDir, "SceneEditData", c.Scene.GetName(), "terrain_ed.bin") };
                _context = c; _sceneName = c.Scene.GetName(); _inspector = inspector; _output = output; _error = null;
                _stamp = Stamp(); _changed = DateTime.MinValue; _thread = GetCurrentThreadId();
                _callback = Poll;
                _timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 250, _callback);
                if (_timer == UIntPtr.Zero) throw new InvalidOperationException("Could not install the editor save timer.");
                AppDomain.CurrentDomain.SetData(StopKey, new Action(Stop));
            }
            private static void Poll(IntPtr window, uint message, UIntPtr id, uint time)
            {
                try
                {
                    if (_timer == UIntPtr.Zero || id != _timer || _refreshing) return;
                    if (GetCurrentThreadId() != _thread) throw new InvalidOperationException("Save guard thread mismatch.");
                    _polls++;
                    // Remain inert while another scene, an editor modal or a scene
                    // load owns the UI. A save must settle for at least one second.
                    if (!MBEditor.IsEditModeOn || MBEditor._editorScene == null ||
                        MBEditor._editorScene.GetName() != _sceneName || ActiveModalWidget() != IntPtr.Zero) return;
                    if ((GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 || (GetAsyncKeyState(4) & 0x8000) != 0)
                    {
                        // Do not splice a cache transaction into a live paint or
                        // camera gesture. Keep a pending save's idle debounce.
                        if (_changed != DateTime.MinValue) _changed = DateTime.UtcNow;
                        return;
                    }
                    IntPtr manager = Marshal.ReadIntPtr(_context.Address(0x18defa0));
                    if (Marshal.ReadIntPtr(manager, 0x10d0) != _context.Terrain || Marshal.ReadIntPtr(manager, 0x10c0) != _context.NativeScene) return;
                    string stamp = Stamp();
                    if (stamp != _stamp) { _stamp = stamp; _changed = DateTime.UtcNow; return; }
                    if (_changed == DateTime.MinValue || (DateTime.UtcNow - _changed).TotalSeconds < 1) return;
                    foreach (string file in _files) if (File.Exists(file)) using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                    _refreshing = true;
                    try { RefreshSurface(ReadContext(MBEditor._editorScene, _context.Address, _inspector)); }
                    finally { _refreshing = false; }
                    _changed = DateTime.MinValue; _refreshes++; _lastRefresh = DateTime.UtcNow.ToString("o");
                    File.AppendAllText(Path.Combine(_output, "surface-save-guard.jsonl"), JsonConvert.SerializeObject(Status()) + Environment.NewLine);
                }
                catch (IOException) { _changed = DateTime.UtcNow; }
                catch (Exception error)
                {
                    _error = error.Message; Stop();
                    try { File.AppendAllText(Path.Combine(_output, "surface-save-guard.jsonl"), JsonConvert.SerializeObject(Status()) + Environment.NewLine); } catch { }
                }
            }
        }

        private static JObject SurfaceState(Context c)
        {
            int nx = Marshal.ReadInt32(c.Terrain, 0x14), ny = Marshal.ReadInt32(c.Terrain, 0x18);
            if (nx < 1 || ny < 1 || nx > 16 || ny > 16 || nx * ny > 16) throw new InvalidOperationException("Surface state supports at most sixteen terrain nodes.");
            var nodes = new JArray();
            for (int x = 0; x < nx; x++) for (int y = 0; y < ny; y++)
            {
                IntPtr node = c.Fn<EditableNode>(0x343c80)(c.Terrain, x, y);
                if (node == IntPtr.Zero || Marshal.ReadIntPtr(node, 0x10) != c.Terrain) throw new InvalidOperationException("Surface node owner mismatch.");
                var buffers = new JArray();
                for (int slot = 0; slot <= c.Layers.Count; slot++)
                {
                    int width = Marshal.ReadInt32(node, 0x8b8 + slot * 8), height = Marshal.ReadInt32(node, 0x8bc + slot * 8);
                    if (width < 1 || height < 1 || width > 1025 || height > 1025) throw new InvalidOperationException("Surface buffer dimensions are unsupported.");
                    byte[] bytes = new byte[checked(width * height * 4)]; Marshal.Copy(Ptr(node, 0x948 + slot * 8), bytes, 0, bytes.Length);
                    string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    buffers.Add(new JObject { ["name"] = slot == 0 ? "height" : c.Layers[slot - 1].Name, ["width"] = width, ["height"] = height, ["hash"] = hash,
                        ["cornerProbe"] = new JArray(BitConverter.ToSingle(bytes, 0), BitConverter.ToSingle(bytes, Math.Min(bytes.Length - 4, (width + 1) * 4))) });
                }
                nodes.Add(new JObject { ["x"] = x, ["y"] = y, ["buffers"] = buffers });
            }
            return new JObject { ["layerOrder"] = new JArray(c.Layers.Select(l => l.Name)), ["nodes"] = nodes };
        }

        private static string QtText(IntPtr text)
        {
            if (text == IntPtr.Zero) return "";
            int count = Marshal.ReadInt32(text, 4);
            if (count < 0 || count > 4096) throw new InvalidOperationException("Invalid editor Qt text.");
            return Marshal.PtrToStringUni(new IntPtr(text.ToInt64() + Marshal.ReadInt64(text, 16)), count);
        }
        private static IEnumerable<IntPtr> QtList(IntPtr data)
        {
            if (data == IntPtr.Zero) yield break;
            int first = Marshal.ReadInt32(data, 8), end = Marshal.ReadInt32(data, 12);
            if (first < 0 || end < first || end - first > 4096) throw new InvalidOperationException("Invalid editor widget list.");
            for (int i = first; i < end; i++) yield return Marshal.ReadIntPtr(data, 16 + i * 8);
        }
        private static object ReadNavigation(Scene scene, bool? visible)
        {
            int count = scene.GetNavMeshFaceCount();
            if (count < 0 || count > 250000) throw new InvalidOperationException("Navigation study face count is unsupported.");
            var ids = new Dictionary<int, int>(); var sample = new JArray();
            for (int i = 0; i < count; i++)
            {
                int id = scene.GetIdOfNavMeshFace(i); if (!ids.ContainsKey(id)) ids[id] = 0; ids[id]++;
                if (i < 16)
                {
                    var center = new TaleWorlds.Library.Vec3(); scene.GetNavMeshCenterPosition(i, ref center);
                    sample.Add(new JObject { ["index"] = i, ["id"] = id, ["center"] = new JArray(center.x, center.y, center.z) });
                }
            }
            IntPtr roots; TopLevelWidgets(out roots);
            var queue = new Queue<IntPtr>(QtList(roots)); var seen = new HashSet<IntPtr>(); var actions = new JArray();
            // QApplication creates an owned temporary QList, unlike children().
            if (roots != IntPtr.Zero && Marshal.ReadInt32(roots) == 1) DisposeQtList(roots);
            while (queue.Count > 0 && seen.Count < 20000)
            {
                IntPtr obj = queue.Dequeue(); if (obj == IntPtr.Zero || !seen.Add(obj)) continue;
                if (ObjectInherits(obj, "QAction"))
                {
                    IntPtr a, b; ObjectName(obj, out a); ActionText(obj, out b);
                    string name, text;
                    try { name = QtText(a); text = QtText(b); }
                    finally { DeleteQString(ref a); DeleteQString(ref b); }
                    // The observed navigation-mode action is the only callable
                    // target. Requests cannot specify a Qt object or method.
                    if (visible.HasValue && name == (visible.Value ? "navigation_mesh_mode_button" : "select_mode_button"))
                    {
                        var empty = new QtArgument();
                        if (!InvokeQt(obj, "trigger", 0, empty, empty, empty, empty, empty, empty, empty, empty, empty, empty))
                            throw new InvalidOperationException("Could not switch the editor navigation mode.");
                    }
                    if (Regex.IsMatch(name + " " + text, "nav|navigation|mode_button", RegexOptions.IgnoreCase))
                        actions.Add(new JObject { ["name"] = name, ["text"] = text, ["checked"] = ActionChecked(obj) });
                }
                foreach (IntPtr child in QtList(Marshal.ReadIntPtr(ObjectChildren(obj)))) queue.Enqueue(child);
            }
            return new { navigationOnly = true, scene = scene.GetName(), faceCount = count,
                faceIds = ids, sampleFaces = sample, editorActions = actions, visitedWidgets = seen.Count,
                navigationModeRequested = visible };
        }

        private static IntPtr Ptr(IntPtr owner, int offset)
        {
            if (owner == IntPtr.Zero) throw new InvalidOperationException("Missing native flora owner.");
            IntPtr pointer = Marshal.ReadIntPtr(owner, offset);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Missing native flora data.");
            return pointer;
        }
        private static int Count(IntPtr vector, int stride, int maximum)
        {
            long start = Marshal.ReadIntPtr(vector).ToInt64(), end = Marshal.ReadIntPtr(vector, 8).ToInt64(), capacity = Marshal.ReadIntPtr(vector, 16).ToInt64();
            long length = end - start;
            if (length < 0 || length > (long)stride * maximum || length % stride != 0 || capacity < end || capacity - start > (long)stride * maximum * 2)
                throw new InvalidOperationException("Native flora vector does not match the supported layout.");
            return (int)(length / stride);
        }
        private static string String(IntPtr value)
        {
            int count = Marshal.ReadInt32(value, 16);
            if (count < 1 || count > 128) throw new InvalidOperationException("Invalid native flora name.");
            byte[] bytes = new byte[count]; Marshal.Copy(Ptr(value, 8), bytes, 0, count);
            string text = Encoding.UTF8.GetString(bytes);
            if (!Regex.IsMatch(text, "^[A-Za-z][A-Za-z0-9 _-]{0,127}$")) throw new InvalidOperationException("Invalid native flora identifier.");
            return text;
        }
        private static float Float(IntPtr owner, int offset) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(owner, offset)), 0); }
        private static JArray Vector(IntPtr p, int offset) { return new JArray(Float(p, offset), Float(p, offset + 4), Float(p, offset + 8)); }
        private static JObject Describe(Context c, IntPtr p)
        {
            int kind = Marshal.ReadInt32(p); string name;
            if (!c.Names.TryGetValue(kind, out name)) throw new InvalidOperationException("Native flora definition has an unknown kind.");
            return new JObject { ["name"] = name, ["density"] = Float(p, 4), ["seedIndex"] = Marshal.ReadInt32(p, 8),
                ["colonyRadius"] = Float(p, 12), ["colonyThreshold"] = Float(p, 16), ["weightOffset"] = Float(p, 20),
                ["sizeMin"] = Vector(p, 24), ["sizeMax"] = Vector(p, 40), ["albedoMultiplier"] = Vector(p, 56) };
        }
        private static Context ReadContext(Scene scene, Func<int, IntPtr> address, Func<IntPtr> inspector)
        {
            var c = new Context { Scene = scene, Address = address, Inspector = inspector() };
            if (!scene.ContainsTerrain) throw new InvalidOperationException("Live flora editing requires terrain.");
            IntPtr manager = Marshal.ReadIntPtr(address(0x18defa0));
            c.Terrain = Ptr(manager, 0x10d0); c.NativeScene = Ptr(manager, 0x10c0);
            if (Marshal.ReadIntPtr(c.Terrain, 0x68) != c.NativeScene || Marshal.ReadIntPtr(c.NativeScene, 0x99a50) != c.Terrain)
                throw new InvalidOperationException("Live flora scene ownership mismatch.");
            c.Renderer = Ptr(c.NativeScene, 0x780); c.Allocator = Marshal.ReadIntPtr(address(0x18eae00));
            c.CurrentSeason = Marshal.ReadInt32(c.NativeScene, 0x3504);
            if (c.CurrentSeason < 0 || c.CurrentSeason > 3) throw new InvalidOperationException("Invalid active flora season.");
            IntPtr registry = Marshal.ReadIntPtr(address(0x18df048));
            int kinds = Count(IntPtr.Add(registry, 0xd8), 8, 4096);
            IntPtr kindStart = Marshal.ReadIntPtr(registry, 0xd8);
            for (int i = 0; i < kinds; i++)
            {
                IntPtr kind = Ptr(kindStart, i * 8);
                string name = String(IntPtr.Add(kind, 8)); c.Kinds.Add(name, i); c.Names.Add(i, name);
            }
            int layers = Marshal.ReadInt32(c.Terrain, 0x78);
            if (layers < 1 || layers > 15 || Count(IntPtr.Add(c.NativeScene, 0x999f0), 8, 16) != layers)
                throw new InvalidOperationException("Unsupported native terrain layer set.");
            IntPtr layerStart = Marshal.ReadIntPtr(c.NativeScene, 0x999f0);
            var materials = new Dictionary<IntPtr, Material>();
            for (int i = 0; i < layers; i++)
            {
                IntPtr info = Ptr(layerStart, i * 8);
                var layer = new Layer { Index = i, Name = String(IntPtr.Add(info, 0x28)), Info = info };
                for (int season = 0; season < 4; season++)
                {
                    IntPtr material = Ptr(info, 8 + season * 8); Material value;
                    if (!materials.TryGetValue(material, out value))
                    {
                        value = new Material { Pointer = material, Buffer = Marshal.ReadIntPtr(material, 0x360), Header = new byte[24], Bindings = new List<Binding>() };
                        Marshal.Copy(IntPtr.Add(material, 0x360), value.Header, 0, 24);
                        int count = Count(IntPtr.Add(material, 0x360), 8, 64);
                        for (int j = 0; j < count; j++)
                        {
                            IntPtr p = Ptr(value.Buffer, j * 8); byte[] bytes = new byte[72]; Marshal.Copy(p, bytes, 0, 72);
                            value.Bindings.Add(new Binding { Pointer = p, Bytes = bytes, Value = Describe(c, p) });
                        }
                        materials.Add(material, value);
                    }
                    layer.Materials[season] = value;
                }
                c.Layers.Add(layer);
            }
            return c;
        }
        private static JObject Snapshot(Context c)
        {
            var layers = new JArray();
            foreach (Layer layer in c.Layers)
            {
                var seasons = new JObject();
                for (int s = 0; s < 4; s++) seasons[Seasons[s]] = new JArray(layer.Materials[s].Bindings.Select(x => x.Value.DeepClone()));
                layers.Add(new JObject { ["index"] = layer.Index, ["name"] = layer.Name, ["seasons"] = seasons });
            }
            string revision;
            using (var sha = SHA256.Create()) revision = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(layers.ToString(Formatting.None)))).Replace("-", "");
            return new JObject { ["capability"] = "live_terrain_flora_v1", ["processId"] = Process.GetCurrentProcess().Id,
                ["currentSeason"] = Seasons[c.CurrentSeason], ["revision"] = revision, ["layers"] = layers };
        }
        private static JObject ReadPlan(string path, string output)
        {
            string root = Path.GetFullPath(output).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path ?? "");
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Live flora plan must remain inside the bridge output directory.");
            var file = new FileInfo(full);
            if (!file.Exists || file.Length < 2 || file.Length > 262144 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Invalid live flora plan file.");
            for (DirectoryInfo d = file.Directory; d != null && d.FullName.Length >= root.Length - 1; d = d.Parent)
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Live flora plan cannot traverse a reparse point.");
            string json;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var text = new StreamReader(stream, Encoding.UTF8, true))
            {
                char[] chars = new char[262145]; int length = 0, read;
                while (length < chars.Length && (read = text.Read(chars, length, chars.Length - length)) > 0) length += read;
                if (length > 262144) throw new InvalidOperationException("Live flora plan exceeds its size limit.");
                json = new string(chars, 0, length);
            }
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 24 })
            {
                JObject value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidOperationException("Trailing content in live flora plan.");
                return value;
            }
        }
        private static float Number(JObject row, string name, float fallback, float low, float high)
        {
            JToken token = row[name];
            if (token != null && token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new InvalidOperationException("Flora " + name + " must be numeric.");
            double value = token == null ? fallback : token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value < low || value > high)
                throw new InvalidOperationException("Flora " + name + " is outside its supported range.");
            return (float)value;
        }
        private static float[] Numbers(JObject row, string name, float fallback, float low, float high)
        {
            JArray values = row[name] as JArray;
            if (row[name] != null && (values == null || values.Count != 3)) throw new InvalidOperationException("Flora " + name + " requires three components.");
            var result = new float[3];
            for (int i = 0; i < 3; i++) result[i] = values == null ? fallback : Number(new JObject { [name] = values[i] }, name, fallback, low, high);
            return result;
        }
        private static void Put(byte[] bytes, int offset, float value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static Binding Validate(Context c, JObject row, string layer, int index, Dictionary<string, bool> official)
        {
            string[] fields = { "name", "density", "seedIndex", "colonyRadius", "colonyThreshold", "weightOffset", "sizeMin", "sizeMax", "albedoMultiplier" };
            if (row.Properties().Any(x => !fields.Contains(x.Name))) throw new InvalidOperationException("Unknown flora definition field.");
            string name = row.Value<string>("name"); int kind; bool grass;
            if (name == null || !official.TryGetValue(name, out grass) || !c.Kinds.TryGetValue(name, out kind))
                throw new InvalidOperationException("Flora kind is not an installed official Native kind.");
            if (layer == "Grassland" && !grass && !GroundCover.Contains(name)) throw new InvalidOperationException("Grassland may bind verified non-tree ground cover only.");
            float seed = Number(row, "seedIndex", index + 1, 0, 1000000);
            if (seed != Math.Floor(seed)) throw new InvalidOperationException("Flora seedIndex must be an integer.");
            byte[] bytes = new byte[72]; Buffer.BlockCopy(BitConverter.GetBytes(kind), 0, bytes, 0, 4);
            Put(bytes, 4, Number(row, "density", 1, 0, 60)); Buffer.BlockCopy(BitConverter.GetBytes((int)seed), 0, bytes, 8, 4);
            Put(bytes, 12, Number(row, "colonyRadius", 0, 0, 200)); Put(bytes, 16, Number(row, "colonyThreshold", 0, 0, 1));
            Put(bytes, 20, Number(row, "weightOffset", .35f, 0, 1));
            float[] min = Numbers(row, "sizeMin", 1, .05f, 5), max = Numbers(row, "sizeMax", 1, .05f, 5), color = Numbers(row, "albedoMultiplier", 1, 0, 4);
            for (int i = 0; i < 3; i++)
            {
                if (min[i] > max[i]) throw new InvalidOperationException("Flora minimum size exceeds maximum size.");
                Put(bytes, 24 + i * 4, min[i]); Put(bytes, 40 + i * 4, max[i]); Put(bytes, 56 + i * 4, color[i]);
            }
            Put(bytes, 36, float.MaxValue); Put(bytes, 52, float.MaxValue); Put(bytes, 68, float.MaxValue);
            return new Binding { Bytes = bytes };
        }
        private static Dictionary<string, bool> OfficialKinds()
        {
            string path = Path.GetFullPath("../../Modules/Native/ModuleData/flora_kinds.xml");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using (var reader = XmlReader.Create(path, settings))
                return XDocument.Load(reader).Root.Elements("flora_kind").ToDictionary(x => (string)x.Attribute("name"),
                    x => x.Element("flags") != null && x.Element("flags").Elements("flag").Any(f => (string)f.Attribute("name") == "is_grass" && (string)f.Attribute("value") == "true"));
        }
        private static bool Same(List<Binding> a, List<Binding> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < 72; j += 4)
                {
                    if (j == 0 || j == 8) { if (BitConverter.ToInt32(a[i].Bytes, j) != BitConverter.ToInt32(b[i].Bytes, j)) return false; }
                    else if (j != 36 && j != 52 && j != 68 && Math.Abs(BitConverter.ToSingle(a[i].Bytes, j) - BitConverter.ToSingle(b[i].Bytes, j)) > .00001f) return false;
                }
            return true;
        }
        private static object Apply(Context c, JObject plan, string output)
        {
            if (plan.Properties().Any(x => !new[] { "expectedLayers", "expectedRevision", "layers" }.Contains(x.Name)))
                throw new InvalidOperationException("Unknown live flora plan field.");
            JObject before = Snapshot(c);
            if (plan.Value<string>("expectedRevision") != before.Value<string>("revision"))
                throw new InvalidOperationException("Flora state changed. Read current settings and prepare the plan again; nothing was modified.");
            JArray expected = plan["expectedLayers"] as JArray, edits = plan["layers"] as JArray;
            if (expected == null || !expected.Values<string>().SequenceEqual(c.Layers.Select(x => x.Name)) || edits == null || edits.Count < 1 || edits.Count > 15)
                throw new InvalidOperationException("Live flora plan does not match the active layer order.");
            var official = OfficialKinds(); var replacements = new Dictionary<IntPtr, Replacement>();
            var requested = new HashSet<string>();
            foreach (JObject edit in edits)
            {
                if (edit.Properties().Any(x => x.Name != "layer" && x.Name != "seasons")) throw new InvalidOperationException("Unknown layer update field.");
                Layer layer = c.Layers.SingleOrDefault(x => x.Name == edit.Value<string>("layer"));
                JObject seasons = edit["seasons"] as JObject;
                if (layer == null || seasons == null || !seasons.Properties().Any()) throw new InvalidOperationException("Unknown layer or empty seasonal edit.");
                foreach (JProperty property in seasons.Properties())
                {
                    int s = Array.IndexOf(Seasons, property.Name); JArray rows = property.Value as JArray;
                    if (s < 0 || rows == null || rows.Count > 64 || !requested.Add(layer.Index + ":" + s)) throw new InvalidOperationException("Invalid or duplicate seasonal flora list.");
                    var values = new List<Binding>(); var names = new HashSet<string>();
                    foreach (JToken token in rows)
                    {
                        JObject row = token as JObject;
                        if (row == null || !names.Add(row.Value<string>("name") ?? "")) throw new InvalidOperationException("Invalid or duplicate flora kind.");
                        values.Add(Validate(c, row, layer.Name, values.Count, official));
                    }
                    Material material = layer.Materials[s]; Replacement replacement;
                    if (!replacements.TryGetValue(material.Pointer, out replacement))
                    { replacement = new Replacement { Old = material, Next = values }; replacements.Add(material.Pointer, replacement); }
                    else if (!Same(replacement.Next, values)) throw new InvalidOperationException("Shared seasonal material requires identical flora definitions.");
                }
            }
            foreach (Layer layer in c.Layers) for (int s = 0; s < 4; s++)
            {
                Replacement replacement;
                if (!replacements.TryGetValue(layer.Materials[s].Pointer, out replacement)) continue;
                if (!requested.Contains(layer.Index + ":" + s) && !Same(replacement.Old.Bindings, replacement.Next))
                    throw new InvalidOperationException("This material is shared by another season or layer; include every owner in the plan.");
                replacement.Owners.Add(Tuple.Create(layer, s, new List<IntPtr>()));
            }
            for (int s = 0; s < 4; s++)
            {
                int count = c.Layers.Sum(l => replacements.ContainsKey(l.Materials[s].Pointer) ? replacements[l.Materials[s].Pointer].Next.Count : l.Materials[s].Bindings.Count);
                if (count > 256) throw new InvalidOperationException("Native seasonal flora layer capacity exceeded.");
            }
            var changes = replacements.Values.Where(x => !Same(x.Old.Bindings, x.Next)).ToList();
            if (changes.Count == 0) return new { applied = true, changed = false, live = true, requiresEditorReload = false, processId = Process.GetCurrentProcess().Id, revision = before.Value<string>("revision") };
            string backup = Path.Combine(output, "live-flora-backup-" + Guid.NewGuid().ToString("N") + ".json");
            var original = new JArray();
            foreach (Layer layer in c.Layers)
            {
                JObject seasons = new JObject();
                for (int s = 0; s < 4; s++) if (changes.Any(r => r.Owners.Any(o => o.Item1 == layer && o.Item2 == s)))
                    seasons[Seasons[s]] = new JArray(layer.Materials[s].Bindings.Select(x => x.Value.DeepClone()));
                if (seasons.Properties().Any()) original.Add(new JObject { ["layer"] = layer.Name, ["seasons"] = seasons });
            }
            File.WriteAllText(backup, new JObject { ["expectedLayers"] = new JArray(c.Layers.Select(x => x.Name)),
                ["expectedRevision"] = before["revision"], ["layers"] = original }.ToString(Formatting.Indented));
            bool mutated = false, installed = false;
            var layerIds = changes.SelectMany(x => x.Owners.Select(o => o.Item1.Index)).Distinct().ToArray();
            try
            {
                // Allocate all replacement metadata before touching active pointers.
                foreach (Replacement change in changes)
                {
                    foreach (Binding binding in change.Next)
                    { binding.Pointer = c.Alloc(72); Marshal.Copy(binding.Bytes, 0, binding.Pointer, 72); }
                    if (change.Next.Count > 0)
                    {
                        change.Buffer = c.Alloc(change.Next.Count * 8, Marshal.ReadByte(change.Old.Pointer, 0x378));
                        for (int i = 0; i < change.Next.Count; i++) Marshal.WriteIntPtr(change.Buffer, i * 8, change.Next[i].Pointer);
                    }
                    foreach (var owner in change.Owners) foreach (Binding binding in change.Next)
                    {
                        IntPtr record = c.Alloc(56);
                        c.Fn<NewBinding>(0x590950)(record, owner.Item1.Index, IntPtr.Add(owner.Item1.Info, 0x28));
                        Marshal.WriteIntPtr(record, 8, binding.Pointer); owner.Item3.Add(record);
                    }
                }
                mutated = true;
                foreach (int id in layerIds) c.Fn<ClearDraw>(0x5a2e30)(IntPtr.Add(c.Renderer, 0x88), id);
                foreach (Replacement change in changes) foreach (var owner in change.Owners)
                    c.Fn<LayerSeason>(0x5993d0)(c.Renderer, owner.Item1.Index, owner.Item2);
                foreach (Replacement change in changes)
                {
                    Marshal.WriteIntPtr(change.Old.Pointer, 0x360, change.Buffer);
                    IntPtr end = IntPtr.Add(change.Buffer, change.Next.Count * 8);
                    Marshal.WriteIntPtr(change.Old.Pointer, 0x368, end); Marshal.WriteIntPtr(change.Old.Pointer, 0x370, end);
                }
                installed = true;
                foreach (Replacement change in changes) foreach (var owner in change.Owners) foreach (IntPtr record in owner.Item3)
                    c.Fn<AddBinding>(0x5986a0)(c.Renderer, record, owner.Item2);
                Refresh(c, layerIds);
                foreach (int id in layerIds) c.Fn<Handler>(0x93c550)(Ptr(c.Terrain, 0x80 + id * 8));
                c.Fn<Handler>(0x5bbc80)(c.Inspector);
                Marshal.WriteInt32(c.NativeScene, 0x2c0c, Marshal.ReadInt32(c.NativeScene, 0x2c0c) + 1);
                // Old records were destroyed by the native removal routine. The
                // material owns its definition pointers and vector allocation.
                var surviving = new HashSet<IntPtr>(c.Layers.SelectMany(l => l.Materials).Where(m => !changes.Any(x => x.Old == m)).SelectMany(m => m.Bindings.Select(b => b.Pointer)));
                var freed = new HashSet<IntPtr>();
                foreach (Replacement change in changes)
                {
                    foreach (Binding binding in change.Old.Bindings)
                        if (!surviving.Contains(binding.Pointer) && freed.Add(binding.Pointer)) c.Release(binding.Pointer);
                    c.Release(change.Old.Buffer);
                }
                return new { applied = true, changed = true, live = true, requiresEditorReload = false,
                    processId = Process.GetCurrentProcess().Id, backupPath = backup, changedLayers = layerIds,
                    refreshComplete = true };
            }
            catch (Exception error)
            {
                if (!mutated)
                {
                    foreach (Replacement change in changes)
                    {
                        foreach (var owner in change.Owners) foreach (IntPtr record in owner.Item3)
                        { c.Fn<DestroyString>(0xe09bf0)(IntPtr.Add(record, 0x10), 0); c.Release(record); }
                        foreach (Binding binding in change.Next) c.Release(binding.Pointer);
                        c.Release(change.Buffer);
                    }
                }
                var failure = new InvalidOperationException("Live flora update failed" + (mutated ? " after native mutation; inspect before retrying. " : " before mutation. ") + error.Message + "; backup: " + backup, error);
                failure.Data["partial"] = mutated; failure.Data["metadataInstalled"] = installed;
                throw failure;
            }
        }
        private static void Refresh(Context c, IEnumerable<int> layerIds)
        {
            // The final aggregate pass covers every terrain layer. Clear their
            // previous draw entries first, retaining free flora (layer -1), so
            // unchanged layers are not appended twice during a partial update.
            foreach (Layer layer in c.Layers) c.Fn<ClearDraw>(0x5a2e30)(IntPtr.Add(c.Renderer, 0x88), layer.Index);
            foreach (int id in layerIds.Distinct()) c.Fn<Generate>(0x598e90)(c.Renderer, c.Terrain, id, false);
            // TerrainLayerInspector's complete update path includes this second
            // phase. Omitting it leaves the normal generated trees undrawn.
            c.Fn<Aggregate>(0x5987c0)(c.Renderer, c.Terrain, c.Layers.Count, Marshal.ReadByte(c.NativeScene, 0x2cbc) != 0);
        }
    }
}
