# TCG-PlayKyn — Pokémon TCG for *TCG Card Shop Simulator*

**TCG-PlayKyn** is a mod for *TCG Card Shop Simulator* (using BepInEx 5) that transforms the gaming tables in your shop into fully interactive, 3D matches of the **Pokémon Trading Card Game**, using the cards you’ve pulled from booster packs and expansion sets—including those from **MyCurio**.

---

## Project Origin and Development

The mod began as a prototype and rules simulator programmed in **Python using Visual Studio Code**. To integrate it natively—complete with 3D graphics—into the game’s Unity engine, the project was ported, adapted, and optimized for **C# (.NET / BepInEx)**, with some assistance from **AI** (I’m still learning C#, so please bear with me! :S).

---

## What does the mod offer?

1. **Play with your own collection:**
- To build a deck, you must own the cards within your save file (whether they are stored in your **Album**, kept in **Storage Boxes**, or displayed on your **Shelves**). 
- Basic Energy cards are unlimited, just as they are in official games and tournaments.

2. **Compatibility with MyCurio expansions:**
- It automatically detects all Pokémon card expansions installed in your game (e.g., *Base Set*, *Jungle*, *Fossil*, *Base Set 2*, *Team Rocket*, *Gym Heroes*, *Gym Challenge*, and promotional sets). 
- Cards are displayed with high-quality artwork, including their original borders and holographic finishes.

3. **3D Deck Builder and `.txt` list importer:**
- You can create and manage multiple 60-card decks directly from the **Workbench** in your shop. - **Quick Importer:** You can export a deck list from *Pokémon TCG Live* or *PTCGO* in text format (`.txt`), paste it into the mod’s folder, and load it instantly. If you are missing any cards, the game will specify exactly which ones they are and which booster pack you can find them in.

4. **3D Matches on the Shop Table:**
- When you sit down to play against a customer, a physical **playmat** unfolds, featuring raised cards, shadows, animations for drawing cards toward the screen, and a fan-view display of your hand. 
- **3D Coin with Realistic Physics:** Perform parabolic coin flips (featuring Pikachu and Poké Ball faces) to determine turns, attack outcomes, and special effects. 
- **3D Counters and Tokens:** Damage counters and status markers (Asleep, Confused, Paralyzed, Poisoned) that physically rotate the card on the playmat. 
- **3D Stadium Cards:** Stadium cards are placed physically on the side of the playmat and apply their rules to both players.

5. **Full Mechanics from the Base Era:**
- Fully functional attacks, Trainer cards, Supporters, and Stadiums. 
- **Pokémon Abilities (*Poké-Powers*):** Manual abilities—such as *Rain Dance*, *Damage Swap*, *Energy Trans*, *Shift*, *Heal*, or *Step In*—include an interactive button in the interface, allowing you to easily activate them during your turn.

6. **Direct Impact on Your Business:**
- **If you win:** You gain experience (XP) for your character and boost your shop’s reputation with positive 5-star reviews. 
- **If you lose:** The customer leaves happy, but they do not pay the table rental fee.

---

## Development Status (Roadmap)

| Phase | Content | Status |
| :---: | :--- | :---: |
| **F0** | **Database and Formats:** MyCurio expansion reader, validation of owned cards, and `.txt` file importer. | ✅ Completed |
| **F1** | **3D Table and Playmat:** Neoprene playmat layout and zone positioning (Bench, Active, Prizes, Deck, Discard Pile, Stadium). | ✅ Completed |
| **F2** | **3D Physical Cards:** Cardstock thickness, official textures, deck stacking, and rotation for status conditions. | ✅ Completed |
| **F3** | **Table Animations:** Card flight, energy sliding, evolution pulse, and attack impact effects. | ✅ Completed |
| **F4** | **Hand-in-Fan and Interaction:** Mouse detection, and drag-and-drop functionality for cards on the table. | ✅ Completed |
| **F5** | **Close-up Inspection and Realistic Drawing:** Card-draw animation, 180° rotation, and detailed card inspection. | ✅ Completed |
| **F6** | **3D Damage Counters and Status Markers:** Acrylic damage tokens, floating numbers, and special condition markers. | ✅ Completed |
| **F7** | **3D Coin:** Metallic coin with parabolic toss physics, double-bounce behavior, and a reeded edge. | ✅ Completed |
| **F8** | **Full Matches and AI:** Turn flow, intelligent NPC opponent, and reputation/XP rewards for the shop. | ✅ Completed |
| **F9** | **3D Deck Builder:** Interactive interface at the crafting table, featuring filters by type, search functionality, and format options. | ✅ Completed |
| **F10** | **Audio and Visual Effects:** Sounds for cards, shuffling, coins, and impacts, plus screen shake upon a knockout. | ✅ Completed |
| **F10.5** | **Modular Code and Settings:** SeparClean file organization and centralized visual configuration (`VisualSettings.cs`). | ✅ Completed |
| **F10.6** | **Pokémon Abilities and Expansions:** All 26 active abilities with dedicated buttons, plus a preview of modern expansions. | ✅ Completed |
| **F10.7** | **Implementation of MyCurioHolo and Holographic Overhaul as optional requirements. Display of foil and non-foil cards on the deck table | ✅ Completed |
| **Next Up** | **NEO Era Mechanics Implementation:** Adding all cards from the TCG’s NEO era, including their unique mechanics and abilities. | ⌛ Next goal |
---

## Installation

1. Ensure that **BepInEx 5** is installed for your copy of *TCG Card Shop Simulator*.
2. Make sure the Pokémon card packs by **MyCurio** are installed within the `BepInEx/plugins/` directory.
3. Copy the `TCG-PlayKyn` folder (which contains `TCG-PlayKyn.dll` and the `Data/` folder) into the following directory:
```text
TCG Card Shop Simulator/BepInEx/plugins/
```
4. Launch the game. You can start playing by interacting with a workbench or sitting at a game table!
