# 🎴 PokeTCG Play — Pokémon TCG 3D para *TCG Card Shop Simulator*

**PokeTCG Play** es un mod para *TCG Card Shop Simulator* (BepInEx 5) que convierte las mesas de juego de tu tienda en partidas reales e interactivas de **Pokémon Trading Card Game en 3D**, utilizando las cartas que hayas conseguido abriendo sobres en tu partida y los paquetes de expansiones de **MyCurio**.

---

## ✨ ¿Qué hace el mod y cuál es su objetivo?

El objetivo de **PokeTCG Play** es integrar el juego físico de Pokémon TCG directamente dentro del ciclo de juego de tu tienda:

1. **Juega solo con tu colección real:** Para armar un mazo necesitas poseer las cartas en tu partida (ya sea en tu **Álbum**, en **Cajas/Estanterías de Almacenamiento** o en **Estanterías de Exhibición**). Las Energías Básicas son ilimitadas como en los constructores oficiales.
2. **Integración nativa con los mods de MyCurio:** Escanea automáticamente todas las expansiones de MyCurio instaladas en `BepInEx/plugins`, carga sus texturas en alta calidad y vincula cada carta con el sobre/expansión correspondiente dentro del juego.
3. **Constructor de Mazos (Workbench) e Importador `.txt` en vivo:**
   - En la **Mesa de Trabajo (Workbench)** puedes crear múltiples mazos de 60 cartas, elegir el **formato** (`Base Set`, `Unlimited`, etc.) y marcar tu mazo activo.
   - **Importador PTCGO / PTCG Live en caliente:** Pega archivos `.txt` con listas de mazos en `BepInEx/config/PokeTCGPlay/import` (puedes abrir la carpeta desde el propio juego sin cerrarlo). Si te faltan cartas para completar el mazo importado, el mod te muestra un **reporte detallado de cartas faltantes** y **en qué expansión/sobre conseguirlas**.
4. **Experiencia 3D sobre la mesa física:**
   - Al sentarte frente a un cliente en una mesa de juego, se despliega un **tapete 3D** con cartas físicas (grosor real, cara frontal de MyCurio, reverso oficial, sombras dinámicas), pilas de mazo y descarte proporcionales, cartas de premio, energías asomando debajo de cada Pokémon y rotaciones físicas para estados alterados.
5. **Recompensas y riesgo para tu tienda:**
   - **Si ganas al NPC:** Obtienes **Experiencia (XP)** adicional y aumentas la **calificación/reputación** de tu tienda con reseñas de 5 estrellas (sin generar dinero directo de la nada).
   - **Si el NPC te gana:** El cliente se lleva el costo de uso de la mesa en lugar de pagárselo a tu tienda.

---

## 🗺️ Hoja de Ruta (Roadmap)

El desarrollo está estructurado paso a paso de **F0 a F10**:

