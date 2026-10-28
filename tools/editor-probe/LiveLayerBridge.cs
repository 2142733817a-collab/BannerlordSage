using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using TaleWorlds.Engine;
using Path = System.IO.Path;

namespace BannerlordSage.EditorProbe
{
    // The fixed local companion can be rebuilt without unloading the editor.
    // Requests cannot choose an assembly, type, method, or executable path.
    internal static class LiveLayerBridge
    {
        private static string _hash;
        private static MethodInfo _execute;

        public static object Execute(string action, Scene scene, JObject args, string output)
        {
            NativeTerrain.FindInspector();
            string path = Path.Combine(output, "BannerlordSage.EditorProbe.Live.dll");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 1024 || file.Length > 2 * 1024 * 1024 ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The local live-layer companion is missing or invalid. Install the compiled companion before editing layers.");
            byte[] bytes = File.ReadAllBytes(path);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes));
            if (hash != _hash)
            {
                Type entry = Assembly.Load(bytes).GetType("BannerlordSage.EditorProbe.Live.TerrainLayers", true);
                _execute = entry.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (_execute == null) throw new InvalidOperationException("Invalid live-layer companion entry point.");
                _hash = hash;
            }
            try
            {
                return _execute.Invoke(null, new object[] { action, scene, args, output,
                    new Func<int, IntPtr>(NativeTerrain.Address), new Func<IntPtr>(NativeTerrain.FindInspector) });
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }
    }
}
