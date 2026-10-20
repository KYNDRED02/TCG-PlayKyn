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
    [BepInPlugin(GUID, "PokeTCG Play", "0.4.0")]
    [BepInDependency("EnhancedPrefabLoader.API", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "duck2.poketcgplay";
        public static ManualLogSource Log;
        public static string DataDir;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> RequireOwnedCards;
        internal static ConfigEntry<bool> FreeBasicEnergy;
        internal static ConfigEntry<string> DefaultFormat;
        internal static ConfigEntry<string> ActiveDeck;

        void Awake()
        {
            Log = Logger;
            DataDir = Path.Combine(Path.GetDirectoryName(Info.Location), "Data");
            ToggleKey = Config.Bind("General", "DebugToggleKey", new KeyboardShortcut(KeyCode.F8), "Tecla opcional de depuración.");
            RequireOwnedCards = Config.Bind("Coleccion", "RequireOwnedCards", true, "Solo permite usar en los mazos las cartas que tienes en tu álbum (obtenidas en sobres).");
            FreeBasicEnergy = Config.Bind("Coleccion", "FreeBasicEnergy", true, "Las Energías Básicas no necesitan estar en tu colección.");
            DefaultFormat = Config.Bind("Mazos", "DefaultFormat", "base1", "Formato por defecto (id de Data/formats/*.json).");
            ActiveDeck = Config.Bind("Mazos", "ActiveDeck", "", "Nombre del mazo que usas al jugar.");
            try
            {
                string pluginsDir = Path.GetDirectoryName(Path.GetDirectoryName(Info.Location));
                CardDatabase.Load(DataDir, pluginsDir);
                FormatRegistry.Load(DataDir);
                DeckStore.MigrateLegacy(DataDir);
                Log.LogInfo($"Cargadas {CardDatabase.All.Count} cartas de {CardDatabase.Sets.Count} expansiones; formatos: {string.Join(", ", FormatRegistry.Formats.Select(f => f.Name))}; mazos: {DeckStore.List().Count}");
            }
            catch (Exception e) { Log.LogError("Error cargando datos: " + e); }

            try
            {
                var h = new Harmony(GUID);
                h.PatchAll(typeof(PlayTablePatches));
                h.PatchAll(typeof(WorkbenchPatches));
                Log.LogInfo("Parches de Mesa de Juego y Mesa de Creación (Workbench) aplicados.");
            }
            catch (Exception e) { Log.LogError("Error aplicando parches Harmony: " + e); }

            gameObject.AddComponent<GameUI>();
            gameObject.AddComponent<DeckBuilderUI>();
        }
    }

    /// <summary>
    /// Carga y retiene en caché los sprites de MyCurio (BS-001..BS-102 y CardBack) más las texturas de Playmat y Moneda.
    /// </summary>
    public static class SpriteCache
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> refIncremented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string bundlePath;
        static bool bundlePathSearched;
        static bool eplHooked;
        static bool customTexturesLoaded;

        public static Texture2D PlaymatTex { get; private set; }
        public static Texture2D CoinHeadsTex { get; private set; }
        public static Texture2D CoinTailsTex { get; private set; }

        static readonly MethodInfo getAsyncMethod =
            typeof(StreamingAsset<Sprite>).GetMethod("GetAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo assetKeyProp =
            typeof(StreamingAsset<Sprite>).GetProperty("AssetKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static object assetRefTracker;
        static MethodInfo incrementRefMethod;

        public static int Count => cache.Count;

        static void LoadCustomTextures()
        {
            if (customTexturesLoaded) return;
            customTexturesLoaded = true;
            PlaymatTex = LoadPng(Path.Combine(Plugin.DataDir, "images", "playmat.png"));
            CoinHeadsTex = LoadPng(Path.Combine(Plugin.DataDir, "images", "coin_heads.png"));
            CoinTailsTex = LoadPng(Path.Combine(Plugin.DataDir, "images", "coin_tails.png"));
        }

        static Texture2D LoadPng(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(tex, bytes);
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"LoadPng ({path}): {e.Message}");
                return null;
            }
        }

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
            LoadCustomTextures();
            if (!eplHooked && Epl.IsAvailable && Epl.Api != null)
            {
                eplHooked = true;
                if (Epl.Api.IsBundleLoadingComplete) PreloadFromEpl();
                else Epl.Api.Events.OnBundleLoadingComplete += PreloadFromEpl;
            }
        }

        /// <summary>Bundle del reverso de carta: el de la primera expansión con MyCurio (Base Set).</summary>
        static string FindBundlePath()
        {
            if (bundlePathSearched) return bundlePath;
            bundlePathSearched = true;
            foreach (var s in CardDatabase.Sets)
                if (!string.IsNullOrEmpty(s.MyCurioExpansion) && MyCurioIndex.Expansions.TryGetValue(s.MyCurioExpansion, out var e) && e.BundlePath != null)
                    return bundlePath = e.BundlePath;
            return bundlePath = MyCurioIndex.Expansions.Values.Select(e => e.BundlePath).FirstOrDefault(p => p != null);
        }

        /// <summary>Precarga las imágenes de todas las cartas registradas, desde los bundles de MyCurio.</summary>
        public static void PreloadFromEpl()
        {
            LoadCustomTextures();
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Epl.IsAvailable && Epl.Api?.Assets?.CardExpansions != null)
                {
                    bool backDone = false;
                    foreach (var exKey in CardDatabase.Sets.Select(s => s.MyCurioExpansion).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                    {
                        if (!Collection.TryParseExpansion(exKey, out var expType)) continue;
                        if (!backDone)
                        {
                            var cb = Epl.Api.Assets.CardExpansions.GetCardExpansionCardBack(expType);
                            if (cb != null && !string.IsNullOrEmpty(cb.BundlePath))
                            {
                                RequestStreamingAsset(new StreamingAsset<Sprite>(cb.BundlePath, string.IsNullOrEmpty(cb.AssetName) ? "CardBack" : cb.AssetName), "CardBack");
                                backDone = true;
                            }
                        }
                        var dict = Epl.Api.Assets.CardExpansions.GetCardSprites(expType);
                        if (dict == null) continue;
                        foreach (var kv in dict)
                        {
                            var sa = kv.Value;
                            if (sa == null || string.IsNullOrEmpty(sa.AssetName) || !CardDatabase.SpriteBundle.ContainsKey(sa.AssetName)) continue;
                            RequestStreamingAsset(sa);
                            requested.Add(sa.AssetName);
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("PreloadFromEpl: " + e.Message); }

            // Respaldo: pedir directamente al bundle de MyCurio lo que EPL no devolvió
            string back = FindBundlePath();
            if (!string.IsNullOrEmpty(back)) RequestStreamingAsset(new StreamingAsset<Sprite>(back, "CardBack"));
            foreach (var kv in CardDatabase.SpriteBundle)
                if (!requested.Contains(kv.Key))
                    RequestStreamingAsset(new StreamingAsset<Sprite>(kv.Value, kv.Key));
        }

        static void RequestStreamingAsset(StreamingAsset<Sprite> sa, string cacheKey = null)
        {
            string key = cacheKey ?? sa?.AssetName;
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

            string path = name == "CardBack" ? FindBundlePath() : (CardDatabase.SpriteBundle.TryGetValue(name, out var p) ? p : null);
            if (!string.IsNullOrEmpty(path) && !pending.Contains(name))
                RequestStreamingAsset(new StreamingAsset<Sprite>(path, name));
            return null;
        }

        public static Sprite GetCardBack() => Get("CardBack");
    }
}
