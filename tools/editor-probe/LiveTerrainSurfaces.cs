using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Path = System.IO.Path;

namespace BannerlordSage.EditorProbe.Live
{
    public static partial class TerrainLayers
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SurfaceConstructor(IntPtr material);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SurfaceRead(IntPtr material, IntPtr sourcePath, IntPtr xmlElement, int season);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void XmlParse(IntPtr document, IntPtr mutableText);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CopyText(IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindTemplate(IntPtr head, out IntPtr result, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InsertTemplate(IntPtr head, out IntPtr result, IntPtr unused, IntPtr hint, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateSurfaceLayer(IntPtr terrain, IntPtr template, IntPtr unused, IntPtr name, IntPtr material);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeColourBrush(IntPtr terrain, int slot, ref TaleWorlds.Library.Vec3 position,
            IntPtr unused4, IntPtr unused5, float radius, IntPtr unused7, IntPtr unused8, IntPtr unused9, IntPtr unused10, IntPtr unused11,
            ref int mode, [MarshalAs(UnmanagedType.I1)] bool allLayers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeBrushFinish(IntPtr controller, int slot);
        private static readonly Dictionary<string, string> RegisteredSurfaceHashes = SurfaceHashOwnership();
        private static Dictionary<string, string> SurfaceHashOwnership()
        {
            const string key = "BannerlordSage.SurfaceTemplateHashes";
            var shared = AppDomain.CurrentDomain.GetData(key) as Dictionary<string, string>;
            if (shared == null)
            {
                shared = new Dictionary<string, string>();
                // Migrate ownership from an earlier fixed companion generation;
                // all descriptors remain owned by the native library meanwhile.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly == typeof(TerrainLayers).Assembly) continue;
                    Type type = assembly.GetType(typeof(TerrainLayers).FullName, false);
                    var field = type == null ? null : type.GetField("RegisteredSurfaceHashes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var previous = field == null ? null : field.GetValue(null) as Dictionary<string, string>;
                    if (previous != null) foreach (var pair in previous) shared[pair.Key] = pair.Value;
                }
                AppDomain.CurrentDomain.SetData(key, shared);
            }
            return shared;
        }

        private static void TouchNativeSurfaceCaches(Context c)
        {
            TaleWorlds.Library.Vec2i dimensions; float nodeSize; int layerCount, version;
            c.Scene.GetTerrainData(out dimensions, out nodeSize, out layerCount, out version);
            if (dimensions.X < 1 || dimensions.Y < 1 || dimensions.X * dimensions.Y > 16 ||
                float.IsNaN(nodeSize) || float.IsInfinity(nodeSize) || nodeSize <= 0 || layerCount != c.Layers.Count)
                throw new InvalidOperationException("Unsupported terrain for native cache touch.");
            var centres = new List<TaleWorlds.Library.Vec3>(); int selected = -1;
            // Select one common layer with an entirely zero brush footprint in
            // every node. Preflight all nodes before invoking any native brush.
            for (int layer = c.Layers.Count - 1; layer >= 1 && selected < 0; layer--)
            {
                centres.Clear(); int slot = layer + 1;
                for (int x = 0; x < dimensions.X; x++) for (int y = 0; y < dimensions.Y; y++)
                {
                    IntPtr node = c.Fn<EditableNode>(0x343c80)(c.Terrain, x, y);
                    int w = Marshal.ReadInt32(node, 0x8b8 + slot * 8), h = Marshal.ReadInt32(node, 0x8bc + slot * 8);
                    if (w < 3 || h < 3 || w > 1025 || h > 1025) throw new InvalidOperationException("Unsupported cache-touch weightmap dimensions.");
                    IntPtr data = Ptr(node, 0x948 + slot * 8);
                    float sx = nodeSize / (w - 1), sy = nodeSize / (h - 1);
                    int mx = (int)Math.Ceiling(1 / sx) + 1, my = (int)Math.Ceiling(1 / sy) + 1;
                    bool found = false; int searched = 0;
                    for (int i = mx; i < w - mx && !found && searched < 4096; i++) for (int j = my; j < h - my && !found && searched < 4096; j++)
                    {
                        searched++;
                        bool zero = true;
                        for (int a = i - mx; a <= i + mx && zero; a++) for (int b = j - my; b <= j + my; b++)
                            if (Marshal.ReadInt32(data, (a * h + b) * 4) != 0) { zero = false; break; }
                        if (!zero) continue;
                        float px = x * nodeSize + i * sx, py = y * nodeSize + j * sy;
                        centres.Add(new TaleWorlds.Library.Vec3(px, py, c.Scene.GetTerrainHeight(new TaleWorlds.Library.Vec2(px, py)))); found = true;
                    }
                }
                if (centres.Count == dimensions.X * dimensions.Y) selected = layer;
            }
            if (selected < 0) throw new InvalidOperationException("No verified zero material footprint exists in every node; cache touch refused.");
            IntPtr manager = Marshal.ReadIntPtr(c.Address(0x18defa0)), controller = IntPtr.Add(manager, 0x460);
            if (Marshal.ReadIntPtr(controller, 0x568) != manager) throw new InvalidOperationException("Cache-touch brush owner mismatch.");
            IntPtr brush = IntPtr.Add(manager, 0x1250);
            byte[] saved = new byte[0x68]; Marshal.Copy(brush, saved, 0, saved.Length);
            int tool = Marshal.ReadInt32(manager, 0x1130), delta = Marshal.ReadInt32(manager, 0x2d4);
            byte upper = Marshal.ReadByte(manager, 0x1459); int applied = 0, mode = 1;
            try
            {
                Marshal.WriteInt32(manager, 0x1130, 1); SurfaceFloat(manager, 0x2d4, .1f); Marshal.WriteByte(manager, 0x1459, 0);
                Marshal.WriteInt32(brush, 0, 1); SurfaceFloat(brush, 4, 0); SurfaceFloat(brush, 8, 1);
                SurfaceFloat(brush, 0xc, .01f); SurfaceFloat(brush, 0x10, 0); SurfaceFloat(brush, 0x14, 0);
                SurfaceFloat(brush, 0x18, 0); Marshal.WriteInt32(brush, 0x1c, -1);
                Marshal.WriteByte(brush, 0x3c, 0); Marshal.WriteByte(brush, 0x48, 0); Marshal.WriteByte(brush, 0x54, 0);
                foreach (var point in centres)
                {
                    var position = point;
                    c.Fn<NativeColourBrush>(0x5a9750)(c.Terrain, selected + 1, ref position, IntPtr.Zero, IntPtr.Zero, 1,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref mode, false);
                    applied++;
                }
            }
            finally
            {
                try { if (applied > 0) c.Fn<NativeBrushFinish>(0x4987e0)(controller, selected + 1); }
                finally
                {
                    Marshal.Copy(saved, 0, brush, saved.Length); Marshal.WriteInt32(manager, 0x1130, tool);
                    Marshal.WriteInt32(manager, 0x2d4, delta); Marshal.WriteByte(manager, 0x1459, upper);
                }
            }
        }

        private sealed class SurfaceText : IDisposable
        {
            public IntPtr Pointer;
            private Context _context;
            public SurfaceText(Context c, string value)
            {
                _context = c;
                byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
                IntPtr chars = Marshal.AllocHGlobal(bytes.Length), source = Marshal.AllocHGlobal(32);
                Pointer = Marshal.AllocHGlobal(32);
                try
                {
                    Marshal.Copy(bytes, 0, chars, bytes.Length); Marshal.Copy(new byte[32], 0, source, 32);
                    Marshal.WriteIntPtr(source, 8, chars); Marshal.WriteInt32(source, 16, bytes.Length - 1);
                    c.Fn<CopyText>(0xe09fd0)(Pointer, source);
                }
                finally { Marshal.FreeHGlobal(chars); Marshal.FreeHGlobal(source); }
            }
            public void Dispose() { _context.Fn<DestroyString>(0xe09bf0)(Pointer, 0); Marshal.FreeHGlobal(Pointer); }
        }

        private static object SurfaceMaterialSnapshot(Context c)
        {
            return new { layers = c.Layers.Select(layer => new { index = layer.Index, name = layer.Name,
                seasons = layer.Materials.Select(material => new { name = String(IntPtr.Add(material.Pointer, 0x388)),
                    smoothBlend = Float(material.Pointer, 0x2c4), elevation = Float(material.Pointer, 0x2a0),
                    parallax = Float(material.Pointer, 0x2ac), slopeScale = Float(material.Pointer, 0x2b8),
                    floraBindings = material.Bindings.Count }).ToArray() }).ToArray() };
        }

        private sealed class SurfaceSource
        {
            public string Name, Template, SourceLayer, Hash;
            public XElement Xml;
            public IntPtr Material;
        }
        private static object ApplySurfacePalette(Context c, JObject plan, string output)
        {
            string[] allowed = { "kind", "operation", "expectedLayers", "expectedFloraRevision" };
            if (plan.Properties().Any(p => !allowed.Contains(p.Name))) throw new InvalidOperationException("Unknown surface palette fields.");
            string operation = plan.Value<string>("operation");
            if (operation != "inspect" && operation != "register" && operation != "create" && operation != "soften_existing") throw new InvalidOperationException("Invalid surface palette operation.");
            if (!JToken.DeepEquals(plan["expectedLayers"], new JArray(c.Layers.Select(l => l.Name))) ||
                plan.Value<string>("expectedFloraRevision") != Snapshot(c).Value<string>("revision"))
                throw new InvalidOperationException("The layer order or flora changed before the surface palette operation.");
            if (operation == "soften_existing") return SoftenExistingSurfaces(c, output);
            if (c.Layers.Count + 5 > 15) throw new InvalidOperationException("Insufficient free terrain layer slots.");
            JObject session = JObject.Parse(File.ReadAllText(Path.Combine(output, "session.json")));
            string native = Path.Combine(session.Value<string>("gameDir"), "Modules", "Native");
            const string scene = "mp_sergeant_map_008";
            string registration = Path.Combine(native, "ModuleData", "Multiplayer", "MultiplayerScenes.xml");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            XDocument registry;
            using (var reader = XmlReader.Create(registration, settings)) registry = XDocument.Load(reader);
            if (!registry.Descendants("Scene").Any(e => (string)e.Attribute("name") == scene)) throw new InvalidOperationException("Official reference scene is not registered.");
            string source = Path.Combine(native, "SceneObj", scene, "scene.xscene");
            XDocument document;
            using (var reader = XmlReader.Create(source, settings)) document = XDocument.Load(reader);
            string[] names = { "Woodland Ground", "Pebble Mix", "Soil Flow", "Forest Humus", "Rock Detail" };
            string[] sourceLayers = { "grass", "stone_grass", "flow", "mud_dark", "rock_detail" };
            var sources = new List<SurfaceSource>();
            for (int i = 0; i < names.Length; i++)
            {
                if (c.Layers.Any(l => l.Name == names[i])) throw new InvalidOperationException("A target palette layer already exists; inspect rather than retrying creation.");
                XElement original = document.Root.Element("terrain").Element("layers").Elements("layer")
                    .Single(l => (string)l.Attribute("name") == sourceLayers[i]).Element("summer");
                var xml = new XElement(original) { Name = "terrain_material" };
                string template = "bs_ref008_" + sourceLayers[i] + "_v1";
                xml.SetAttributeValue("name", template); xml.SetAttributeValue("is_flora_layer", "false");
                xml.SetAttributeValue("smooth_blend_amount", "1.000");
                xml.Elements("meshes").Remove(); xml.Add(new XElement("meshes"));
                if (xml.Element("textures").Elements("texture").Any(t => (string)t.Attribute("type") == "diffusemap" && (string)t.Attribute("name") == "editor_grid_8"))
                    throw new InvalidOperationException("An editor-only reference material cannot be painted.");
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)))).Replace("-", "");
                sources.Add(new SurfaceSource { Name = names[i], SourceLayer = sourceLayers[i], Template = template, Xml = xml, Hash = hash });
            }
            if (operation == "inspect") return new { reference = source, layers = sources.Select(s => new { s.Name, s.SourceLayer, s.Template, s.Hash, xml = s.Xml.ToString() }).ToArray() };
            File.WriteAllText(Path.Combine(output, "surface-palette-reference-backup.json"), new JObject { ["before"] = SurfaceState(c),
                ["flora"] = Snapshot(c), ["materials"] = JArray.FromObject(sources.Select(s => new { s.Name, s.SourceLayer, s.Template, s.Hash, xml = s.Xml.ToString() })) }.ToString());
            foreach (SurfaceSource item in sources) item.Material = RegisterSurfaceTemplate(c, item, source);
            if (operation == "register") return new { registered = true, sceneUnchanged = true, templates = sources.Select(s => s.Template).ToArray() };
            int before = c.Layers.Count;
            for (int i = 0; i < sources.Count; i++)
            {
                SurfaceSource item = sources[i];
                using (var template = new SurfaceText(c, item.Template)) using (var name = new SurfaceText(c, item.Name))
                {
                    if (c.Fn<CreateSurfaceLayer>(0x5b0210)(c.Terrain, template.Pointer, IntPtr.Zero, name.Pointer, item.Material) == IntPtr.Zero ||
                        Marshal.ReadInt32(c.Terrain, 0x78) != before + i + 1)
                        throw new InvalidOperationException("Surface palette creation partially failed; do not retry without inspecting layers.");
                }
            }
            c.Fn<Handler>(0x5bb4f0)(c.Inspector); c.Fn<Handler>(0x5bc0d0)(c.Inspector);
            Marshal.WriteInt32(c.NativeScene, 0x2c0c, Marshal.ReadInt32(c.NativeScene, 0x2c0c) + 1);
            return new { created = true, added = names, layerCount = before + sources.Count, reference = source };
        }

