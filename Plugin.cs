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
using Newtonsoft.Json;
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
            ToggleKey = Config.Bind("General", "DebugToggleKey", new KeyboardShortcut(KeyCode.F10), "Optional debug key (F10).");
            LanguageConfig = Config.Bind("General", "Language", "EN", "Interface language: EN (English) or ES (Español).");
            AnimSpeedMultiplier = Config.Bind("Pulido", "AnimationSpeed", 1.0f, "3D animation speed multiplier (0.75, 1.0, 1.5, 2.0).");
            SfxVolumeConfig = Config.Bind("Pulido", "SfxVolume", 0.85f, "Pokémon TCG sound effects volume (0.0 to 1.0).");
            EnableCameraShake = Config.Bind("Pulido", "EnableCameraShake", true, "Enable camera shake on Knock Out (KO) and heavy hits.");
            RequireOwnedCards = Config.Bind("Coleccion", "RequireOwnedCards", true, "Only allow cards owned in your binder/shelves to be used in decks.");
            FreeBasicEnergy = Config.Bind("Coleccion", "FreeBasicEnergy", true, "Basic Energy cards do not require copies in your binder.");
            DefaultFormat = Config.Bind("Mazos", "DefaultFormat", "base_era", "Default format (id from Data/formats/*.json).");
            ActiveDeck = Config.Bind("Mazos", "ActiveDeck", "", "Name of the active deck used when playing at a table.");
            NpcDeckMode = Config.Bind("Mazos", "NpcDeckMode", "ThemeOrRandom", "NPC customer deck mode: ThemeOrRandom, ThemeOnly, RandomOnly, or a specific official theme deck ID.");
            if (LanguageConfig != null) LanguageConfig.Value = "EN";
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
        static readonly Dictionary<string, Texture2D> typeIconCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> grayscaleCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> refIncremented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string bundlePath;
        static bool bundlePathSearched;
        static bool eplHooked;
        static bool customTexturesLoaded;

        public static Texture2D PlaymatTex { get; private set; }
        public static Texture2D CoinHeadsTex { get; private set; }
        public static Texture2D CoinTailsTex { get; private set; }
        public static Texture2D GearIconTex { get; private set; }
        public static Texture2D FilterSlidersIconTex { get; private set; }
        public static Texture2D SearchLensIconTex { get; private set; }
        public static Texture2D ExpansionsAtlasTex { get; private set; }
        public static readonly List<ExpansionSymbolInfo> ExpansionCatalog = new List<ExpansionSymbolInfo>();

        public class ExpansionSymbolInfo
        {
            [JsonProperty("index")] public int Index;
            [JsonProperty("name")] public string Name = "";
            [JsonProperty("date")] public string Date = "";
            [JsonProperty("setId")] public string SetId = "";
            [JsonProperty("myCurioKey")] public string MyCurioKey = "";
            [JsonProperty("col")] public int Col;
            [JsonProperty("row")] public int Row;
            [JsonIgnore] public Rect UvRect;

            public string ResolveSetId()
            {
                if (!string.IsNullOrEmpty(SetId) && CardDatabase.GetSet(SetId) != null)
                    return SetId;
                var match = CardDatabase.Sets.FirstOrDefault(s =>
                    string.Equals(s.Name, Name, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(Name, "Base", StringComparison.OrdinalIgnoreCase) && string.Equals(s.Id, "base1", StringComparison.OrdinalIgnoreCase)));
                return match != null ? match.Id : SetId;
            }

            public bool IsInstalled()
            {
                string sid = ResolveSetId();
                if (!string.IsNullOrEmpty(sid))
                {
                    var sDef = CardDatabase.GetSet(sid);
                    if (sDef != null)
                    {
                        if (MyCurioIndex.Expansions.Count == 0 || string.IsNullOrEmpty(sDef.MyCurioExpansion) || MyCurioIndex.Expansions.ContainsKey(sDef.MyCurioExpansion))
                            return true;
                    }
                }
                if (!string.IsNullOrEmpty(MyCurioKey) && MyCurioIndex.Expansions.ContainsKey(MyCurioKey))
                    return true;
                if (!string.IsNullOrEmpty(Name) && MyCurioIndex.Expansions.Values.Any(e =>
                    !string.IsNullOrEmpty(e.Name) &&
                    (string.Equals(e.Name, Name, StringComparison.OrdinalIgnoreCase) ||
                     e.Name.IndexOf(Name, StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    return true;
                }
                return false;
            }
        }

        class ExpansionCatalogDto
        {
            [JsonProperty("cols")] public int Cols = 14;
            [JsonProperty("rows")] public int Rows = 13;
            [JsonProperty("cellSize")] public int CellSize = 96;
            [JsonProperty("expansions")] public List<ExpansionSymbolInfo> Expansions = null;
            [JsonProperty("items")] public List<ExpansionSymbolInfo> Items = null;
        }

        public static readonly string[] AllTypeIconNames =
        {
            "Fire", "Water", "Grass", "Lightning", "Psychic",
            "Fighting", "Darkness", "Metal", "Dragon", "Fairy", "Colorless"
        };

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
            GearIconTex = MakeGearIconTex(64);
            FilterSlidersIconTex = MakeFilterSlidersIconTex(64);
            SearchLensIconTex = MakeSearchLensIconTex(64);
            foreach (var tn in AllTypeIconNames)
            {
                var tex = LoadPng(Path.Combine(Plugin.DataDir, "icons", $"type_{tn}.png"));
                if (tex != null) typeIconCache[tn] = tex;
            }
            ExpansionsAtlasTex = LoadPng(Path.Combine(Plugin.DataDir, "icons", "expansions_atlas.png"));
            LoadExpansionCatalog();
        }

        static void LoadExpansionCatalog()
        {
            ExpansionCatalog.Clear();
            try
            {
                string jsonPath = Path.Combine(Plugin.DataDir, "icons", "expansions_catalog.json");
                if (!File.Exists(jsonPath)) return;
                var dto = JsonConvert.DeserializeObject<ExpansionCatalogDto>(File.ReadAllText(jsonPath));
                if (dto == null) return;
                var list = dto.Expansions ?? dto.Items;
                if (list == null) return;
                int cols = Mathf.Max(1, dto.Cols);
                int rows = Mathf.Max(1, dto.Rows);
                float uvW = 1f / cols;
                float uvH = 1f / rows;
                foreach (var item in list)
                {
                    if (item == null) continue;
                    item.UvRect = new Rect(item.Col * uvW, 1f - (item.Row + 1) * uvH, uvW, uvH);
                    ExpansionCatalog.Add(item);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("LoadExpansionCatalog: " + e.Message);
            }
        }

        public static Texture2D GetTypeIcon(string typeName)
        {
            LoadCustomTextures();
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string key = typeName.Trim();
            if (string.Equals(key, "Electric", StringComparison.OrdinalIgnoreCase)) key = "Lightning";
            else if (string.Equals(key, "Normal", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Rainbow", StringComparison.OrdinalIgnoreCase)) key = "Colorless";
            else if (string.Equals(key, "Dark", StringComparison.OrdinalIgnoreCase)) key = "Darkness";
            else if (string.Equals(key, "Steel", StringComparison.OrdinalIgnoreCase)) key = "Metal";
            if (typeIconCache.TryGetValue(key, out var t) && t != null)
                return t;
            var loaded = LoadPng(Path.Combine(Plugin.DataDir, "icons", $"type_{key}.png"));
            if (loaded != null) typeIconCache[key] = loaded;
            return loaded;
        }

        public static Texture2D GetGrayscaleTexture(string cacheKey, Sprite sp)
        {
            if (sp == null || sp.texture == null) return null;
            string key = string.IsNullOrEmpty(cacheKey) ? sp.name : cacheKey;
            if (!string.IsNullOrEmpty(key) && grayscaleCache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            Texture2D src = sp.texture;
            Rect sr = sp.textureRect;
            int w = Mathf.Max(1, Mathf.RoundToInt(sr.width));
            int h = Mathf.Max(1, Mathf.RoundToInt(sr.height));
            Texture2D readable = null;
            bool createdTemp = false;
            try
            {
                readable = src.isReadable ? src : MakeReadableCopy(src);
                createdTemp = readable != null && !ReferenceEquals(readable, src);
                if (readable == null) return null;

                int rx = Mathf.Clamp(Mathf.RoundToInt(sr.x), 0, Mathf.Max(0, readable.width - w));
                int ry = Mathf.Clamp(Mathf.RoundToInt(sr.y), 0, Mathf.Max(0, readable.height - h));
                Color[] pixels = readable.GetPixels(rx, ry, w, h);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    float lum = (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) * 0.76f;
                    pixels[i] = new Color(lum, lum, lum * 1.04f, c.a);
                }

                var gray = new Texture2D(w, h, TextureFormat.RGBA32, false);
                gray.filterMode = FilterMode.Bilinear;
                gray.wrapMode = TextureWrapMode.Clamp;
                gray.SetPixels(pixels);
                gray.Apply();
                if (!string.IsNullOrEmpty(key))
                    grayscaleCache[key] = gray;
                return gray;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (createdTemp && readable != null)
                    UnityEngine.Object.Destroy(readable);
            }
        }

        static Texture2D MakeGearIconTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - cx) / half;
                    float dy = (y - cy) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);
                    float toothWave = Mathf.Cos(ang * 8f);
                    float outerR = 0.62f + Mathf.Clamp01((toothWave - 0.15f) * 3.5f) * 0.17f;
                    float innerR = 0.27f;
                    float distOuter = r - outerR;
                    float distInner = innerR - r;
                    float dist = Mathf.Max(distOuter, distInner);
                    float alpha = Mathf.Clamp01(1f - dist * (size * 0.45f));
                    tex.SetPixel(x, y, new Color(0.95f, 0.97f, 1f, alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D MakeFilterSlidersIconTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            float[] rowsY = { 0.26f, 0.50f, 0.74f };
            float[] knobX = { 0.36f, 0.66f, 0.44f };
            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)(size - 1);
                    float a = 0f;
                    for (int r = 0; r < 3; r++)
                    {
                        float ry = rowsY[r];
                        float kx = knobX[r];
                        if (nx >= 0.16f && nx <= 0.84f)
                        {
                            float dLine = Mathf.Abs(ny - ry);
                            a = Mathf.Max(a, Mathf.Clamp01(1f - (dLine - 0.028f) * size * 0.7f));
                        }
                        float dKnob = Vector2.Distance(new Vector2(nx, ny), new Vector2(kx, ry));
                        if (dKnob <= 0.11f)
                        {
                            float kAlpha = Mathf.Clamp01(1f - (dKnob - 0.085f) * size * 0.6f);
                            a = Mathf.Max(a, kAlpha);
                        }
                    }
                    tex.SetPixel(x, y, new Color(0.95f, 0.92f, 0.84f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D MakeSearchLensIconTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Vector2 center = new Vector2(0.42f, 0.58f);
            Vector2 h0 = new Vector2(0.56f, 0.44f);
            Vector2 h1 = new Vector2(0.82f, 0.18f);
            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)(size - 1);
                    Vector2 p = new Vector2(nx, ny);
                    float dRing = Mathf.Abs(Vector2.Distance(p, center) - 0.22f);
                    float aRing = Mathf.Clamp01(1f - (dRing - 0.038f) * size * 0.55f);

                    Vector2 pa = p - h0;
                    Vector2 ba = h1 - h0;
                    float hProj = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
                    float dHandle = (pa - ba * hProj).magnitude;
                    float aHandle = Mathf.Clamp01(1f - (dHandle - 0.042f) * size * 0.55f);

                    tex.SetPixel(x, y, new Color(0.92f, 0.90f, 0.84f, Mathf.Max(aRing, aHandle)));
                }
            }
            tex.Apply();
            return tex;
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
