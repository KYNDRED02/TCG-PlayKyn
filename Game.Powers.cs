// PokeTCGPlay: Activated Pokemon Powers resolution and on-play card triggers
using System;
using System.Collections.Generic;
using System.Linq;

namespace PokeTCGPlay
{
    /// <summary>
    /// Submódulo de Poderes Pokémon (Poké-Powers / Habilidades) de la Era Base de MyCurio.
    /// 
    /// ¿Para qué sirve este archivo?
    /// - Define qué habilidades requieren ser activadas manualmente por el jugador durante su turno.
    /// - Resuelve el efecto concreto de cada habilidad activa (Damage Swap, Rain Dance, Energy Trans, etc.).
    /// - Ejecuta las habilidades que se disparan al bajar o evolucionar de la mano (On-Play como Sneak Attack de Dark Golbat).
    /// - Controla que la habilidad no esté bloqueada por estados (Dormido, Confuso, Paralizado) ni por Toxic Gas de Muk.
    /// 
    /// Si necesitas agregar o modificar una habilidad:
    /// - Regístrala en 'ActivatedPowers' si lleva botón manual.
    /// - Agrega su efecto en el switch de 'UsePokemonPower'.
    /// </summary>
    public partial class Game
    {
        // Lista de habilidades que el jugador activa manualmente con un botón durante su turno
        public static readonly HashSet<string> ActivatedPowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Damage Swap",
            "Rain Dance",
            "Energy Burn",
            "Energy Trans",
            "Buzzap",
            "Shift",
            "Heal",
            "Peek",
            "Step In",
            "Curse",
            "Strange Behavior",
            "Cowardice",
            "Evolutionary Light",
            "Pollen Stench",
            "Matter Exchange",
            "Gather Fire",
            "Long-Distance Hypnosis",
            "Trickery",
            "Special Delivery",
            "Solar Power",
            "Energy Charge",
            "Fragrance Trap",
            "Natural Healing",
            "Shapeshift",
            "Soak Up",
            "Emerge"
        };

        /// <summary>
        /// Comprueba si un poder Pokémon requiere botón de activación manual en la interfaz.
        /// </summary>
        public bool IsActivatedPower(string powerName)
        {
            if (string.IsNullOrEmpty(powerName)) return false;
            return ActivatedPowers.Contains(powerName);
        }

