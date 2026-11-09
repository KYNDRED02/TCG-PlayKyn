// PokeTCGPlay: 3D status counters, audio SFX triggers, localization and settings modal
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// F10: Opciones globales de pulido (Velocidad de Animación, Idioma ES/EN, Volumen SFX y Sacudida de Cámara).
    /// </summary>
    public static class Loc
    {
        public static string CurrentLang
        {
            get => "EN";
            set
            {
                if (Plugin.LanguageConfig != null)
                    Plugin.LanguageConfig.Value = "EN";
            }
        }

        public static bool IsEnglish => true;

        public static string T(string es, string en) => en;

        public static float AnimSpeed
        {
            get => Plugin.AnimSpeedMultiplier != null ? Mathf.Clamp(Plugin.AnimSpeedMultiplier.Value, 0.5f, 3.0f) : 1.0f;
            set
            {
                if (Plugin.AnimSpeedMultiplier != null)
                    Plugin.AnimSpeedMultiplier.Value = Mathf.Clamp(value, 0.5f, 3.0f);
            }
        }

        public static float SfxVolume
        {
            get => Plugin.SfxVolumeConfig != null ? Mathf.Clamp01(Plugin.SfxVolumeConfig.Value) : 0.85f;
            set
            {
                if (Plugin.SfxVolumeConfig != null)
                    Plugin.SfxVolumeConfig.Value = Mathf.Clamp01(value);
            }
        }

        public static bool CameraShakeEnabled
        {
            get => Plugin.EnableCameraShake == null || Plugin.EnableCameraShake.Value;
            set
            {
                if (Plugin.EnableCameraShake != null)
                    Plugin.EnableCameraShake.Value = value;
            }
        }

        public static string TypeLabel(string elementType)
        {
            if (IsEnglish) return string.IsNullOrEmpty(elementType) ? "Colorless" : elementType;
            switch (elementType)
            {
                case "Fire": return "Fuego";
                case "Water": return "Agua";
                case "Grass": return "Planta";
                case "Lightning": return "Rayo";
                case "Psychic": return "Psíquico";
                case "Fighting": return "Lucha";
                case "Colorless": return "Incoloro";
                default: return string.IsNullOrEmpty(elementType) ? "Incoloro" : elementType;
            }
        }
    }

    /// <summary>
    /// F10: Sintetizador y reproductor de efectos de sonido dedicados para Pokémon TCG:
    /// barajar mazo, robar carta, colocar carta sobre el tapete, unir energía, evolucionar,
    /// lanzamiento y rebote metálico de moneda 3D, impactos de ataque por tipo elemental y KO.
    /// </summary>
    public static class TcgAudio
    {
        const int SampleRate = 44100;
        static AudioSource sAudioSource;
        static bool sInitialized;

        static AudioClip sShuffleClip;
        static AudioClip sCardDrawClip;
        static AudioClip sCardPlaceClip;
        static AudioClip sAttachEnergyClip;
        static AudioClip sEvolveClip;
        static AudioClip sCoinLaunchClip;
        static AudioClip sCoinBounceClip;
        static AudioClip sCoinSettleClip;
        static AudioClip sKnockOutClip;
        static AudioClip sVictoryClip;
        static AudioClip sDefeatClip;
        static readonly Dictionary<string, AudioClip> sAttackClips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        public static void EnsureInitialized()
        {
            if (sInitialized && sAudioSource != null) return;
            sInitialized = true;

            try
            {
                var go = new GameObject("PokeTCG_AudioEngine");
                UnityEngine.Object.DontDestroyOnLoad(go);
                sAudioSource = go.AddComponent<AudioSource>();
                sAudioSource.playOnAwake = false;
                sAudioSource.spatialBlend = 0f; // 2D studio clarity

                sShuffleClip = BuildShuffleClip();
                sCardDrawClip = BuildCardDrawClip();
                sCardPlaceClip = BuildCardPlaceClip();
                sAttachEnergyClip = BuildAttachEnergyClip();
                sEvolveClip = BuildEvolveClip();
                sCoinLaunchClip = BuildCoinLaunchClip();
                sCoinBounceClip = BuildCoinBounceClip();
                sCoinSettleClip = BuildCoinSettleClip();
                sKnockOutClip = BuildKnockOutClip();
                sVictoryClip = BuildFanfareClip(true);
                sDefeatClip = BuildFanfareClip(false);

                sAttackClips["Fire"] = BuildElementAttackClip("Fire");
                sAttackClips["Water"] = BuildElementAttackClip("Water");
                sAttackClips["Grass"] = BuildElementAttackClip("Grass");
                sAttackClips["Lightning"] = BuildElementAttackClip("Lightning");
                sAttackClips["Psychic"] = BuildElementAttackClip("Psychic");
                sAttackClips["Fighting"] = BuildElementAttackClip("Fighting");
                sAttackClips["Colorless"] = BuildElementAttackClip("Colorless");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("TcgAudio init: " + e.Message);
            }
        }

        static float GetEffectiveVolume(float baseMul = 1f)
        {
            float gameSfx = 1f;
            try { gameSfx = Mathf.Clamp01(SoundManager.SFXVolume); } catch { }
            return Mathf.Clamp01(gameSfx * Loc.SfxVolume * baseMul);
        }

        static void PlayClip(AudioClip clip, float volumeMul = 1f, float pitch = 1f)
        {
            EnsureInitialized();
            float vol = GetEffectiveVolume(volumeMul);
            if (vol <= 0.005f || clip == null || sAudioSource == null) return;
            try
            {
                sAudioSource.pitch = Mathf.Clamp(pitch, 0.7f, 1.4f);
                sAudioSource.PlayOneShot(clip, vol);
            }
            catch { }
        }

        public static void PlayShuffle()
        {
            PlayClip(sShuffleClip, 0.95f, 1.0f);
            try { SoundManager.GenericLightTap(0.45f * Loc.SfxVolume, 0.92f); } catch { }
        }

        public static void PlayCardDraw()
        {
            PlayClip(sCardDrawClip, 0.80f, UnityEngine.Random.Range(0.96f, 1.05f));
        }

        public static void PlayCardPlace()
        {
            PlayClip(sCardPlaceClip, 0.88f, UnityEngine.Random.Range(0.95f, 1.04f));
            try { SoundManager.GenericLightTap(0.55f * Loc.SfxVolume, 0.88f); } catch { }
        }

        public static void PlayAttachEnergy()
        {
            PlayClip(sAttachEnergyClip, 0.85f, 1.0f);
            try { SoundManager.GenericPop(0.45f * Loc.SfxVolume, 1.12f); } catch { }
        }

        public static void PlayEvolve()
        {
            PlayClip(sEvolveClip, 0.95f, 1.0f);
            try { SoundManager.GenericConfirm(0.55f * Loc.SfxVolume, 1.15f); } catch { }
        }

        public static void PlayCoinLaunch()
        {
            PlayClip(sCoinLaunchClip, 0.90f, 1.0f);
        }

        public static void PlayCoinBounce(int bounceIndex)
        {
            float vol = bounceIndex <= 1 ? 0.82f : 0.55f;
            float pitch = bounceIndex <= 1 ? 1.0f : 1.14f;
            PlayClip(sCoinBounceClip, vol, pitch);
        }

        public static void PlayCoinSettle(bool heads)
        {
            PlayClip(sCoinSettleClip, 0.80f, heads ? 1.06f : 0.94f);
        }

        public static void PlayAttackImpact(string elementType, int damage = 20)
        {
            EnsureInitialized();
            string key = string.IsNullOrEmpty(elementType) || !sAttackClips.ContainsKey(elementType)
                ? "Colorless"
                : elementType;
            float boost = Mathf.Clamp(0.82f + (damage / 80f) * 0.25f, 0.80f, 1.15f);
            PlayClip(sAttackClips[key], boost, 1.0f);
        }

        public static void PlayKnockOut()
        {
            PlayClip(sKnockOutClip, 1.0f, 1.0f);
            try { SoundManager.GenericCancel(0.65f * Loc.SfxVolume, 0.78f); } catch { }
        }

        public static void PlayMatchResult(bool playerWon)
        {
            PlayClip(playerWon ? sVictoryClip : sDefeatClip, 0.95f, 1.0f);
            try
            {
                if (playerWon) SoundManager.GenericConfirm(0.8f * Loc.SfxVolume, 1.05f);
                else SoundManager.GenericCancel(0.8f * Loc.SfxVolume, 0.90f);
            }
            catch { }
        }

        // ==================== Síntesis Procedural de AudioClips (44.1 kHz) ====================
        static AudioClip CreateClip(string name, float[] samples)
        {
            var clip = AudioClip.Create("PokeTCG_" + name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        static AudioClip BuildShuffleClip()
        {
            // Cascada rápida de 11 aleteos de cartulina (riffle shuffle) + golpe final de emparejado
            int len = Mathf.RoundToInt(SampleRate * 0.52f);
            var data = new float[len];
            var rng = new System.Random(101);
            float[] flickTimes = { 0.01f, 0.045f, 0.08f, 0.11f, 0.145f, 0.175f, 0.21f, 0.245f, 0.28f, 0.315f, 0.355f, 0.42f };

            for (int f = 0; f < flickTimes.Length; f++)
            {
                int start = Mathf.RoundToInt(flickTimes[f] * SampleRate);
                bool finalTap = (f == flickTimes.Length - 1);
                int dur = Mathf.RoundToInt((finalTap ? 0.075f : 0.028f) * SampleRate);
                float freq = finalTap ? 145f : (380f + f * 28f);

                for (int i = 0; i < dur && (start + i) < len; i++)
                {
                    float t = i / (float)SampleRate;
                    float env = Mathf.Exp(-t * (finalTap ? 42f : 95f));
                    float noise = ((float)rng.NextDouble() * 2f - 1f) * (finalTap ? 0.35f : 0.72f);
                    float tone = Mathf.Sin(2f * Mathf.PI * freq * t) * (finalTap ? 0.65f : 0.32f);
                    data[start + i] = Mathf.Clamp(data[start + i] + (noise + tone) * env * 0.52f, -0.95f, 0.95f);
                }
            }
            return CreateClip("Shuffle", data);
        }

        static AudioClip BuildCardDrawClip()
        {
            // Deslizamiento sedoso de carta saliendo del tope del mazo
            int len = Mathf.RoundToInt(SampleRate * 0.16f);
            var data = new float[len];
            var rng = new System.Random(202);
            float prev = 0f;
            for (int i = 0; i < len; i++)
            {
                float u = i / (float)len;
                float t = i / (float)SampleRate;
                float env = Mathf.Sin(u * Mathf.PI) * Mathf.Exp(-u * 1.8f);
                float rawNoise = (float)rng.NextDouble() * 2f - 1f;
                prev = Mathf.Lerp(prev, rawNoise, 0.32f);
                float sweep = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(520f, 980f, u) * t) * 0.18f;
                data[i] = (prev * 0.55f + sweep) * env * 0.55f;
            }
            return CreateClip("CardDraw", data);
        }

        static AudioClip BuildCardPlaceClip()
        {
            // Golpe amortiguado de carta posándose sobre tapete de neopreno
            int len = Mathf.RoundToInt(SampleRate * 0.14f);
            var data = new float[len];
            var rng = new System.Random(303);
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float envThud = Mathf.Exp(-t * 58f);
                float envSnap = Mathf.Exp(-t * 140f);
                float thud = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(175f, 78f, t / 0.14f) * t) * envThud * 0.68f;
                float snap = ((float)rng.NextDouble() * 2f - 1f) * envSnap * 0.38f;
                data[i] = Mathf.Clamp((thud + snap) * 0.65f, -0.95f, 0.95f);
            }
            return CreateClip("CardPlace", data);
        }

        static AudioClip BuildAttachEnergyClip()
        {
            // Pulso cristalino ascendente al unir energía
            int len = Mathf.RoundToInt(SampleRate * 0.28f);
            var data = new float[len];
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float u = i / (float)len;
                float env = Mathf.Sin(u * Mathf.PI) * Mathf.Exp(-t * 5.2f);
                float f1 = Mathf.Lerp(440f, 880f, u);
                float f2 = f1 * 1.5f;
                float sample = (Mathf.Sin(2f * Mathf.PI * f1 * t) * 0.55f + Mathf.Sin(2f * Mathf.PI * f2 * t) * 0.30f) * env;
                data[i] = sample * 0.55f;
            }
            return CreateClip("AttachEnergy", data);
        }

        static AudioClip BuildEvolveClip()
        {
            // Arpegio armónico ascendente (C5 - E5 - G5 - C6)
            int len = Mathf.RoundToInt(SampleRate * 0.54f);
            var data = new float[len];
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.50f };
            float[] starts = { 0.00f, 0.09f, 0.18f, 0.27f };

            for (int n = 0; n < notes.Length; n++)
            {
                int sIdx = Mathf.RoundToInt(starts[n] * SampleRate);
                float freq = notes[n];
                for (int i = sIdx; i < len; i++)
                {
                    float t = (i - sIdx) / (float)SampleRate;
                    float env = Mathf.Exp(-t * 8.5f) * Mathf.Clamp01(t / 0.008f);
                    float val = (Mathf.Sin(2f * Mathf.PI * freq * t) + 0.35f * Mathf.Sin(2f * Mathf.PI * freq * 2f * t)) * env * 0.28f;
                    data[i] = Mathf.Clamp(data[i] + val, -0.95f, 0.95f);
                }
            }
            return CreateClip("Evolve", data);
        }

        static AudioClip BuildCoinLaunchClip()
        {
            // Ping metálico brillante al lanzar la moneda con el pulgar
            int len = Mathf.RoundToInt(SampleRate * 0.34f);
            var data = new float[len];
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float env = Mathf.Exp(-t * 13.5f);
                float ring1 = Mathf.Sin(2f * Mathf.PI * 2480f * t);
                float ring2 = Mathf.Sin(2f * Mathf.PI * 5120f * t) * 0.55f;
                float ring3 = Mathf.Sin(2f * Mathf.PI * 7410f * t) * 0.25f * Mathf.Exp(-t * 28f);
                data[i] = (ring1 + ring2 + ring3) * env * 0.38f;
            }
            return CreateClip("CoinLaunch", data);
        }

        static AudioClip BuildCoinBounceClip()
        {
            // Toque metálico corto contra el tapete
            int len = Mathf.RoundToInt(SampleRate * 0.14f);
            var data = new float[len];
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float env = Mathf.Exp(-t * 36f);
                float thud = Mathf.Sin(2f * Mathf.PI * 195f * t) * Mathf.Exp(-t * 65f) * 0.55f;
                float clink = (Mathf.Sin(2f * Mathf.PI * 2150f * t) + 0.45f * Mathf.Sin(2f * Mathf.PI * 4380f * t)) * env * 0.35f;
                data[i] = Mathf.Clamp(thud + clink, -0.95f, 0.95f);
            }
            return CreateClip("CoinBounce", data);
        }

        static AudioClip BuildCoinSettleClip()
        {
            // Vibración metálica rápida (wobble) al asentarse la moneda
            int len = Mathf.RoundToInt(SampleRate * 0.22f);
            var data = new float[len];
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float env = Mathf.Exp(-t * 16f);
                float mod = 0.65f + 0.35f * Mathf.Sin(2f * Mathf.PI * 28f * t);
                float bell = Mathf.Sin(2f * Mathf.PI * 1960f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 3920f * t);
                data[i] = bell * mod * env * 0.28f;
            }
            return CreateClip("CoinSettle", data);
        }

        static AudioClip BuildElementAttackClip(string elementType)
        {
            int len = Mathf.RoundToInt(SampleRate * 0.38f);
            var data = new float[len];
            var rng = new System.Random(elementType.GetHashCode());
            float filterState = 0f;

            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float u = i / (float)len;
                float noise = (float)rng.NextDouble() * 2f - 1f;
                float sample = 0f;

                switch (elementType)
                {
                    case "Fire":
                        // Rugido de llamarada + crepitar ígneo
                        filterState = Mathf.Lerp(filterState, noise, 0.18f);
                        float fireEnv = Mathf.Sin(Mathf.Pow(u, 0.6f) * Mathf.PI) * Mathf.Exp(-t * 3.5f);
                        float fireLow = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(210f, 95f, u) * t) * 0.45f;
                        sample = (filterState * 0.85f + fireLow) * fireEnv;
                        break;

                    case "Water":
                        // Impacto hidro-presión + burbujeo resonante
                        float waterEnv = Mathf.Exp(-t * 6.5f);
                        float bubbleFreq = 340f + Mathf.Sin(t * 95f) * 160f + u * 280f;
                        filterState = Mathf.Lerp(filterState, noise, 0.28f);
                        sample = (Mathf.Sin(2f * Mathf.PI * bubbleFreq * t) * 0.55f + filterState * 0.45f) * waterEnv;
                        break;

                    case "Grass":
                        // Latigazo/corte rápido de hojas afiladas
                        float grassEnv = Mathf.Exp(-t * 11f);
                        float whipFreq = Mathf.Lerp(1250f, 280f, Mathf.Pow(u, 0.55f));
                        sample = (Mathf.Sin(2f * Mathf.PI * whipFreq * t) * 0.6f + noise * 0.35f * Mathf.Exp(-t * 18f)) * grassEnv;
                        break;

                    case "Lightning":
                        // Descarga eléctrica de alto voltaje + trueno
                        float zapEnv = Mathf.Exp(-t * 8.2f);
                        float saw = ((t * 620f) % 1f) * 2f - 1f;
                        float modZap = Mathf.Sin(2f * Mathf.PI * (1400f + Mathf.Sin(t * 240f) * 650f) * t);
                        sample = (saw * 0.35f + modZap * 0.45f + noise * 0.35f) * zapEnv;
                        break;

                    case "Psychic":
                        // Onda psíquica hipnótica con vibrato espacial
                        float psyEnv = Mathf.Sin(u * Mathf.PI) * Mathf.Exp(-t * 3.2f);
                        float vib = Mathf.Sin(2f * Mathf.PI * 16f * t) * 55f;
                        float psy1 = Mathf.Sin(2f * Mathf.PI * (520f + vib) * t);
                        float psy2 = Mathf.Sin(2f * Mathf.PI * (785f - vib * 0.8f) * t);
                        sample = (psy1 * 0.55f + psy2 * 0.45f) * psyEnv;
                        break;

                    case "Fighting":
                        // Impacto sísmico contundente de roca/puño
                        float fightEnv = Mathf.Exp(-t * 10.5f);
                        float punchFreq = Mathf.Lerp(165f, 44f, Mathf.Pow(u, 0.45f));
                        float sub = Mathf.Sin(2f * Mathf.PI * punchFreq * t) * 0.80f;
                        float grit = noise * Mathf.Exp(-t * 24f) * 0.50f;
                        sample = (sub + grit) * fightEnv;
                        break;

                    default:
                        // Corte físico veloz (Colorless)
                        float slashEnv = Mathf.Exp(-t * 12f);
                        float slashFreq = Mathf.Lerp(680f, 130f, u);
                        sample = (Mathf.Sin(2f * Mathf.PI * slashFreq * t) * 0.60f + noise * 0.40f * Mathf.Exp(-t * 20f)) * slashEnv;
                        break;
                }

                data[i] = Mathf.Clamp(sample * 0.68f, -0.95f, 0.95f);
            }
            return CreateClip("Atk_" + elementType, data);
        }

        static AudioClip BuildKnockOutClip()
        {
            // Estruendo grave de Fuera de Combate (KO) + caída dramática
            int len = Mathf.RoundToInt(SampleRate * 0.56f);
            var data = new float[len];
            var rng = new System.Random(909);
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float u = i / (float)len;
                float env = Mathf.Exp(-t * 5.2f);
                float boom = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(155f, 38f, Mathf.Pow(u, 0.5f)) * t) * 0.72f;
                float rumble = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 11f) * 0.42f;
                data[i] = Mathf.Clamp((boom + rumble) * env * 0.82f, -0.95f, 0.95f);
            }
            return CreateClip("KnockOut", data);
        }

        static AudioClip BuildFanfareClip(bool victory)
        {
            int len = Mathf.RoundToInt(SampleRate * 0.72f);
            var data = new float[len];
            float[] freqs = victory
                ? new[] { 523.25f, 659.25f, 783.99f, 1046.50f }
                : new[] { 392.00f, 349.23f, 311.13f, 261.63f };
            float[] starts = { 0.00f, 0.12f, 0.24f, 0.36f };

            for (int n = 0; n < freqs.Length; n++)
            {
                int sIdx = Mathf.RoundToInt(starts[n] * SampleRate);
                float f = freqs[n];
                for (int i = sIdx; i < len; i++)
                {
                    float t = (i - sIdx) / (float)SampleRate;
                    float env = Mathf.Exp(-t * 6.0f);
                    float v = (Mathf.Sin(2f * Mathf.PI * f * t) + 0.3f * Mathf.Sin(2f * Mathf.PI * f * 2f * t)) * env * 0.26f;
                    data[i] = Mathf.Clamp(data[i] + v, -0.95f, 0.95f);
                }
            }
            return CreateClip(victory ? "Victory" : "Defeat", data);
        }
    }

    /// <summary>
    /// F10: Efectos visuales 3D por tipo elemental sobre el tapete físico, sacudida de cámara al KO/impacto
    /// y ventana unificada de Configuración en vivo (Velocidad de Animación, Idioma, Audio y Cámara).
    /// </summary>
    public static class PolishEffects3D
    {
        class ParticleBurst3D
        {
            public string ElementType;
            public Vector3 OriginWorld;
            public float Elapsed;
            public float Duration;
            public float BaseScale;
            public Color PrimaryColor;
            public Color SecondaryColor;
            public readonly Vector3[]Dirs = new Vector3[14];
            public readonly float[] Speeds = new float[14];
            public readonly float[] SpinOffsets = new float[14];
        }

        class PooledVfxNode
        {
            public GameObject Go;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Material Mat;
        }

        static Transform sVfxRoot;
        static readonly List<ParticleBurst3D> sActiveBursts = new List<ParticleBurst3D>();
        static readonly List<PooledVfxNode> sActiveNodes = new List<PooledVfxNode>();
        static readonly Stack<PooledVfxNode> sInactiveNodes = new Stack<PooledVfxNode>();

        static Mesh sQuadMesh;
        static Mesh sCubeMesh;
        static Mesh sSphereMesh;
        static Texture2D sRingTex;
        static Texture2D sGlowTex;

        // Estado de Sacudida de Cámara (Camera Shake en KO e impactos fuertes)
        static float sShakeTimer;
        static float sShakeDuration;
        static float sShakeIntensity;

        public static void ClearAll()
        {
            sActiveBursts.Clear();
            sShakeTimer = 0f;
            sShakeIntensity = 0f;
            RecycleAllNodes();
        }

        public static void TriggerCameraShake(float intensity = 0.018f, float duration = 0.35f)
        {
            if (!Loc.CameraShakeEnabled) return;
            if (intensity >= sShakeIntensity || sShakeTimer <= 0.05f)
            {
                sShakeIntensity = intensity;
                sShakeDuration = Mathf.Max(0.08f, duration);
                sShakeTimer = sShakeDuration;
            }
        }

        public static Vector3 GetCameraShakeOffset()
        {
            if (!Loc.CameraShakeEnabled || sShakeTimer <= 0f) return Vector3.zero;
            float decay = Mathf.Clamp01(sShakeTimer / Mathf.Max(0.05f, sShakeDuration));
            float amp = sShakeIntensity * decay * decay;
            float t = Time.unscaledTime * 54f;
            return new Vector3(
                Mathf.Sin(t * 1.13f) * amp,
                Mathf.Cos(t * 1.47f) * amp * 0.65f,
                Mathf.Sin(t * 0.89f + 1.4f) * amp * 0.75f);
        }

        public static void SpawnElementalAttackVfx(Vector3 worldPos, string elementType, int damage = 20)
        {
            EnsureVfxResources();
            GetElementPalette(elementType, out Color c1, out Color c2);
            float scale = Mathf.Clamp(0.95f + (damage / 70f) * 0.38f, 0.90f, 1.48f);

            var burst = new ParticleBurst3D
            {
                ElementType = string.IsNullOrEmpty(elementType) ? "Colorless" : elementType,
                OriginWorld = worldPos + Vector3.up * 0.005f,
                Elapsed = 0f,
                Duration = 0.56f / Mathf.Max(0.75f, Loc.AnimSpeed),
                BaseScale = scale,
                PrimaryColor = c1,
                SecondaryColor = c2
            };

            for (int i = 0; i < 14; i++)
            {
                float ang = (i / 14f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.18f, 0.18f);
                float horiz = UnityEngine.Random.Range(0.55f, 1.15f);
                float up = elementType == "Fire"
                    ? UnityEngine.Random.Range(0.95f, 1.65f)
                    : (elementType == "Lightning" ? UnityEngine.Random.Range(0.80f, 1.50f) : UnityEngine.Random.Range(0.45f, 1.10f));
                burst.Dirs[i] = new Vector3(Mathf.Cos(ang) * horiz, up, Mathf.Sin(ang) * horiz).normalized;
                burst.Speeds[i] = UnityEngine.Random.Range(0.14f, 0.27f) * scale;
                burst.SpinOffsets[i] = UnityEngine.Random.Range(0f, 360f);
            }

            sActiveBursts.Add(burst);
        }

        public static void SpawnKnockOutVfx(Vector3 worldPos)
        {
            SpawnElementalAttackVfx(worldPos, "Fire", 60);
            TriggerCameraShake(0.022f, 0.42f);
            TcgAudio.PlayKnockOut();
        }

        public static void SpawnEvolveVfx(Vector3 worldPos, string elementType)
        {
            SpawnElementalAttackVfx(worldPos, string.IsNullOrEmpty(elementType) ? "Psychic" : elementType, 35);
            TcgAudio.PlayEvolve();
        }

        public static void SyncFrame(Transform playmatRoot)
        {
            float dt = Time.deltaTime;
            if (sShakeTimer > 0f)
                sShakeTimer = Mathf.Max(0f, sShakeTimer - dt);

            RecycleAllNodes();
            if (sActiveBursts.Count == 0 || playmatRoot == null) return;

            EnsureVfxResources();
            Quaternion matRot = playmatRoot.rotation;

            for (int b = sActiveBursts.Count - 1; b >= 0; b--)
            {
                var burst = sActiveBursts[b];
                burst.Elapsed += dt;
                float u = Mathf.Clamp01(burst.Elapsed / Mathf.Max(0.08f, burst.Duration));
                if (u >= 1f)
                {
                    sActiveBursts.RemoveAt(b);
                    continue;
                }

                float fade = 1f - u * u;

                // 1) Anillo de onda de choque 3D sobre la carta/tapete
                var ring = SpawnNode(sQuadMesh, sRingTex);
                float ringDiam = Mathf.Lerp(0.035f, 0.21f * burst.BaseScale, Mathf.SmoothStep(0f, 1f, u));
                ring.Go.transform.position = burst.OriginWorld + Vector3.up * 0.002f;
                ring.Go.transform.rotation = matRot * Quaternion.Euler(90f, u * 90f, 0f);
                ring.Go.transform.localScale = new Vector3(ringDiam, ringDiam, 1f);
                ring.Mat.color = new Color(burst.PrimaryColor.r, burst.PrimaryColor.g, burst.PrimaryColor.b, 0.88f * fade);

                // 2) Destello central de impacto
                if (u < 0.55f)
                {
                    float flashU = u / 0.55f;
                    var glow = SpawnNode(sQuadMesh, sGlowTex);
                    float glowSize = Mathf.Lerp(0.05f, 0.16f * burst.BaseScale, Mathf.Sin(flashU * Mathf.PI));
                    glow.Go.transform.position = burst.OriginWorld + Vector3.up * 0.006f;
                    glow.Go.transform.rotation = matRot * Quaternion.Euler(90f, 0f, 0f);
                    glow.Go.transform.localScale = new Vector3(glowSize, glowSize, 1f);
                    glow.Mat.color = new Color(burst.SecondaryColor.r, burst.SecondaryColor.g, burst.SecondaryColor.b, (1f - flashU) * 0.82f);
                }

                // 3) 14 partículas 3D con geometría y movimiento propios del tipo elemental
                Mesh partMesh = GetElementParticleMesh(burst.ElementType);
                for (int i = 0; i < 14; i++)
                {
                    var node = SpawnNode(partMesh, null);
                    Vector3 dir = burst.Dirs[i];
                    float dist = burst.Speeds[i] * Mathf.Pow(u, 0.72f);

                    Vector3 offset;
                    if (burst.ElementType == "Psychic" || burst.ElementType == "Grass")
                    {
                        // Espiral orbital ascendente
                        float spiralAng = burst.SpinOffsets[i] * Mathf.Deg2Rad + u * 6.5f;
                        float rad = dist * 0.85f;
                        offset = new Vector3(Mathf.Cos(spiralAng) * rad, u * 0.095f * burst.BaseScale, Mathf.Sin(spiralAng) * rad);
                    }
                    else if (burst.ElementType == "Fighting" || burst.ElementType == "Water")
                    {
                        // Parábola balística con gravedad sobre la mesa
                        float arcY = Mathf.Sin(u * Mathf.PI) * 0.085f * burst.BaseScale;
                        offset = new Vector3(dir.x * dist, Mathf.Max(0.002f, arcY), dir.z * dist);
                    }
                    else
                    {
                        // Explosión radial/ascendente (Fire, Lightning, Colorless)
                        offset = dir * dist;
                    }

                    node.Go.transform.position = burst.OriginWorld + offset;
                    node.Go.transform.rotation = Quaternion.Euler(
                        burst.SpinOffsets[i] + u * 420f,
                        burst.SpinOffsets[i] * 1.3f + u * 360f,
                        u * 280f);

                    float pScale = Mathf.Lerp(0.015f, 0.003f, u) * burst.BaseScale;
                    if (burst.ElementType == "Lightning")
                        node.Go.transform.localScale = new Vector3(pScale * 0.38f, pScale * 2.4f, pScale * 0.38f);
                    else if (burst.ElementType == "Grass")
                        node.Go.transform.localScale = new Vector3(pScale * 1.5f, pScale * 0.25f, pScale * 0.85f);
                    else
                        node.Go.transform.localScale = new Vector3(pScale, pScale, pScale);

                    Color pCol = (i % 2 == 0) ? burst.PrimaryColor : burst.SecondaryColor;
                    node.Mat.color = new Color(pCol.r, pCol.g, pCol.b, 0.92f * fade);
                }
            }
        }

        static Mesh GetElementParticleMesh(string elementType)
        {
            switch (elementType)
            {
                case "Water":
                case "Psychic":
                case "Fire":
                    return sSphereMesh != null ? sSphereMesh : sCubeMesh;
                default:
                    return sCubeMesh;
            }
        }

        static void GetElementPalette(string elementType, out Color primary, out Color secondary)
        {
            switch (elementType)
            {
                case "Fire":
                    primary = new Color(1.00f, 0.36f, 0.10f, 1f);
                    secondary = new Color(1.00f, 0.84f, 0.18f, 1f);
                    break;
                case "Water":
                    primary = new Color(0.16f, 0.64f, 1.00f, 1f);
                    secondary = new Color(0.62f, 0.92f, 1.00f, 1f);
                    break;
                case "Grass":
                    primary = new Color(0.20f, 0.88f, 0.38f, 1f);
                    secondary = new Color(0.72f, 1.00f, 0.42f, 1f);
                    break;
                case "Lightning":
                    primary = new Color(1.00f, 0.90f, 0.16f, 1f);
                    secondary = new Color(0.88f, 0.96f, 1.00f, 1f);
                    break;
                case "Psychic":
                    primary = new Color(0.78f, 0.26f, 0.96f, 1f);
                    secondary = new Color(1.00f, 0.48f, 0.92f, 1f);
                    break;
                case "Fighting":
                    primary = new Color(0.88f, 0.46f, 0.18f, 1f);
                    secondary = new Color(1.00f, 0.74f, 0.36f, 1f);
                    break;
                default:
                    primary = new Color(0.92f, 0.95f, 1.00f, 1f);
                    secondary = new Color(1.00f, 0.88f, 0.45f, 1f);
                    break;
            }
        }

        static void EnsureVfxResources()
        {
            if (sVfxRoot == null)
            {
                var go = new GameObject("PokeTCG_ElementalVfxPool3D");
                UnityEngine.Object.DontDestroyOnLoad(go);
                sVfxRoot = go.transform;
            }
            if (sQuadMesh == null)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sQuadMesh = q.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(q);
            }
            if (sCubeMesh == null)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sCubeMesh = c.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(c);
            }
            if (sSphereMesh == null)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sSphereMesh = s.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(s);
            }
            if (sRingTex == null)
            {
                sRingTex = BuildRingTex(96);
            }
            if (sGlowTex == null)
            {
                sGlowTex = BuildGlowTex(64);
            }
        }

        static PooledVfxNode SpawnNode(Mesh mesh, Texture2D tex)
        {
            PooledVfxNode node;
            if (sInactiveNodes.Count > 0)
            {
                node = sInactiveNodes.Pop();
            }
            else
            {
                var go = new GameObject("VfxNode3D");
                go.transform.SetParent(sVfxRoot, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var mat = CardView.CreateUnlitMaterial(Color.white, null, true);
                mr.sharedMaterial = mat;
                node = new PooledVfxNode { Go = go, Filter = mf, Renderer = mr, Mat = mat };
            }

            node.Filter.sharedMesh = mesh;
            node.Mat.mainTexture = tex;
            if (!node.Go.activeSelf) node.Go.SetActive(true);
            sActiveNodes.Add(node);
            return node;
        }

        static void RecycleAllNodes()
        {
            for (int i = 0; i < sActiveNodes.Count; i++)
            {
                var n = sActiveNodes[i];
                if (n?.Go != null)
                {
                    n.Go.SetActive(false);
                    sInactiveNodes.Push(n);
                }
            }
            sActiveNodes.Clear();
        }

        static Texture2D BuildRingTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float band = 1f - Mathf.Clamp01(Mathf.Abs(r - 0.78f) / 0.16f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, band * band));
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D BuildGlowTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = Mathf.Pow(1f - r, 2.2f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        // ==================== F10: Panel Unificado de Opciones en Vivo ====================
        public static void DrawSettingsModal(ref bool isOpen)
        {
            if (!isOpen) return;
            GameUI.EnsureStyles();

            // Renderizar con la fuente nativa del juego (FredokaOne-Regular SDF) tanto en partida como en PokeDeck Builder
            var ui = GameUI.Instance;
            if (ui != null && (ui.IsMatchUiActive || (DeckBuilderUI.Instance != null && DeckBuilderUI.Instance.NeedsNativeOverlay)))
            {
                DrawSettingsModalNative(ui, ref isOpen);
                return;
            }

            float mw = 620f;
            float mh = 376f;
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);

            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            GUI.Box(mRect, "", GameUI.BoxBgStyle);

            GUILayout.BeginArea(new Rect(mRect.x + 22f, mRect.y + 16f, mRect.width - 44f, mRect.height - 32f));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=20><b>{Loc.T("Opciones", "Settings")}</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("Cerrar", "Close"), GameUI.BtnRedStyle, GUILayout.Width(105), GUILayout.Height(32)))
            {
                GameUI.PlayClickSound(2);
                isOpen = false;
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            // 1. Velocidad de Animación
            GUILayout.BeginVertical(GameUI.SubBoxStyle);
            GUILayout.Label($"<b>{Loc.T("Velocidad de Animación 3D:", "3D Animation Speed:")}</b> <color=#80ed99>{Loc.AnimSpeed:0.##}x</color>", GameUI.SubHeaderStyle);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            DrawSpeedBtn(0.75f, Loc.T("0.75x (Lenta)", "0.75x (Slow)"));
            DrawSpeedBtn(1.00f, Loc.T("1.0x (Normal)", "1.0x (Normal)"));
            DrawSpeedBtn(1.50f, Loc.T("1.5x (Rápida)", "1.5x (Fast)"));
            DrawSpeedBtn(2.00f, Loc.T("2.0x (Turbo)", "2.0x (Turbo)"));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // 2. Idioma de la Interfaz
            GUILayout.BeginVertical(GameUI.SubBoxStyle);
            GUILayout.Label($"<b>{Loc.T("Idioma de la Interfaz:", "Interface Language:")}</b>", GameUI.SubHeaderStyle);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Español (ES)", !Loc.IsEnglish ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Height(30)))
            {
                Loc.CurrentLang = "ES";
                GameUI.PlayClickSound(0);
            }
            GUILayout.Space(8);
            if (GUILayout.Button("English (EN)", Loc.IsEnglish ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Height(30)))
            {
                Loc.CurrentLang = "EN";
                GameUI.PlayClickSound(0);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // 3. Volumen de Efectos de Sonido (SFX) con Slider
            GUILayout.BeginVertical(GameUI.SubBoxStyle);
            int pct = Mathf.RoundToInt(Loc.SfxVolume * 100f);
            GUILayout.Label($"<b>{Loc.T("Volumen de Sonidos Pokémon TCG:", "Pokémon TCG Sound Effects Volume:")}</b> <color=#9ecbff>{pct}%</color>", GameUI.SubHeaderStyle);
            GUILayout.Space(8);
            float nextVol = GUILayout.HorizontalSlider(Loc.SfxVolume, 0f, 1f, GUILayout.Height(24));
            if (Mathf.Abs(nextVol - Loc.SfxVolume) > 0.001f)
            {
                Loc.SfxVolume = nextVol;
            }
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // 4. Sacudida de Cámara al KO e Impactos Fuertes
            GUILayout.BeginVertical(GameUI.SubBoxStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{Loc.T("Sacudida de Cámara al KO / Golpe:", "Camera Shake on KO / Heavy Hit:")}</b>", GameUI.SubHeaderStyle);
            GUILayout.FlexibleSpace();
            bool shake = Loc.CameraShakeEnabled;
            if (GUILayout.Button(shake ? Loc.T("Activada", "Enabled") : Loc.T("Desactivada", "Disabled"),
                shake ? GameUI.BtnGreenStyle : GameUI.BtnRedStyle, GUILayout.Width(150), GUILayout.Height(28)))
            {
                Loc.CameraShakeEnabled = !shake;
                if (Loc.CameraShakeEnabled)
                    TriggerCameraShake(0.018f, 0.30f);
                GameUI.PlayClickSound(0);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.EndArea();
        }

        static void DrawSettingsModalNative(GameUI ui, ref bool isOpen)
        {
            float mw = 640f;
            float mh = 398f;
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);

            // Fondo oscurecido y ventana principal
            ui.DrawNativeBox(new Rect(0f, 0f, Screen.width, Screen.height), GameUI.sDarkBackdropSprite, new Color(0.02f, 0.04f, 0.08f, 0.72f), false);
            ui.DrawNativeBox(mRect, GameUI.sPanelSprite, Color.white);

            float ix = mRect.x + 24f;
            float iw = mw - 48f;
            float curY = mRect.y + 18f;

            // Cabecera: Título "Settings" y botón "Close" con la fuente nativa del juego
            ui.DrawNativeLabel(new Rect(ix, curY, 340f, 38f), Loc.T("Opciones", "Settings"), 28f, TextAlignmentOptions.MidlineLeft, Color.white);
            if (ui.DrawNativeButton(new Rect(ix + iw - 118f, curY, 118f, 38f), Loc.T("Cerrar", "Close"), 19f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                isOpen = false;
                return;
            }
            curY += 50f;

            // 1. Velocidad de Animación 3D
            Rect sec1 = new Rect(ix, curY, iw, 82f);
            ui.DrawNativeBox(sec1, GameUI.sSubPanelSprite, Color.white);
            ui.DrawNativeLabel(
                new Rect(sec1.x + 14f, sec1.y + 8f, sec1.width - 28f, 26f),
                $"{Loc.T("Velocidad de Animación 3D:", "3D Animation Speed:")} <color=#80ed99>{Loc.AnimSpeed:0.##}x</color>",
                18f, TextAlignmentOptions.MidlineLeft, Color.white);

            float[] speeds = { 0.75f, 1.00f, 1.50f, 2.00f };
            string[] speedLabels =
            {
                Loc.T("0.75x (Lenta)", "0.75x (Slow)"),
                Loc.T("1.0x (Normal)", "1.0x (Normal)"),
                Loc.T("1.5x (Rápida)", "1.5x (Fast)"),
                Loc.T("2.0x (Turbo)", "2.0x (Turbo)")
            };
            float spdBtnW = (sec1.width - 28f - 24f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                bool active = Mathf.Abs(Loc.AnimSpeed - speeds[i]) < 0.08f;
                Rect br = new Rect(sec1.x + 14f + i * (spdBtnW + 8f), sec1.y + 38f, spdBtnW, 34f);
                if (ui.DrawNativeButton(br, speedLabels[i], 16f,
                    active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    Loc.AnimSpeed = speeds[i];
                    GameUI.PlayClickSound(0);
                }
            }
            curY += 92f;

            // 2. Idioma de la Interfaz
            Rect sec2 = new Rect(ix, curY, iw, 82f);
            ui.DrawNativeBox(sec2, GameUI.sSubPanelSprite, Color.white);
            ui.DrawNativeLabel(
                new Rect(sec2.x + 14f, sec2.y + 8f, sec2.width - 28f, 26f),
                Loc.T("Idioma de la Interfaz:", "Interface Language:"),
                18f, TextAlignmentOptions.MidlineLeft, Color.white);

            float langBtnW = (sec2.width - 28f - 12f) * 0.5f;
            bool isEs = !Loc.IsEnglish;
            if (ui.DrawNativeButton(new Rect(sec2.x + 14f, sec2.y + 38f, langBtnW, 34f), "Español (ES)", 17f,
                isEs ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                isEs ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                isEs ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
            {
                Loc.CurrentLang = "ES";
                GameUI.PlayClickSound(0);
            }
            if (ui.DrawNativeButton(new Rect(sec2.x + 14f + langBtnW + 12f, sec2.y + 38f, langBtnW, 34f), "English (EN)", 17f,
                !isEs ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                !isEs ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                !isEs ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
            {
                Loc.CurrentLang = "EN";
                GameUI.PlayClickSound(0);
            }
            curY += 92f;

            // 3. Volumen de Sonidos con Slider interactivo (sin fila Test SFX ni botones predeterminados)
            Rect sec3 = new Rect(ix, curY, iw, 82f);
            ui.DrawNativeBox(sec3, GameUI.sSubPanelSprite, Color.white);
            int pctNat = Mathf.RoundToInt(Loc.SfxVolume * 100f);
            ui.DrawNativeLabel(
                new Rect(sec3.x + 14f, sec3.y + 8f, sec3.width - 28f, 26f),
                $"{Loc.T("Volumen de Sonidos Pokémon TCG:", "Pokémon TCG Sound Effects Volume:")} <color=#9ecbff>{pctNat}%</color>",
                18f, TextAlignmentOptions.MidlineLeft, Color.white);

            ui.DrawNativeLabel(new Rect(sec3.x + 14f, sec3.y + 38f, 38f, 34f), "0%", 14.5f, TextAlignmentOptions.MidlineLeft, new Color(0.75f, 0.84f, 0.96f, 1f));
            ui.DrawNativeLabel(new Rect(sec3.xMax - 58f, sec3.y + 38f, 44f, 34f), "100%", 14.5f, TextAlignmentOptions.MidlineRight, new Color(0.75f, 0.84f, 0.96f, 1f));

            Rect sliderRect = new Rect(sec3.x + 48f, sec3.y + 38f, sec3.width - 108f, 34f);
            float newVol = ui.DrawNativeSlider(sliderRect, "PokeTCG_SfxVolumeSlider", Loc.SfxVolume, 0f, 1f, out bool volChanged);
            if (volChanged)
            {
                Loc.SfxVolume = newVol;
            }
            curY += 92f;

            // 4. Sacudida de Cámara al KO / Golpe
            Rect sec4 = new Rect(ix, curY, iw, 52f);
            ui.DrawNativeBox(sec4, GameUI.sSubPanelSprite, Color.white);
            ui.DrawNativeLabel(
                new Rect(sec4.x + 14f, sec4.y + 8f, sec4.width - 190f, 36f),
                Loc.T("Sacudida de Cámara al KO / Golpe:", "Camera Shake on KO / Heavy Hit:"),
                18f, TextAlignmentOptions.MidlineLeft, Color.white);

            bool shk = Loc.CameraShakeEnabled;
            if (ui.DrawNativeButton(
                new Rect(sec4.xMax - 166f, sec4.y + 8f, 152f, 36f),
                shk ? Loc.T("Activada", "Enabled") : Loc.T("Desactivada", "Disabled"),
                17f,
                shk ? GameUI.sBtnGreenN : GameUI.sBtnRedN,
                shk ? GameUI.sBtnGreenH : GameUI.sBtnRedH,
                shk ? GameUI.sBtnGreenA : GameUI.sBtnRedA))
            {
                Loc.CameraShakeEnabled = !shk;
                if (Loc.CameraShakeEnabled)
                    TriggerCameraShake(0.018f, 0.30f);
                GameUI.PlayClickSound(0);
            }

            if (Event.current != null && Event.current.type == EventType.MouseDown && mRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
            }
        }

        static void DrawSpeedBtn(float targetSpeed, string label)
        {
            bool active = Mathf.Abs(Loc.AnimSpeed - targetSpeed) < 0.08f;
            if (GUILayout.Button(label, active ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Height(30)))
            {
                Loc.AnimSpeed = targetSpeed;
                GameUI.PlayClickSound(0);
            }
        }
    }
}
