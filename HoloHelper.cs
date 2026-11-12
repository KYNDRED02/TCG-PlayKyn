// TCG-PlayKyn: Optional holographic and foil rendering bridge (MyCurioHolo & game shaders)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// Gestiona de forma modular y 100% opcional (soft-dependency) la integración visual con
    /// MyCurioHolo, Holographic Overhaul o los shaders de Foil del juego base.
    /// Si ningún mod de foil está instalado, las cartas se visualizan limpias sin lanzar errores.
    /// </summary>
    public static class HoloHelper
    {
        static bool sInitialized;
        static bool sMyCurioHoloAvailable;
        static IDictionary sMyCurioHoloTypes;
        static MethodInfo sMyCurioHoloMaterialFor;
        static readonly Dictionary<string, Material> sHoloMaterialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        static Shader sFallbackFoilShader;

        public static bool IsMyCurioHoloAvailable
        {
            get
            {
                EnsureInitialized();
                return sMyCurioHoloAvailable;
            }
        }

        public static void EnsureInitialized()
        {
            if (sInitialized) return;
            sInitialized = true;

            try
            {
                // Buscar MyCurioHolo en los ensamblados cargados en el dominio de la aplicación
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    var asm = assemblies[i];
                    if (asm == null) continue;
                    string name = asm.GetName().Name;
                    if (string.Equals(name, "MyCurioHolo", StringComparison.OrdinalIgnoreCase))
                    {
                        var tPlugin = asm.GetType("MyCurioHolo.Plugin");
                        if (tPlugin != null)
                        {
                            var fTypes = tPlugin.GetField("Types", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            sMyCurioHoloTypes = fTypes?.GetValue(null) as IDictionary;
                            sMyCurioHoloMaterialFor = tPlugin.GetMethod("MaterialFor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            sMyCurioHoloAvailable = (sMyCurioHoloTypes != null && sMyCurioHoloMaterialFor != null);
                            if (sMyCurioHoloAvailable)
                            {
                                Plugin.Log?.LogInfo("HoloHelper: MyCurioHolo detectado exitosamente. Efectos holográficos activos.");
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("HoloHelper: No se pudo enlazar MyCurioHolo (funcionará en modo estándar): " + ex.Message);
                sMyCurioHoloAvailable = false;
            }

            try
            {
                // Fallback a shaders de Foil en el juego o de mods como Holographic Overhaul
                sFallbackFoilShader = Shader.Find("Custom/Holofoil") ?? Shader.Find("Card/Foil") ?? Shader.Find("TCG/CardFoil");
            }
            catch { }
        }

        /// <summary>
        /// Aplica la textura principal, el color y las coordenadas atlas/UV al material holográfico,
        /// evitando que la carta o el arte se visualicen en blanco sólido.
        /// </summary>
        public static void ApplySpriteProperties(Material mat, Sprite sp)
        {
            if (mat == null || sp == null || sp.texture == null) return;
            Texture2D t = sp.texture;
            Rect tr = sp.textureRect;
            float tw = t.width;
            float th = t.height;

            Vector4 artST = new Vector4(tr.width / tw, tr.height / th, tr.x / tw, tr.y / th);

            // 1. Asignar textura principal de la carta en ambos campos estándar de Unity
            mat.mainTexture = t;
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", t);

            // 2. Coordenadas de recorte / atlas (escala y desplazamiento)
            mat.mainTextureOffset = new Vector2(artST.z, artST.w);
            mat.mainTextureScale = new Vector2(artST.x, artST.y);

            if (mat.HasProperty("_ArtST"))
                mat.SetVector("_ArtST", artST);

            // 3. Color base en blanco puro para no oscurecer ni teñir el arte original
            mat.color = Color.white;
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", Color.white);
        }

        /// <summary>
        /// Obtiene el material holográfico especializado para la carta si es Foil, configurando
        /// las coordenadas de textura atlas (_ArtST) y las máscaras de brillo.
        /// </summary>
        public static Material GetHoloMaterial(CardDef card, Sprite sp)
        {
            if (card == null || !card.IsFoil || sp == null || sp.texture == null) return null;
            EnsureInitialized();

            string spriteKey = card.Sprite;
            if (string.IsNullOrEmpty(spriteKey)) return null;

            string cacheKey = spriteKey + "_" + sp.name;
            if (sHoloMaterialCache.TryGetValue(cacheKey, out var existingMat) && existingMat != null)
            {
                ApplySpriteProperties(existingMat, sp);
                return existingMat;
            }

            // 1. Intentar obtener material holográfico desde MyCurioHolo
            if (sMyCurioHoloAvailable && sMyCurioHoloTypes != null && sMyCurioHoloMaterialFor != null)
            {
                try
                {
                    string style = null;
                    if (sMyCurioHoloTypes.Contains(spriteKey))
                    {
                        style = sMyCurioHoloTypes[spriteKey] as string;
                    }
                    else if (sMyCurioHoloTypes.Contains(spriteKey + "_R"))
                    {
                        style = sMyCurioHoloTypes[spriteKey + "_R"] as string;
                    }

                    if (!string.IsNullOrEmpty(style))
                    {
                        var rawMat = sMyCurioHoloMaterialFor.Invoke(null, new object[] { style, spriteKey }) as Material;
                        if (rawMat != null)
                        {
                            var instanceMat = new Material(rawMat);
                            instanceMat.name = "Holo_" + spriteKey;
                            ApplySpriteProperties(instanceMat, sp);

                            sHoloMaterialCache[cacheKey] = instanceMat;
                            return instanceMat;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"HoloHelper: Error al obtener material MyCurioHolo para {spriteKey}: {e.Message}");
                }
            }

            // 2. Fallback opcional a shader de foil del juego si no se encuentra en MyCurioHolo
            if (sFallbackFoilShader != null && sp != null && sp.texture != null)
            {
                try
                {
                    var fallback = new Material(sFallbackFoilShader);
                    fallback.name = "FallbackFoil_" + spriteKey;
                    ApplySpriteProperties(fallback, sp);
                    sHoloMaterialCache[cacheKey] = fallback;
                    return fallback;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Limpia la caché de materiales al cambiar de escena o reiniciar recursos.
        /// </summary>
        public static void ClearCache()
        {
            foreach (var kv in sHoloMaterialCache)
            {
                if (kv.Value != null)
                    UnityEngine.Object.Destroy(kv.Value);
            }
            sHoloMaterialCache.Clear();
        }
    }
}
