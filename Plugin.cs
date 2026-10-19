using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EnhancedPrefabLoader.API;
using EnhancedPrefabLoader.API.Models;
using HarmonyLib;
using UnityEngine;

namespace PokeTCGPlay
{
    [BepInPlugin(GUID, "PokeTCG Play", "0.3.0")]
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
            ToggleKey = Config.Bind("General", "DebugToggleKey", new KeyboardShortcut(KeyCode.F8), "Tecla opcional de depuración.");
            try
            {
                CardDatabase.Load(DataDir);
                Log.LogInfo($"Cargadas {CardDatabase.ByNumber.Count} cartas; con sprite MyCurio: {CardDatabase.ByNumber.Values.Count(c => c.Sprite != null)}");
            }
            catch (Exception e) { Log.LogError("Error cargando datos: " + e); }

            try
            {
                Harmony.CreateAndPatchAll(typeof(PlayTablePatches), GUID);
                Log.LogInfo("Parches de InteractablePlayTable aplicados (clic derecho en mesa para sentarse).");
            }
            catch (Exception e) { Log.LogError("Error aplicando parches Harmony: " + e); }

            gameObject.AddComponent<GameUI>();
        }
    }

    /// <summary>
    /// Carga y retiene en caché los sprites de MyCurio (BS-001..BS-102) usando EnhancedPrefabLoader.API
    /// e incrementa el contador de referencias en AssetRefTracker para que EPL no los descargue a los 60s.
    /// </summary>
    public static class SpriteCache
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> refIncremented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string bundlePath;
        static bool bundlePathSearched;
        static bool eplHooked;

        static readonly MethodInfo getAsyncMethod =
            typeof(StreamingAsset<Sprite>).GetMethod("GetAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo assetKeyProp =
            typeof(StreamingAsset<Sprite>).GetProperty("AssetKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static object assetRefTracker;
        static MethodInfo incrementRefMethod;

        public static int Count => cache.Count;

        static void PinAssetKey(StreamingAsset<Sprite> sa)
        {
            try
            {
                string assetKey = assetKeyProp?.GetValue(sa, null) as string;
                if (string.IsNullOrEmpty(assetKey) || !refIncremented.Add(assetKey)) return;

                if (assetRefTracker == null)
                {
                    var bridgeType = typeof(Epl).Assembly.GetType("EnhancedPrefabLoader.API.InternalBridges.StreamingAssetsBridge");
                    var prop = bridgeType?.GetProperty("AssetRefTracker", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    assetRefTracker = prop?.GetValue(null, null);
                    incrementRefMethod = assetRefTracker?.GetType().GetMethod("IncrementRef", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                incrementRefMethod?.Invoke(assetRefTracker, new object[] { assetKey });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("PinAssetKey: " + e.Message);
            }
        }

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
            if (bundlePathSearched && !string.IsNullOrEmpty(bundlePath)) return bundlePath;
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
                            return bundlePath;
                        }
                    }
                }
            }
            catch { }

            try
            {
                string pluginsDir = Path.GetDirectoryName(Path.GetDirectoryName(Plugin.DataDir));
                var files = Directory.GetFiles(pluginsDir, "mycurio-pokemon-base01", SearchOption.AllDirectories);
                if (files.Length > 0) bundlePath = files[0];
            }
            catch { }
            return bundlePath;
        }

        public static void PreloadFromEpl()
        {
            try
            {
                if (Enum.TryParse<ECardExpansionType>("MyCurioPokemon-BS", true, out var expType) && Epl.IsAvailable && Epl.Api?.Assets?.CardExpansions != null)
                {
                    var dict = Epl.Api.Assets.CardExpansions.GetCardSprites(expType);
                    if (dict != null && dict.Count > 0)
                    {
                        foreach (var kv in dict)
                        {
                            var sa = kv.Value;
                            if (sa == null || string.IsNullOrEmpty(sa.AssetName)) continue;
                            RequestStreamingAsset(sa);
                        }
                        return;
                    }
                }
            }
            catch { }

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

        static void RequestStreamingAsset(StreamingAsset<Sprite> sa)
        {
            string key = sa?.AssetName;
            if (string.IsNullOrEmpty(key)) return;
            if (cache.TryGetValue(key, out var existing) && existing != null && existing.texture != null) return;
            if (!pending.Add(key)) return;
            try
            {
                Action<UnityEngine.Object> cb = obj =>
                {
                    pending.Remove(key);
                    if (obj is Sprite sp && sp != null && sp.texture != null)
                    {
                        cache[key] = sp;
                        PinAssetKey(sa);
                    }
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
            if (cache.TryGetValue(name, out var s))
            {
                if (s != null && s.texture != null) return s;
                cache.Remove(name);
            }

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