| Fase | Nombre | Descripción | Estado |
| :---: | :--- | :--- | :---: |
| **F0** | **Datos, Expansiones, Colección y Formatos** | Lector universal de expansiones de MyCurio, validación contra la colección real del jugador (Álbum + Almacén + Estantes), soporte multi-mazo, formatos JSON e importador `.txt` estilo PTCGO en caliente con detector de cartas faltantes. | ✅ Completado |
| **F1** | **Tapete y Anclas 3D en la Mesa** | Generación del tapete 3D (`PokeTCG_Playmat3D`) sobre las mesas de juego y sistema de coordenadas/anclas 3D para Activo, Banca (5), Premios (6), Mazo, Descarte, Estadio y Mano. | ✅ Completado |
| **F2** | **Cartas 3D sobre la Mesa** | Renderizado de cartas 3D (`CardView`) con grosor físico, texturas de MyCurio, bloques de mazo escalables (`DeckStack3D`), apilado de energías/evoluciones y rotación por Condiciones Especiales. | ✅ Completado |
| **F3** | **Animador 3D (`BoardAnimator`)** | Vuelos parabólicos en 3D con inclinación dinámica, giro de 180° al robar cartas o tomar premios, deslizamiento de energías, pulso de evolución, embestida de ataque y caída al descarte. | ✅ Completado |
| **F4** | **Mano e Input 3D** | Abanico 3D de la mano del jugador y del NPC, detección por raycast 3D (hover/elevación), selección y arrastre de cartas hacia zonas válidas de la mesa e inspección con clic derecho. | ✅ Completado |
| **F5** | **Inspección 3D, Mazo Realista y Robo a Pantalla** | Pila 3D de Mazo realista en capas escalonadas, animación de robo hacia la pantalla viéndose por detrás con vuelta de 180° y pausa de `0.5s`, abanico bajo sin tapar la Banca e inspección 3D con giro interactivo e iconos de energía. | ✅ Completado |
| **F6** | **HUD de Estado y Contadores 3D** | Fichas 3D acrílicas de daño (`10`, `50`, `100`) y estado (`VEN`, `DEF`, `+10`), números flotantes 3D, iconos animados con pulso y duración, tinte pulsante y rotación física suavizada por condición especial. | ✅ Completado |
| **F7** | **Moneda 3D** | Moneda cilíndrica 3D con caras Pikachu/Pokéball, canto estriado dorado/grafito, vista previa flotante al inicio, lanzamiento parabólico con doble rebote, vibración de canto, anillo de aterrizaje y cola 3D secuencial en ataques/estados. | ✅ Completado |
| **F8** | **Flujo de Partida, IA y Recompensas** | Flujo completo de partida, IA del NPC y recompensas de tienda (XP + reseñas positivas al ganar, o pérdida de la tarifa de mesa si gana el NPC). | 🔜 Siguiente |
| **F9** | **Deck Builder 3D** | Constructor de mazos 3D en la Workbench con filtros, validación por formato y colección real del jugador. | ⏳ Pendiente |
| **F10** | **Audio, Pulido y Herramientas de Expansión** | Efectos de sonido en mesa (barajar, robar, moneda, impacto), sacudida de cámara en KO y opciones de configuración. | ⏳ Pendiente |
| **F10.5** | **Refactor de Legibilidad y `VisualSettings`** | Reestructuración modular limpia (150–250 líneas por archivo) y archivo único `VisualSettings.cs` + `PokeTCGPlay.cfg` para editar alturas, cámaras y animaciones fácilmente. | ⏳ Pendiente |

---

## 🛠️ Cómo agregar nuevas expansiones o cartas manualmente

El mod está diseñado para que puedas ampliar el catálogo sin necesidad de recompilar el código:

- **Cartas (`Data/cards/<set>.json`):** Puedes definir o editar cualquier carta (PS, tipo, coste de retirada, ataques y efectos) en archivos JSON dentro de `BepInEx/plugins/PokeTCGPlay/Data/cards/`. Si una carta de MyCurio aún no tiene archivo JSON manual, el mod la indexa igualmente para que puedas verla, coleccionarla y diagnosticar qué te falta.
- **Formatos (`Data/formats/<formato>.json`):** Define qué sets están permitidos, tamaño del mazo (60), límite de copias (4) y reglas del formato.
- **Importar Mazos (`BepInEx/config/PokeTCGPlay/import/*.txt`):** Coloca cualquier archivo `.txt` exportado de PTCGO / Pokémon TCG Live y presiona **`📥 Importar .txt`** en la Mesa de Trabajo sin reiniciar el juego.

---

## 📦 Requisitos e Instalación

1. Tener instalado **BepInEx 5** en *TCG Card Shop Simulator*.
2. Tener instalados los paquetes de expansiones de **MyCurio** en `BepInEx/plugins/`.
3. Copiar la carpeta compilada de `PokeTCGPlay` (`PokeTCGPlay.dll` y la carpeta `Data/`) dentro de `BepInEx/plugins/PokeTCGPlay/`.
