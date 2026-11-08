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
        internal static ConfigEntry<string> NpcDeckMode;
        internal static ConfigEntry<float> AnimSpeedMultiplier;
        internal static ConfigEntry<string> LanguageConfig;
        internal static ConfigEntry<float> SfxVolumeConfig;
        internal static ConfigEntry<bool> EnableCameraShake;

        void Awake()
        {
            Log = Logger;
            DataDir = Path.Combine(Path.GetDirectoryName(Info.Location), "Data");
            ToggleKey = Config.Bind("General", "DebugToggleKey", new KeyboardShortcut(KeyCode.F10), "Tecla opcional de depuración (F10 para evitar conflicto con F8 de Pallet Delivery).");
            LanguageConfig = Config.Bind("General", "Language", "ES", "Idioma de la interfaz: ES (Español) o EN (English).");
            AnimSpeedMultiplier = Config.Bind("Pulido", "AnimationSpeed", 1.0f, "Multiplicador de velocidad de animaciones 3D (0.75, 1.0, 1.5, 2.0).");
            SfxVolumeConfig = Config.Bind("Pulido", "SfxVolume", 0.85f, "Volumen de efectos de sonido de Pokémon TCG (0.0 a 1.0).");
            EnableCameraShake = Config.Bind("Pulido", "EnableCameraShake", true, "Activa la sacudida de cámara en Fuera de Combate (KO) e impactos fuertes.");
            RequireOwnedCards = Config.Bind("Coleccion", "RequireOwnedCards", true, "Solo permite usar en los mazos las cartas que tienes en tu álbum (obtenidas en sobres).");
            FreeBasicEnergy = Config.Bind("Coleccion", "FreeBasicEnergy", true, "Las Energías Básicas no necesitan estar en tu colección.");
            DefaultFormat = Config.Bind("Mazos", "DefaultFormat", "base_era", "Formato por defecto (id de Data/formats/*.json).");
            ActiveDeck = Config.Bind("Mazos", "ActiveDeck", "", "Nombre del mazo que usas al jugar.");
            NpcDeckMode = Config.Bind("Mazos", "NpcDeckMode", "ThemeOrRandom", "Mazos que usan los NPCs: ThemeOrRandom (Oficiales + Aleatorios), ThemeOnly (Solo Mazos Oficiales Base Era), RandomOnly (Solo Aleatorios) o el ID/Nombre de un mazo oficial específico.");
            TcgAudio.EnsureInitialized();
            try
            {
                string pluginsDir = Path.GetDirectoryName(Path.GetDirectoryName(Info.Location));
                CardDatabase.Load(DataDir, pluginsDir);
                FormatRegistry.Load(DataDir);
                ThemeDeckRegistry.Load(DataDir);
                DeckStore.MigrateLegacy(DataDir);
                Log.LogInfo($"Cargadas {CardDatabase.All.Count} cartas de {CardDatabase.Sets.Count} expansiones; formatos: {string.Join(", ", FormatRegistry.Formats.Select(f => f.Name))}; mazos oficiales: {ThemeDeckRegistry.Decks.Count}; mazos guardados: {DeckStore.List().Count}");
            }
            catch (Exception e) { Log.LogError("Error cargando datos: " + e); }

            try
            {
                var h = new Harmony(GUID);
                h.PatchAll(typeof(PlayTablePatches));
                h.PatchAll(typeof(WorkbenchPatches));
                PlayTableVarietyCompat.Apply(h);
                Log.LogInfo("Parches de Mesa de Juego y Mesa de Creación (Workbench) aplicados.");
            }
            catch (Exception e) { Log.LogError("Error aplicando parches Harmony: " + e); }

            gameObject.AddComponent<TableView>();
            gameObject.AddComponent<BoardAnimator>();
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

        static readonly MethodInfo getAsyncSpriteMethod =
            typeof(StreamingAsset<Sprite>).GetMethod("GetAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo assetKeySpriteProp =
            typeof(StreamingAsset<Sprite>).GetProperty("AssetKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo getAsyncTexMethod =
            typeof(StreamingAsset<Texture>).GetMethod("GetAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo assetKeyTexProp =
            typeof(StreamingAsset<Texture>).GetProperty("AssetKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
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

        static void PinAssetKey(string assetKey)
        {
            try
            {
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

        public static Texture2D MakeReadableCopy(Texture2D source)
        {
            if (source == null) return null;
            if (source.isReadable)
            {
                return source;
            }
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                readable.Apply();
                return readable;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static Rect DetectActiveCardRect(Texture2D tex, Rect initialRect)
        {
            if (tex == null || initialRect.width <= initialRect.height * 0.80f)
                return initialRect;

            float fallbackW = Mathf.Clamp(Mathf.Round(initialRect.height * (63f / 88f)), 1f, initialRect.width);
            Texture2D readable = null;
            bool createdTemp = false;
            try
            {
                readable = tex.isReadable ? tex : MakeReadableCopy(tex);
                createdTemp = readable != null && !ReferenceEquals(readable, tex);
                if (readable == null)
                    return new Rect(initialRect.x, initialRect.y, fallbackW, initialRect.height);

                Color32[] pixels = readable.GetPixels32();
                int tw = readable.width;
                int th = readable.height;
                int rx0 = Mathf.Clamp(Mathf.RoundToInt(initialRect.x), 0, tw - 1);
                int rx1 = Mathf.Clamp(Mathf.RoundToInt(initialRect.xMax) - 1, rx0, tw - 1);
                int ry0 = Mathf.Clamp(Mathf.RoundToInt(initialRect.y + initialRect.height * 0.22f), 0, th - 1);
                int ry1 = Mathf.Clamp(Mathf.RoundToInt(initialRect.y + initialRect.height * 0.78f), ry0, th - 1);

                // Tomar el color de relleno del margen derecho (en texturas 1024x1024 POT de MyCurio suele ser blanco o transparente)
                Color32 padCol = pixels[(th / 2) * tw + rx1];
                int minActiveX = rx1;
                int maxActiveX = rx0;
                int stepY = Mathf.Max(1, (ry1 - ry0) / 24);

                for (int y = ry0; y <= ry1; y += stepY)
                {
                    int row = y * tw;
                    for (int x = rx0; x <= rx1; x++)
                    {
                        Color32 c = pixels[row + x];
                        if (IsActiveCardPixel(c, padCol))
                        {
                            if (x < minActiveX) minActiveX = x;
                            break;
                        }
                    }
                    for (int x = rx1; x >= rx0; x--)
                    {
                        Color32 c = pixels[row + x];
                        if (IsActiveCardPixel(c, padCol))
                        {
                            if (x > maxActiveX) maxActiveX = x;
                            break;
                        }
                    }
                }

                int detectedW = maxActiveX - minActiveX + 1;
                float minExpectedW = initialRect.height * 0.62f;
                float maxExpectedW = initialRect.height * 0.80f;
                if (detectedW >= minExpectedW && detectedW <= maxExpectedW)
                {
                    return new Rect(minActiveX, initialRect.y, detectedW, initialRect.height);
                }
            }
            catch { }
            finally
            {
                if (createdTemp && readable != null)
                    UnityEngine.Object.Destroy(readable);
            }

            return new Rect(initialRect.x, initialRect.y, fallbackW, initialRect.height);
        }

        static bool IsActiveCardPixel(Color32 c, Color32 pad)
        {
            if (pad.a < 20)
                return c.a > 40;
            int diff = Math.Abs(c.r - pad.r) + Math.Abs(c.g - pad.g) + Math.Abs(c.b - pad.b);
            return diff > 28;
        }

        static Sprite ConvertObjectToSprite(UnityEngine.Object obj, bool trimCardAspect = false)
        {
            if (obj is Sprite sp && sp != null && sp.texture != null)
            {
                Rect sr = sp.textureRect;
                if (trimCardAspect && sr.width > sr.height * 0.80f)
                {
                    Rect cropped = DetectActiveCardRect(sp.texture, sr);
                    var trimmed = Sprite.Create(sp.texture, cropped, new Vector2(0.5f, 0.5f), 100f);
                    trimmed.name = sp.name;
                    return trimmed;
                }
                return sp;
            }
            if (obj is Texture2D tex && tex != null)
            {
                Rect full = new Rect(0f, 0f, tex.width, tex.height);
                if (trimCardAspect)
                    full = DetectActiveCardRect(tex, full);
                var created = Sprite.Create(tex, full, new Vector2(0.5f, 0.5f), 100f);
                created.name = tex.name;
                return created;
            }
            return null;
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

        /// <summary>Precarga las imágenes de todas las cartas registradas, desde los bundles de MyCurio (compatible con EPL 6.7.1 / API 6.5.0).</summary>
        public static void PreloadFromEpl()
        {
            LoadCustomTextures();
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Epl.IsAvailable && Epl.Api?.Assets?.CardExpansions != null)
                {
                    bool backDone = false;
                    var allExKeys = CardDatabase.Sets.Select(s => s.MyCurioExpansion)
                        .Concat(CardDatabase.All.Select(c => c.MyCurioExpansion))
                        .Where(x => !string.IsNullOrEmpty(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var exKey in allExKeys)
                    {
                        if (!Collection.TryParseExpansion(exKey, out var expType)) continue;
                        if (!backDone)
                        {
                            var cb = Epl.Api.Assets.CardExpansions.GetCardExpansionCardBack(expType);
                            if (cb != null && !string.IsNullOrEmpty(cb.BundlePath))
                            {
                                RequestCardBackAsset(cb);
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
            if (!string.IsNullOrEmpty(back))
                RequestStreamingAsset(new StreamingAsset<Sprite>(back, "CardBack"), "CardBack");
            foreach (var kv in CardDatabase.SpriteBundle)
                if (!requested.Contains(kv.Key))
                    RequestStreamingAsset(new StreamingAsset<Sprite>(kv.Value, kv.Key));
        }

        static void RequestCardBackAsset(StreamingAsset<Texture> cbAsset)
        {
            const string key = "CardBack";
            if (cbAsset == null || string.IsNullOrEmpty(cbAsset.BundlePath)) return;
            if (cache.TryGetValue(key, out var existing) && existing != null && existing.texture != null) return;
            if (!pending.Add(key)) return;

            string bundle = cbAsset.BundlePath;
            string assetName = string.IsNullOrEmpty(cbAsset.AssetName) ? "CardBack" : cbAsset.AssetName;
            try
            {
                // Pedir primero como Sprite (tiene el rectángulo recortado exacto del reverso en los bundles de MyCurio)
                var spriteAsset = new StreamingAsset<Sprite>(bundle, assetName);
                Action<UnityEngine.Object> onSp = objSp =>
                {
                    var sp = ConvertObjectToSprite(objSp, trimCardAspect: true);
                    if (sp != null)
                    {
                        pending.Remove(key);
                        cache[key] = sp;
                        PinAssetKey(assetKeySpriteProp?.GetValue(spriteAsset, null) as string);
                        return;
                    }
                    if (getAsyncTexMethod == null)
                    {
                        pending.Remove(key);
                        return;
                    }
                    Action<UnityEngine.Object> onTex = objTex =>
                    {
                        pending.Remove(key);
                        var spTex = ConvertObjectToSprite(objTex, trimCardAspect: true);
                        if (spTex != null)
                        {
                            cache[key] = spTex;
                            PinAssetKey(assetKeyTexProp?.GetValue(cbAsset, null) as string);
                        }
                    };
                    getAsyncTexMethod.Invoke(cbAsset, new object[] { onTex });
                };

                if (getAsyncSpriteMethod != null)
                    getAsyncSpriteMethod.Invoke(spriteAsset, new object[] { onSp });
                else
                    onSp(null);
            }
            catch (Exception e)
            {
                pending.Remove(key);
                Plugin.Log?.LogWarning($"GetAsync falló para CardBack: {e.Message}");
            }
        }

        static void RequestStreamingAsset(StreamingAsset<Sprite> sa, string cacheKey = null)
        {
            string key = cacheKey ?? sa?.AssetName;
            if (sa == null || string.IsNullOrEmpty(key) || getAsyncSpriteMethod == null) return;
            if (cache.TryGetValue(key, out var existing) && existing != null && existing.texture != null) return;
            if (!pending.Add(key)) return;
            bool isBack = string.Equals(key, "CardBack", StringComparison.OrdinalIgnoreCase);
            try
            {
                Action<UnityEngine.Object> onSp = obj =>
                {
                    var sp = ConvertObjectToSprite(obj, trimCardAspect: isBack);
                    if (sp != null)
                    {
                        pending.Remove(key);
                        cache[key] = sp;
                        PinAssetKey(assetKeySpriteProp?.GetValue(sa, null) as string);
                    }
                    else if (isBack && getAsyncTexMethod != null)
                    {
                        var texSa = new StreamingAsset<Texture>(sa.BundlePath, sa.AssetName);
                        Action<UnityEngine.Object> onTex = objTex =>
                        {
                            pending.Remove(key);
                            var spTex = ConvertObjectToSprite(objTex, trimCardAspect: true);
                            if (spTex != null)
                            {
                                cache[key] = spTex;
                                PinAssetKey(assetKeyTexProp?.GetValue(texSa, null) as string);
                            }
                        };
                        getAsyncTexMethod.Invoke(texSa, new object[] { onTex });
                    }
                    else
                    {
                        pending.Remove(key);
                    }
                };
                getAsyncSpriteMethod.Invoke(sa, new object[] { onSp });
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
