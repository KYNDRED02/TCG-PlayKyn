// TCG-PlayKyn: Centralized tweakable 3D visual scales, card heights and animation timings
using System;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// Configuración central de ajustes visuales, escalas y posiciones 3D del mod.
    /// Si necesitas calibrar la altura de las cartas, el tamaño del tapete o las velocidades
    /// de animación, puedes modificar directamente los valores de este archivo.
    /// </summary>
    public static class VisualSettings
    {
        // =========================================================================
        // 1. ESCALAS DE LAS CARTAS EN LA MESA 3D
        // =========================================================================

        // Tamaño de la carta activa en el centro del campo de batalla
        public static float EscalaCartaActiva = 1.08f;

        // Tamaño de las cartas colocadas en la banca (ligeramente más pequeñas para no estorbar)
        public static float EscalaCartaBanca = 0.94f;

        // Tamaño de las cartas de premio a la izquierda del tapete
        public static float EscalaCartaPremio = 0.88f;

        // Tamaño de la carta de estadio colocada a la derecha del campo
        public static float EscalaCartaEstadio = 0.95f;

        // Tamaño de las cartas en la mano del jugador
        public static float EscalaCartaMano = 1.00f;

        // Grosor físico del cartón de las cartas en el espacio 3D
        public static float GrosorCarta = 0.00032f;

        // Grosor del tapete físico de juego sobre la mesa
        public static float GrosorTapete = 0.0016f;


        // =========================================================================
        // 2. POSICIÓN Y ÁNGULO DE LA MANO 3D DEL JUGADOR
        // =========================================================================

        // Altura vertical de la mano del jugador frente a la cámara (más alto se acerca a la vista)
        public static float AlturaManoJugador = 0.066f;

        // Ángulo de inclinación de las cartas de la mano hacia los ojos del jugador
        public static float InclinacionManoJugador = -44.5f;

        // Elevación extra que sube una carta de la mano cuando pasas el ratón por encima
        public static float ElevacionHoverMano = 0.028f;

        // Separación horizontal máxima entre las cartas de la mano en abanico
        public static float SeparacionAbanicoMano = 0.042f;


        // =========================================================================
        // 3. VELOCIDADES Y TIEMPOS DE ANIMACIÓN (En segundos)
        // =========================================================================

        // Duración del vuelo de una carta cuando se juega desde la mano a la mesa
        public static float DuracionVueloJugarCarta = 0.38f;

        // Duración de la animación de retirada / intercambio de Pokémon activo con la banca
        public static float DuracionCambioBanca = 0.45f;

        // Duración del despegue de las 5 cartas de Pokédex desde la baraja hacia el centro
        public static float DuracionVueloPokedex = 0.40f;

        // Opacidad máxima del fondo oscurecido al inspeccionar una carta o usar Pokédex (0.0 = transparente, 1.0 = negro)
        public static float OpacidadFondoOscurecido = 0.65f;


        // =========================================================================
        // 4. CÁMARA Y SACUDIDA DE IMPACTO (Screen Shake)
        // =========================================================================

        // Fuerza de la sacudida de la cámara cuando un Pokémon queda Fuera de Combate (K.O.)
        public static float IntensidadSacudidaKO = 0.025f;

        // Duración en segundos de la sacudida de pantalla tras un golpe fuerte o K.O.
        public static float DuracionSacudidaKO = 0.35f;
    }
}