        /// <summary>
        /// Ejecuta una habilidad Pokémon manual elegida por el jugador desde el botón en la tarjeta o panel lateral.
        /// </summary>
        public string UsePokemonPower(PlayerState p, PokemonInPlay user, PokemonInPlay target = null)
        {
            if (user == null || user.Card == null) return "No se encontró el Pokémon.";
            var power = user.Card.Power ?? user.Card.Abilities.FirstOrDefault();
            if (power == null) return $"{user.Card.Name} no tiene ninguna habilidad.";

            string pname = power.Name;

            // 1. Validaciones básicas de turno y estado
            if (Phase != Phase.Main) return "Solo puedes usar habilidades durante tu turno principal.";
            if (Current != p) return "No es tu turno.";
            if (IsBusyResolving) return "Espera a que termine la acción actual.";
            if (!p.InPlay.Contains(user)) return $"{user.Card.Name} no está en tu campo de batalla.";
            if (IsPowerDisabled(user)) return $"La habilidad de {user.Card.Name} está bloqueada por un estado alterado o por Gas Tóxico.";

            var opp = OpponentOf(p);

            switch (pname)
            {
                case "Rain Dance":
                    {
                        // Danza de la Lluvia de Blastoise: permite unir todas las energías de agua deseadas a Pokémon de agua
                        p.RainDanceActive = true;
                        Say($"¡Danza de la Lluvia (Rain Dance) de {user.Card.Name} activada! Puedes unir tantas Energías de Agua a tus Pokémon de Agua como quieras este turno.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Energy Burn":
                    {
                        // Quemadura de Energía de Charizard: convierte toda la energía unida en fuego
                        Say($"¡Quemadura de Energía (Energy Burn) activada! Toda la energía unida a {user.Card.Name} proporciona Fuego este turno.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Damage Swap":
                    {
                        // Intercambio de Daño de Alakazam: mueve 10 de daño de un Pokémon propio a otro sin noquearlo
                        var damaged = p.InPlay.Where(x => x.Damage > 0).OrderByDescending(x => x.Damage).FirstOrDefault();
                        var destination = p.InPlay.Where(x => x != damaged && x.RemainingHp > 10).OrderBy(x => x.Damage).FirstOrDefault();

                        if (damaged == null) return "No tienes ningún Pokémon con daño para mover.";
                        if (destination == null) return "No hay otro Pokémon que pueda recibir daño sin ser noqueado.";

                        damaged.Damage -= 10;
                        destination.Damage += 10;
                        Say($"{p.Name} usa Damage Swap: mueve 10 de daño de {damaged.Card.Name} a {destination.Card.Name}.");
                        TcgAudio.PlayCardPlace();
                        CheckKnockOuts();
                        break;
                    }

                case "Energy Trans":
                    {
                        // Traslado de Energía de Venusaur: mueve 1 energía de planta entre tus Pokémon
                        var source = p.InPlay.FirstOrDefault(x => x.Energy.Any(e => e.EnergyProvides == "Grass"));
                        if (source == null) return "No tienes ninguna Energía Planta unida a tus Pokémon para mover.";

                        var dest = target ?? p.InPlay.FirstOrDefault(x => x != source);
                        if (dest == null) return "No tienes otro Pokémon al que transferir la energía.";

                        var grassEnergy = source.Energy.First(e => e.EnergyProvides == "Grass");
                        source.Energy.Remove(grassEnergy);
                        dest.Energy.Add(grassEnergy);
                        Say($"{p.Name} usa Energy Trans: mueve 1 Energía Planta de {source.Card.Name} a {dest.Card.Name}.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Buzzap":
                    {
                        // Buzzap de Electrode: noquea a Electrode y lo une a otro Pokémon como energía doble
                        var dest = target ?? p.InPlay.FirstOrDefault(x => x != user);
                        if (dest == null) return "Necesitas tener otro Pokémon en juego para unir a Electrode.";

                        dest.Attached.Add(user.Card);
                        user.Damage = user.Card.Hp; // Auto-noqueo
                        Say($"¡{user.Card.Name} usa Buzzap! Se auto-noquea y se une a {dest.Card.Name} como carta de Energía doble.");
                        TcgAudio.PlayAttackImpact("Colorless", 10);
                        CheckKnockOuts();
                        break;
                    }

                case "Shift":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Shift solo puede usarse una vez por turno.";
                        // Cambio de tipo de Venomoth: copia el tipo de otro Pokémon en juego no incoloro
                        var otherTypes = Players.SelectMany(pl => pl.InPlay)
                            .Where(x => x != user && !string.IsNullOrEmpty(x.Card.Type) && x.Card.Type != "Colorless")
                            .Select(x => x.Card.Type).Distinct().ToList();

                        if (otherTypes.Count == 0) return "No hay otros Pokémon en juego con tipo elemental aplicable.";

                        int nextIdx = (otherTypes.IndexOf(user.TemporaryType ?? "") + 1) % otherTypes.Count;
                        user.TemporaryType = otherTypes[nextIdx];
                        user.PowersUsedThisTurn.Add(pname);
                        Say($"¡Venomoth usa Shift! Su tipo elemental ahora es {user.TemporaryType}.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Heal":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Heal solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        var damaged = p.InPlay.Where(x => x.Damage > 0).OrderByDescending(x => x.Damage).FirstOrDefault();
                        if (damaged == null) return "Ninguno de tus Pokémon tiene daño para curar.";

                        RunCoinFlip(p, "Heal de Vileplume", heads =>
                        {
                            if (heads)
                            {
                                damaged.Damage = Math.Max(0, damaged.Damage - 10);
                                Say($"  ¡Caras! Heal cura 10 de daño a {damaged.Card.Name}.");
                                TcgAudio.PlayCardPlace();
                            }
                            else
                            {
                                Say("  Cruz. Heal no tuvo efecto.");
                            }
                        });
                        break;
                    }

                case "Natural Healing":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Natural Healing solo puede usarse una vez por turno.";
                        if (user.Damage <= 0) return $"{user.Card.Name} no tiene daño para curar.";

                        user.Damage = Math.Max(0, user.Damage - 10);
                        user.PowersUsedThisTurn.Add(pname);
                        Say($"{user.Card.Name} usa Natural Healing y se cura 10 PS.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Peek":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Peek solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        if (opp.Deck.Count > 0)
                        {
                            var top = opp.Deck[0];
                            Say($"Peek de Mankey: la carta superior del mazo del rival es '{top.Name}'.");
                        }
                        else if (opp.Hand.Count > 0)
                        {
                            var randCard = opp.Hand[Rng.Next(opp.Hand.Count)];
                            Say($"Peek de Mankey: una carta de la mano del rival es '{randCard.Name}'.");
                        }
                        else
                        {
                            Say("Peek de Mankey: el rival no tiene cartas en mazo ni mano para espiar.");
                        }
                        TcgAudio.PlayCardDraw();
                        break;
                    }

                case "Step In":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Step In solo puede usarse una vez por turno.";
                        if (user == p.Active) return "Dragonite ya es tu Pokémon Activo.";

                        int bIdx = p.Bench.IndexOf(user);
                        if (bIdx < 0) return "Dragonite debe estar en la Banca para usar Step In.";

                        user.PowersUsedThisTurn.Add(pname);
                        // Intercambia a Dragonite al puesto activo sin pagar coste de retirada
                        var oldActive = p.Active;
                        p.Active = user;
                        p.Bench[bIdx] = oldActive;
                        Say($"¡Dragonite usa Step In y pasa al frente como Pokémon Activo!");
                        Emit(AnimKind.RetreatSwap, p, user.Card, bIdx);
                        break;
                    }

                case "Curse":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Curse solo puede usarse una vez por turno.";
                        var damaged = opp.InPlay.Where(x => x.Damage > 0).OrderByDescending(x => x.Damage).FirstOrDefault();
                        var destination = opp.InPlay.FirstOrDefault(x => x != damaged);

                        if (damaged == null) return "El oponente no tiene ningún Pokémon con daño para mover.";
                        if (destination == null) return "El oponente no tiene otro Pokémon al que mover el daño.";

                        user.PowersUsedThisTurn.Add(pname);
                        damaged.Damage -= 10;
                        destination.Damage += 10;
                        Say($"{p.Name} usa Curse de Gengar: mueve 10 de daño de {damaged.Card.Name} a {destination.Card.Name}.");
                        TcgAudio.PlayAttackImpact("Colorless", 10);
                        CheckKnockOuts();
                        break;
                    }

                case "Strange Behavior":
                    {
                        if (user.RemainingHp <= 10) return "Slowbro no puede recibir más daño sin ser noqueado.";
                        var source = p.InPlay.Where(x => x != user && x.Damage > 0).OrderByDescending(x => x.Damage).FirstOrDefault();
                        if (source == null) return "No tienes otros Pokémon con daño para transferir a Slowbro.";

                        source.Damage -= 10;
                        user.Damage += 10;
                        Say($"{p.Name} usa Strange Behavior: pasa 10 de daño de {source.Card.Name} a Slowbro.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Cowardice":
                    {
                        if (user.TurnPlaced == Turn) return "Tentacool no puede usar Cowardice en el mismo turno en que entró en juego.";
                        if (user == p.Active) return "Tentacool debe estar en la Banca para usar Cowardice.";

                        p.Bench.Remove(user);
                        p.Hand.Add(user.Card);
                        foreach (var att in user.Attached.Concat(user.Energy)) p.Discard.Add(att);
                        Say($"¡Tentacool usa Cowardice y regresa a la mano de {p.Name}!");
                        TcgAudio.PlayCardDraw();
                        break;
                    }

                case "Evolutionary Light":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Evolutionary Light solo puede usarse una vez por turno.";
                        var evoCard = p.Deck.FirstOrDefault(c => c.Supertype == Supertype.Pokemon && c.Stage != "Basic");
                        if (evoCard == null) return "No quedan cartas de Evolución en tu mazo.";

                        user.PowersUsedThisTurn.Add(pname);
                        p.Deck.Remove(evoCard);
                        p.Hand.Add(evoCard);
                        Shuffle(p.Deck);
                        Say($"{p.Name} usa Evolutionary Light: busca a '{evoCard.Name}' en su mazo y lo añade a la mano.");
                        TcgAudio.PlayCardDraw();
                        break;
                    }

                case "Pollen Stench":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Pollen Stench solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        RunCoinFlip(p, "Pollen Stench de Dark Gloom", heads =>
                        {
                            if (heads)
                            {
                                if (opp.Active != null)
                                {
                                    opp.Active.Status = Status.Confused;
                                    Say($"  ¡Caras! Pollen Stench confunde al Pokémon Activo del rival ({opp.Active.Card.Name}).");
                                }
                            }
                            else
                            {
                                if (p.Active != null)
                                {
                                    p.Active.Status = Status.Confused;
                                    Say($"  Cruz. Pollen Stench confunde a tu propio Pokémon Activo ({p.Active.Card.Name}).");
                                }
                            }
                        });
                        break;
                    }

                case "Matter Exchange":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Matter Exchange solo puede usarse una vez por turno.";
                        if (p.Hand.Count == 0) return "No tienes cartas en la mano para descartar.";

                        user.PowersUsedThisTurn.Add(pname);
                        var discarded = p.Hand[0];
                        p.Hand.RemoveAt(0);
                        p.Discard.Add(discarded);
                        Draw(p, 1);
                        Say($"{p.Name} usa Matter Exchange: descarta '{discarded.Name}' y roba 1 carta.");
                        break;
                    }

                case "Gather Fire":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Gather Fire solo puede usarse una vez por turno.";
                        var otherWithFire = p.InPlay.FirstOrDefault(x => x != user && x.Energy.Any(e => e.EnergyProvides == "Fire"));
                        if (otherWithFire == null) return "No tienes otros Pokémon con Energía Fuego para mover.";

                        user.PowersUsedThisTurn.Add(pname);
                        var fireE = otherWithFire.Energy.First(e => e.EnergyProvides == "Fire");
                        otherWithFire.Energy.Remove(fireE);
                        user.Energy.Add(fireE);
                        Say($"{user.Card.Name} usa Gather Fire: toma 1 Energía Fuego de {otherWithFire.Card.Name}.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Long-Distance Hypnosis":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Long-Distance Hypnosis solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        RunCoinFlip(p, "Long-Distance Hypnosis de Drowzee", heads =>
                        {
                            if (heads)
                            {
                                if (opp.Active != null)
                                {
                                    opp.Active.Status = Status.Asleep;
                                    Say($"  ¡Caras! {opp.Active.Card.Name} se duerme.");
                                }
                            }
                            else
                            {
                                if (p.Active != null)
                                {
                                    p.Active.Status = Status.Asleep;
                                    Say($"  Cruz. Tu propio {p.Active.Card.Name} se duerme.");
                                }
                            }
                        });
                        break;
                    }

                case "Trickery":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Trickery solo puede usarse una vez por turno.";
                        if (p.Prizes.Count == 0 || p.Deck.Count == 0) return "No hay cartas suficientes en Premios o Mazo para intercambiar.";

                        user.PowersUsedThisTurn.Add(pname);
                        var topDeck = p.Deck[0];
                        var prize = p.Prizes[0];
                        p.Deck[0] = prize;
                        p.Prizes[0] = topDeck;
                        Say($"{p.Name} usa Trickery de Rattata: intercambia 1 carta de Premio con el tope de su mazo.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Special Delivery":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Special Delivery solo puede usarse una vez por turno.";
                        if (p.Deck.Count == 0) return "Tu mazo está vacío.";

                        user.PowersUsedThisTurn.Add(pname);
                        Draw(p, 1);
                        if (p.Hand.Count > 0)
                        {
                            var returnCard = p.Hand[p.Hand.Count - 1];
                            p.Hand.RemoveAt(p.Hand.Count - 1);
                            p.Deck.Insert(0, returnCard);
                            Say($"{p.Name} usa Special Delivery: roba 1 carta y devuelve '{returnCard.Name}' a la parte superior del mazo.");
                        }
                        break;
                    }

                case "Solar Power":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Solar Power solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        p.Active?.ClearConditions();
                        opp.Active?.ClearConditions();
                        Say($"¡Poder Solar (Solar Power) de Venusaur cura todos los estados alterados de ambos Pokémon Activos!");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Energy Charge":
                    {
                        var otherWithLight = p.InPlay.FirstOrDefault(x => x != user && x.Energy.Any(e => e.EnergyProvides == "Lightning"));
                        if (otherWithLight == null) return "No tienes otros Pokémon con Energía Rayo para mover.";

                        var lightE = otherWithLight.Energy.First(e => e.EnergyProvides == "Lightning");
                        otherWithLight.Energy.Remove(lightE);
                        user.Energy.Add(lightE);
                        Say($"{user.Card.Name} usa Energy Charge: toma 1 Energía Rayo de {otherWithLight.Card.Name}.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Fragrance Trap":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Fragrance Trap solo puede usarse una vez por turno.";
                        if (opp.Bench.Count == 0) return "El oponente no tiene Pokémon en la Banca.";

                        user.PowersUsedThisTurn.Add(pname);
                        RunCoinFlip(p, "Fragrance Trap de Erika's Victreebel", heads =>
                        {
                            if (heads)
                            {
                                int targetBench = Rng.Next(opp.Bench.Count);
                                ForceSwitch(opp, targetBench);
                                Say($"  ¡Caras! Fragrance Trap arrastra al combate a {opp.Active.Card.Name}.");
                            }
                            else
                            {
                                Say("  Cruz. Fragrance Trap falló.");
                            }
                        });
                        break;
                    }

                case "Shapeshift":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Shapeshift solo puede usarse una vez por turno.";
                        var evoInHand = p.Hand.FirstOrDefault(c => c.Supertype == Supertype.Pokemon && c.Stage != "Basic");
                        if (evoInHand == null) return "No tienes ninguna carta de Evolución en la mano para Shapeshift.";

                        user.PowersUsedThisTurn.Add(pname);
                        p.Hand.Remove(evoInHand);
                        user.ShiftCard = evoInHand;
                        user.Attached.Add(evoInHand);
                        Say($"¡Brock's Ninetales usa Shapeshift y se transforma en '{evoInHand.Name}'!");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Soak Up":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Soak Up solo puede usarse una vez por turno.";
                        var othersWithGrass = p.InPlay.Where(x => x != user && x.Energy.Any(e => e.EnergyProvides == "Grass")).ToList();
                        if (othersWithGrass.Count == 0) return "No tienes otros Pokémon con Energía Planta para absorber.";

                        user.PowersUsedThisTurn.Add(pname);
                        int absorbed = 0;
                        foreach (var other in othersWithGrass)
                        {
                            while (absorbed < 2 && other.Energy.Any(e => e.EnergyProvides == "Grass"))
                            {
                                var gE = other.Energy.First(e => e.EnergyProvides == "Grass");
                                other.Energy.Remove(gE);
                                user.Energy.Add(gE);
                                absorbed++;
                            }
                            if (absorbed >= 2) break;
                        }
                        Say($"{user.Card.Name} usa Soak Up: absorbe {absorbed} Energía Planta de sus compañeros.");
                        TcgAudio.PlayCardPlace();
                        break;
                    }

                case "Emerge":
                    {
                        if (user.PowersUsedThisTurn.Contains(pname)) return "Emerge solo puede usarse una vez por turno.";
                        user.PowersUsedThisTurn.Add(pname);

                        RunCoinFlip(p, "Emerge de Koga's Kakuna", heads =>
                        {
                            if (heads)
                            {
                                var beedrill = p.Deck.FirstOrDefault(c => c.Name.IndexOf("Beedrill", StringComparison.OrdinalIgnoreCase) >= 0);
                                if (beedrill != null)
                                {
                                    p.Deck.Remove(beedrill);
                                    user.Stack.Add(beedrill);
                                    user.ClearConditions();
                                    Shuffle(p.Deck);
                                    Say($"  ¡Caras! Koga's Kakuna emerge y evoluciona en '{beedrill.Name}'.");
                                    Emit(AnimKind.Evolve, p, beedrill, p.Bench.IndexOf(user));
                                }
                                else
                                {
                                    Say("  ¡Caras! Pero no se encontró a Koga's Beedrill en el mazo.");
                                }
                            }
                            else
                            {
                                Say("  Cruz. Emerge no tuvo efecto.");
                            }
                        });
                        break;
                    }

                default:
                    return $"Habilidad '{pname}' no requiere activación manual o es pasiva continua.";
            }

            return null;
        }

        /// <summary>
        /// Dispara habilidades automáticas al entrar en juego desde la mano (On-Play).
        /// </summary>
        public void TriggerOnPlayPower(PlayerState p, CardDef c, PokemonInPlay pip)
        {
            if (c == null || pip == null) return;
            var power = c.Power ?? c.Abilities.FirstOrDefault();
            if (power == null) return;
            if (IsPowerDisabled(pip)) return;

            var opp = OpponentOf(p);

            switch (power.Name)
            {
                case "Sneak Attack":
                    {
                        // Dark Golbat: inflige 10 de daño al entrar en juego a 1 Pokémon rival
                        var target = opp.Active ?? opp.Bench.FirstOrDefault();
                        if (target != null)
                        {
                            target.Damage += 10;
                            Say($"¡Ataque Furtivo (Sneak Attack) de Dark Golbat inflige 10 de daño a {target.Card.Name}!");
                            TcgAudio.PlayAttackImpact("Colorless", 10);
                            CheckKnockOuts();
                        }
                        break;
                    }

                case "Reel In":
                    {
                        // Dark Slowbro: recupera hasta 3 Básicos o Evoluciones del descarte a la mano
                        var monsInDiscard = p.Discard.Where(x => x.Supertype == Supertype.Pokemon).Take(3).ToList();
                        foreach (var mon in monsInDiscard)
                        {
                            p.Discard.Remove(mon);
                            p.Hand.Add(mon);
                        }
                        if (monsInDiscard.Count > 0)
                        {
                            Say($"¡Reel In de Dark Slowbro recupera {monsInDiscard.Count} Pokémon del descarte a la mano!");
                            TcgAudio.PlayCardDraw();
                        }
                        break;
                    }

                case "Summon Minions":
                    {
                        // Dark Dragonite: busca hasta 2 Pokémon Básicos en el mazo y los baja a la banca
                        int canAdd = Math.Min(2, 5 - p.Bench.Count);
                        var basicsInDeck = p.Deck.Where(x => x.IsBasicPokemon).Take(canAdd).ToList();
                        foreach (var b in basicsInDeck)
                        {
                            p.Deck.Remove(b);
                            var newPip = new PokemonInPlay { TurnPlaced = Turn };
                            newPip.Stack.Add(b);
                            p.Bench.Add(newPip);
                            Emit(AnimKind.ToBench, p, b, p.Bench.Count - 1);
                        }
                        if (basicsInDeck.Count > 0)
                        {
                            Shuffle(p.Deck);
                            Say($"¡Summon Minions de Dark Dragonite convoca a {basicsInDeck.Count} Pokémon Básicos a la banca!");
                        }
                        break;
                    }

                case "Call the Boss":
                    {
                        // Giovanni's Persian: busca la carta de Entrenador Giovanni en el mazo
                        var giovanni = p.Deck.FirstOrDefault(x => x.Name.IndexOf("Giovanni", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (giovanni != null)
                        {
                            p.Deck.Remove(giovanni);
                            p.Hand.Add(giovanni);
                            Shuffle(p.Deck);
                            Say($"¡Call the Boss de Giovanni's Persian busca y añade a Giovanni a la mano!");
                            TcgAudio.PlayCardDraw();
                        }
                        break;
                    }
            }
        }
    }
}
