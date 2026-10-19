using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EnhancedPrefabLoader.API;
using EnhancedPrefabLoader.API.Models;
using UnityEngine;

namespace PokeTCGPlay
{
    [BepInPlugin(GUID, "PokeTCG Play", "0.2.0")]
    [BepInDependency("EnhancedPrefabLoader.API", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "duck2.poketcgplay";
        public static ManualLogSource Log;
        public static string DataDir;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        void Awake()
        {
            Log = Logger;
            DataDir = Path.Combine(Path.GetDirectoryName(Info.Location), "Data");
            ToggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "Abre/cierra la mesa de juego.");
            try
            {
                CardDatabase.Load(DataDir);
                Log.LogInfo($"Cargadas {CardDatabase.ByNumber.Count} cartas; con sprite MyCurio: {CardDatabase.ByNumber.Values.Count(c => c.Sprite != null)}");
            }
            catch (Exception e) { Log.LogError("Error cargando datos: " + e); }
            gameObject.AddComponent<GameUI>();
        }
    }

    /// <summary>
    /// Carga y almacena en caché los sprites de MyCurio (BS-001..BS-102) usando EnhancedPrefabLoader.API
    /// (StreamingAsset&lt;Sprite&gt;.GetAsync) o directamente desde el AssetBundle de MyCurio si aún no están en memoria.
    /// </summary>
    public static class SpriteCache
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string bundlePath;
        static bool bundlePathSearched;
        static bool eplHooked;

        public static int Count => cache.Count;

        public static void EnsureInitialized()
        {
            if (!eplHooked && Epl.IsAvailable && Epl.Api != null)
            {
                eplHooked = true;
                if (Epl.Api.IsBundleLoadingComplete) PreloadFromEpl();
                else Epl.Api.Events.OnBundleLoadingComplete += PreloadFromEpl;
            }
        }

        static string FindBundlePath()
        {
            if (bundlePathSearched) return bundlePath;
            bundlePathSearched = true;
            try
            {
                if (Epl.IsAvailable && Epl.Api?.BundleRegistry?.BundleInfo != null)
                {
                    foreach (var kv in Epl.Api.BundleRegistry.BundleInfo)
                    {
                        if (kv.Value != null &&
                            (string.Equals(kv.Value.Name, "MyCurioPokemon-BS", StringComparison.OrdinalIgnoreCase) ||
                             (kv.Value.BundlePath != null && kv.Value.BundlePath.IndexOf("base01", StringComparison.OrdinalIgnoreCase) >= 0)))
                        {
                            bundlePath = kv.Value.BundlePath;
                            Plugin.Log?.LogInfo($"Bundle MyCurio Base Set detectado via EPL: {bundlePath}");
                            return bundlePath;
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("No se pudo consultar BundleRegistry: " + e.Message); }

            try
            {
                string pluginsDir = Path.GetDirectoryName(Path.GetDirectoryName(Plugin.DataDir));
                var files = Directory.GetFiles(pluginsDir, "mycurio-pokemon-base01", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    bundlePath = files[0];
                    Plugin.Log?.LogInfo($"Bundle MyCurio Base Set encontrado en disco: {bundlePath}");
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Error buscando mycurio-pokemon-base01: " + e.Message); }
            return bundlePath;
        }

        public static void PreloadFromEpl()
        {
            try
            {
                // 1) Intentar mediante el enum ECardExpansionType ("MyCurioPokemon-BS")
                if (Enum.TryParse<ECardExpansionType>("MyCurioPokemon-BS", true, out var expType) && Epl.IsAvailable && Epl.Api?.Assets?.CardExpansions != null)
                {
                    var dict = Epl.Api.Assets.CardExpansions.GetCardSprites(expType);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                        {
                            var sa = kv.Value;
                            if (sa == null || string.IsNullOrEmpty(sa.AssetName)) continue;
                            RequestStreamingAsset(sa);
                        }
                        Plugin.Log?.LogInfo($"Solicitados {dict.Count} sprites a EPL para MyCurioPokemon-BS.");
                        return;
                    }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("PreloadFromEpl (enum) falló: " + e.Message); }

            // 2) Solicitar por StreamingAsset con la ruta del bundle
            string path = FindBundlePath();
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var c in CardDatabase.ByNumber.Values)
                {
                    if (!string.IsNullOrEmpty(c.Sprite))
                        RequestStreamingAsset(new StreamingAsset<Sprite>(path, c.Sprite));
                }
            }
        }

        static readonly System.Reflection.MethodInfo getAsyncMethod =
            typeof(StreamingAsset<Sprite>).GetMethod("GetAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        static void RequestStreamingAsset(StreamingAsset<Sprite> sa)
        {
            string key = sa?.AssetName;
            if (string.IsNullOrEmpty(key) || cache.ContainsKey(key) || !pending.Add(key)) return;
            try
            {
                Action<UnityEngine.Object> cb = obj =>
                {
                    pending.Remove(key);
                    if (obj is Sprite sp && sp != null) cache[key] = sp;
                };
                getAsyncMethod?.Invoke(sa, new object[] { cb });
            }
            catch (Exception e)
            {
                pending.Remove(key);
                Plugin.Log?.LogWarning($"GetAsync falló para {key}: {e.Message}");
            }
        }

        public static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (cache.TryGetValue(name, out var s) && s != null) return s;

            EnsureInitialized();

            string path = FindBundlePath();
            if (!string.IsNullOrEmpty(path) && !pending.Contains(name))
            {
                RequestStreamingAsset(new StreamingAsset<Sprite>(path, name));
            }
            return null;
        }
    }
}