        private static object SoftenExistingSurfaces(Context c, string output)
        {
            string[] targets = { "Meadow Blend", "Forest Floor", "Road" };
            foreach (string name in targets) if (c.Layers.Count(l => l.Name == name) != 1) throw new InvalidOperationException("The expected surface layer is missing.");
            JObject before = SurfaceState(c);
            File.WriteAllText(Path.Combine(output, "surface-blending-parameters-before.json"), JObject.FromObject(SurfaceMaterialSnapshot(c)).ToString());
            foreach (Layer layer in c.Layers.Where(l => targets.Contains(l.Name)))
            {
                foreach (Material material in layer.Materials.Distinct())
                {
                    // Offsets verified against both the engine descriptor reader
                    // and writer and the live values in the saved scene XML.
                    SurfaceFloat(material.Pointer, 0x2a0, 0); Marshal.WriteByte(material.Pointer, 0x2a8, 1);
                    SurfaceFloat(material.Pointer, 0x2ac, layer.Name == "Road" ? 0 : -.08f); Marshal.WriteByte(material.Pointer, 0x2b4, 1);
                    SurfaceFloat(material.Pointer, 0x2b8, 1); Marshal.WriteByte(material.Pointer, 0x2c0, 1);
                    SurfaceFloat(material.Pointer, 0x2c4, 1); Marshal.WriteByte(material.Pointer, 0x2cc, 1);
                }
                c.Fn<Handler>(0x93c550)(Ptr(c.Terrain, 0x80 + layer.Index * 8));
            }
            c.Fn<Handler>(0x5bbc80)(c.Inspector);
            RefreshSurface(c);
            if (!JToken.DeepEquals(before, SurfaceState(c))) throw new InvalidOperationException("Material parameter edit changed weight or height buffers.");
            Marshal.WriteInt32(c.NativeScene, 0x2c0c, Marshal.ReadInt32(c.NativeScene, 0x2c0c) + 1);
            return new { updated = targets, buffersUnchanged = true, surface = SurfaceMaterialSnapshot(c) };
        }
        private static void SurfaceFloat(IntPtr p, int offset, float value) { Marshal.WriteInt32(p, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0)); }

        private static IntPtr RegisterSurfaceTemplate(Context c, SurfaceSource item, string source)
        {
            IntPtr head = Marshal.ReadIntPtr(c.Address(0x18def78)), node;
            if (head == IntPtr.Zero) throw new InvalidOperationException("Terrain material library is unavailable.");
            using (var key = new SurfaceText(c, item.Template))
            {
                c.Fn<FindTemplate>(0x05f780)(head, out node, key.Pointer);
                if (node != head)
                {
                    string hash;
                    if (!RegisteredSurfaceHashes.TryGetValue(item.Template, out hash) || hash != item.Hash)
                        throw new InvalidOperationException("Reference template already exists with unverified ownership.");
                    return Ptr(node, 0x40);
                }
                // Same fixed RapidXML document/pool layout and native descriptor
                // parser as the engine's template loader. No library reload/clear.
                IntPtr doc = Marshal.AllocHGlobal(0x3090), text = IntPtr.Zero, material = IntPtr.Zero;
                try
                {
                    Marshal.Copy(new byte[0x3090], 0, doc, 0x3090);
                    IntPtr poolStart = IntPtr.Add(doc, 0x78);
                    Marshal.WriteIntPtr(doc, 0x60, poolStart); Marshal.WriteIntPtr(doc, 0x68, poolStart);
                    Marshal.WriteIntPtr(doc, 0x70, IntPtr.Add(doc, 0x3078));
                    Marshal.WriteIntPtr(doc, 0x3078, c.Address(0x64b30)); Marshal.WriteIntPtr(doc, 0x3080, c.Address(0x64b50));
                    byte[] bytes = Encoding.UTF8.GetBytes(item.Xml.ToString(SaveOptions.DisableFormatting) + "\0");
                    text = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, text, bytes.Length);
                    c.Fn<XmlParse>(0x38d7a0)(doc, text);
                    IntPtr element = Ptr(doc, 0x30);
                    int nameLength = Marshal.ReadInt32(element, 0x10);
                    if (nameLength != 16 || Marshal.PtrToStringAnsi(Ptr(element, 0), nameLength) != "terrain_material")
                        throw new InvalidOperationException("Native XML document did not parse the material element.");
                    material = c.Alloc(0x3d8); Marshal.Copy(new byte[0x3d8], 0, material, 0x3d8);
                    c.Fn<SurfaceConstructor>(0x396fa0)(material);
                    using (var path = new SurfaceText(c, source.Replace('\\', '/')))
                        c.Fn<SurfaceRead>(0x397a30)(material, path.Pointer, element, 0);
                    if (String(IntPtr.Add(material, 0x388)) != item.Template || Marshal.ReadInt32(material, 0x380) == 0 ||
                        Count(IntPtr.Add(material, 0x360), 8, 64) != 0 || Math.Abs(Float(material, 0x2c4) - 1) > .0001f)
                        throw new InvalidOperationException("Native material descriptor did not match its validated reference.");
                    c.Fn<InsertTemplate>(0x1fcb60)(head, out node, IntPtr.Zero, head, key.Pointer);
                    if (node == head || Marshal.ReadIntPtr(node, 0x40) != IntPtr.Zero) throw new InvalidOperationException("Native template insertion did not produce an empty owned slot.");
                    Marshal.WriteIntPtr(node, 0x40, material);
                    RegisteredSurfaceHashes.Add(item.Template, item.Hash);
                    return material; // The engine library now owns this descriptor.
                }
                finally
                {
                    c.Fn<Handler>(0x05dba0)(IntPtr.Add(doc, 0x60));
                    if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
                    Marshal.FreeHGlobal(doc);
                    // A failed initialized descriptor is retained for diagnosis;
                    // freeing its header without releasing texture refs is unsafe.
                }
            }
        }
    }
}
