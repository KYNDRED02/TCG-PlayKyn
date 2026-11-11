// TCG-PlayKyn: Attack entry point, damage calculation and status application helpers
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TCGPlayKyn
{
    /// <summary>
    /// Controlador principal de efectos de cartas (ataques, entrenadores y condiciones de estado).
    /// El cÃ³digo de resoluciÃ³n estÃ¡ dividido en submÃ³dulos temÃ¡ticos:
    /// - Effects.Attacks.cs: Maneja el daÃ±o, monedas y efectos secundarios de los ataques.
    /// - Effects.Trainers.cs: Maneja el efecto de cada carta de Entrenador y Estadio.
    /// </summary>
    public static partial class Effects
    {
        // Opciones de Regex para ignorar mayÃºsculas y minÃºsculas al leer textos de cartas
        static readonly RegexOptions I = RegexOptions.IgnoreCase;

        // Convierte el texto de una carta ("Paralyzed", "Confused", "Asleep") al enum correspondiente
        static Status? ParseStatus(string s)
        {
            if (s.Contains("Paralyzed")) return Status.Paralyzed;
            if (s.Contains("Confused")) return Status.Confused;
            if (s.Contains("Asleep")) return Status.Asleep;
            return null;
        }

        // Aplica una condiciÃ³n especial (Veneno, SueÃ±o, etc.) verificando si el objetivo previene efectos
        static void ApplyCondition(Game g, PokemonInPlay target, string text)
        {
            if (target == null) return;
                        // Si el Pokémon tiene activo un efecto de prevención como Agility o Barrier, no recibe estados
            if (target.PreventAllUntilTurn >= g.Turn)
            {
                g.Say($"  ¡{target.Card.Name} previene los efectos de ataques!");
                return;
            }

            // Piel Gruesa (Thick Skinned) de Snorlax: inmune a Dormido, Confuso, Paralizado y Envenenado
            if (target.Card.Power?.Name == "Thick Skinned" && !g.IsPowerDisabled(target))
            {
                g.Say($"  ¡Piel Gruesa (Thick Skinned) de {target.Card.Name} previene las condiciones especiales!");
                return;
            }
            if (text.Contains("Poisoned"))
            {
                target.Poisoned = true;
                if (text.Contains("20 Poison damage"))
                    target.ToxicPoison = true; // Efecto de TÃ³xico (Nidoking): 20 de veneno por turno
                g.Say($"  {target.Card.Name} queda Envenenado{(target.ToxicPoison ? " (TÃ³xico)" : "")}.");
            }
            var st = ParseStatus(text);
            if (st != null)
            {
                target.Status = st.Value;
                target.StatusTurns = st.Value == Status.Paralyzed ? 1 : 0;
                g.Say($"  {target.Card.Name} queda {st}.");
            }
        }

        // Punto de entrada rÃ¡pido para resolver un ataque sin callback de finalizaciÃ³n
        public static void ResolveAttack(Game g, PlayerState p, PokemonInPlay a, AttackDef atk)
        {
            ResolveAttackSequence(g, p, a, atk, null);
        }
    }
}