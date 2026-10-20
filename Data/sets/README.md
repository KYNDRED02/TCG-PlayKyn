# Cómo agregar una expansión a PokeTCGPlay

Las **imágenes** salen siempre del mod de **MyCurio** instalado (`BepInEx/plugins/MyCurioCardSets_prefabLoader`).
PokeTCGPlay solo necesita las **reglas de juego** de cada carta.

## 1. Crea una carpeta
`Data/sets/<id>/` (por ejemplo `Data/sets/me01/`) con estos archivos:

### `set.json`
```json
{
  "id": "me01",
  "name": "Mega Evolution",
  "order": 100,
  "mycurioExpansion": "MyCurioPokemon-ME01",
  "spritePrefix": "ME01-",
  "spriteDigits": 3,
  "effectsImplemented": false
}
```
- `mycurioExpansion`: el valor `CardExpansion` del `.json` de MyCurio (las expansiones detectadas aparecen en `BepInEx/LogOutput.log`).
- `spritePrefix` + `spriteDigits`: cómo se llama la imagen de MyCurio. La carta nº 5 queda como `ME01-005`.
- `effectsImplemented`: déjalo en `false` hasta que los efectos de la expansión estén programados. Mientras tanto sus cartas se cargan pero no se pueden meter en mazos.

### `cards.json` (opcional)
Las reglas en formato **pokemontcg.io**. Puedes descargarlas tal cual de
<https://github.com/PokemonTCG/pokemon-tcg-data/tree/master/cards/en> (por ejemplo `me1.json`) y renombrar el archivo a `cards.json`.

### `overrides.json` (opcional)
Para **corregir** o **agregar a mano** cartas. Solo escribe los campos que cambian:
```json
{
  "cards": [
    { "id": "me01-5", "hp": "90" },
    { "number": "200", "sprite": "MEP-010", "name": "Riolu", "supertype": "Pokémon",
      "subtypes": ["Basic"], "hp": "70", "types": ["Fighting"],
      "attacks": [ { "name": "Punch", "cost": ["Fighting"], "damage": "20", "text": "" } ],
      "weaknesses": [ { "type": "Psychic", "value": "×2" } ],
      "retreatCost": ["Colorless"] }
  ]
}
```
- Si el `id` (o `number`) ya existe, se mezcla con la carta original. Si no existe, se agrega como carta nueva.
- `sprite` fuerza qué imagen de MyCurio usar (útil para promos `MEP-xxx` dentro de otras expansiones).
- `"unsupported": "motivo"` desactiva una carta concreta.

## 2. Formatos
`Data/formats/*.json` define qué expansiones se permiten:
```json
{ "Id": "mega", "Name": "Mega Evolution", "Sets": ["me01", "me02"], "Banned": [], "DeckSize": 60, "MaxCopies": 4, "MinBasicPokemon": 1 }
```
`"Sets": ["*"]` = todas.

## 3. Mazos
Tus mazos se guardan en `BepInEx/config/PokeTCGPlay/decks/*.deck.json` (se conservan al actualizar el mod).

## Mecánicas futuras
Las cartas con subtipos `EX`, `ex`, `MEGA`, `V`, `VMAX`, `VSTAR`, `GX`... se cargan pero quedan marcadas como **no jugables** hasta que se implemente su mecánica.
