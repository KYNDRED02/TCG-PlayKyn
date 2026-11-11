# TCG-PlayKyn — Pokemon TCG para *TCG Card Shop Simulator*

Para la info en ingles, chequea: [README_EN.md](README_EN.md) 

**TCG-PlayKyn** es un mod para *TCG Card Shop Simulator* (BepInEx 5) que convierte las mesas de juego de tu tienda en partidas reales e interactivas de **Pokémon Trading Card Game en 3D**, utilizando las cartas que vas abriendo en tus sobres y los paquetes de expansiones de **MyCurio**.

---

## Origen y Desarrollo del Proyecto

El mod nació originalmente como un prototipo y simulador de reglas programado en **Python con Visual Studio Code**. Con el fin de integrarlo de manera nativa y con gráficos 3D dentro del motor Unity del juego, el proyecto fue trasladado, adaptado y optimizado a **C# (.NET / BepInEx)** con asistencia parcial de **(IA)** (Aún estoy aprendiendo a utilizar C#, tenganme paciencia :S). 

---

## ¿Qué ofrece el mod?

1. **Juega con tu colección real:**
   - Para armar tu mazo necesitas poseer las cartas en tu partida (ya sea que las tengas guardadas en tu **Álbum**, en **Cajas de Almacenamiento** o exhibidas en tus **Estanterías**).
   - Las Energías Básicas son ilimitadas, igual que en los juegos y torneos oficiales.

2. **Compatibilidad con las expansiones de MyCurio:**
   - Reconoce automáticamente todas las expansiones de cartas de Pokémon instaladas en tu juego (`Base Set`, `Jungle`, `Fossil`, `Base Set 2`, `Team Rocket`, `Gym Heroes`, `Gym Challenge` y paquetes promocionales).
   - Muestra las cartas con su arte en alta calidad, sus bordes y acabados holográficos originales.

3. **Constructor de Mazos 3D e Importador de listas `.txt`:**
   - Puedes crear y gestionar varios mazos de 60 cartas directamente desde la **Mesa de Trabajo (Workbench)** de tu tienda.
   - **Importador rápido:** Puedes exportar una lista de mazo desde *Pokémon TCG Live* o *PTCGO* en formato texto (`.txt`), pegarla en la carpeta del mod y cargarla al instante. Si te falta alguna carta, el juego te indicará con precisión cuál es y en qué sobre conseguirla.

4. **Partidas en 3D sobre la mesa de la tienda:**
   - Al sentarte a jugar frente a un cliente, se despliega un **tapete de juego** físico con cartas en relieve, sombras, animaciones de robo hacia la pantalla y vista en abanico de tu mano.
   - **Moneda 3D con físicas reales:** Lanzamientos parabólicos de moneda (con caras de Pikachu y Pokéball) para decidir turnos, ataques y efectos especiales.
   - **Contadores y fichas 3D:** Fichas de daño y marcadores de estado (Dormido, Confuso, Paralizado, Envenenado) que rotan físicamente la carta sobre el tapete.
   - **Estadio 3D:** Las cartas de Estadio se colocan físicamente en el lateral del tapete y aplican sus reglas a ambos jugadores.

5. **Mecánicas completas de la Era Base:**
   - Ataques, cartas de Entrenador, Partidarios y Estadios totalmente funcionales.
   - **Habilidades de Pokémon (*Poké-Powers*):** Habilidades manuales como *Rain Dance*, *Damage Swap*, *Energy Trans*, *Shift*, *Heal* o *Step In* cuentan con un botón interactivo a su lado en la interfaz para activarlas cómodamente durante tu turno.

6. **Impacto directo en tu negocio:**
   - **Si ganas:** Ganas experiencia (XP) para tu personaje y mejoras la reputación de tu tienda con reseñas positivas de 5 estrellas.
   - **Si pierdes:** El cliente se marcha contento y no paga la tarifa de alquiler de la mesa.

---

## Estado del Desarrollo (Roadmap)

| Fase | Contenido | Estado |
| :---: | :--- | :---: |
| **F0** | **Base de Datos y Formatos:** Lector de expansiones de MyCurio, validación de cartas en posesión e importador `.txt`. | ✅ Completado |
| **F1** | **Mesa y Tapete 3D:** Colocación del tapete de neopreno y cálculo de casillas (Banca, Activo, Premios, Mazo, Descarte, Estadio). | ✅ Completado |
| **F2** | **Cartas Físicas 3D:** Grosor del cartón, texturas oficiales, mazo apilado y giro por estados alterados. | ✅ Completado |
| **F3** | **Animaciones de Mesa:** Vuelos de cartas, deslizamiento de energías, pulso de evolución y embestidas de ataque. | ✅ Completado |
| **F4** | **Mano en Abanico e Interacción:** Detección con el ratón, arrastrar y soltar cartas en la mesa (*drag & drop*). | ✅ Completado |
| **F5** | **Inspección Elevada y Robo Realista:** Animación de robo a pantalla, giro de 180° e inspección detallada de cartas. | ✅ Completado |
| **F6** | **Contadores de Daño y Estados 3D:** Fichas acrílicas de daño, números flotantes y marcadores de condición especial. | ✅ Completado |
| **F7** | **Moneda 3D:** Moneda metálica con físicas de lanzamiento parabólico, doble rebote y canto estriado. | ✅ Completado |
| **F8** | **Partidas Completas e IA:** Flujo de turnos, oponente NPC inteligente y recompensas de reputación/XP para la tienda. | ✅ Completado |
| **F9** | **Constructor de Mazos 3D:** Interfaz interactiva en la mesa de crafteo con filtros por tipo, búsqueda y formatos. | ✅ Completado |
| **F10** | **Audio y Efectos Visuales:** Sonidos de cartas, barajado, monedas, impactos y sacudida de pantalla al noquear. | ✅ Completado |
| **F10.5** | **Código Modular y Ajustes:** Separación limpia de archivos y configuración visual centralizada (`VisualSettings.cs`). | ✅ Completado |
| **Extra** | **Habilidades Pokémon y Expansiones:** Las 26 habilidades activas con botones dedicados y vista previa de expansiones modernas. | ✅ Completado |
| **Proximo** | **Implementación de mecánicas de NEO Era** Implementación de todas las cartas respectivas a la era NEO del TCG, incluyendo sus mecánicas nuevas y habilidades. | ⌛ Siguiente meta | 
---

## Instalación

1. Asegúrate de tener instalado **BepInEx 5** en tu juego *TCG Card Shop Simulator*.
2. Ten instalados los paquetes de cartas de Pokémon de **MyCurio** dentro de `BepInEx/plugins/`.
3. Copia la carpeta `PokeTCGPlay` (que contiene `PokeTCGPlay.dll` y la carpeta `Data/`) dentro del directorio:
   ```text
   TCG Card Shop Simulator/BepInEx/plugins/
   ```
4. Inicia el juego. ¡Al interactuar con una mesa de trabajo o sentarte en una mesa de juego podrás empezar a jugar!
