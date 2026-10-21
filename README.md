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
| **F4** | **Mano e Input 3D** | Abanico 3D de la mano del jugador y del NPC, detección por raycast 3D (hover/elevación), selección y arrastre de cartas hacia zonas válidas de la mesa e inspección con clic derecho. | 🔜 Siguiente |
| **F5** | **Dados, Moneda y Contadores 3D** | Moneda 3D con lanzamiento físico para efectos de *Cara/Cruz*, dados y contadores 3D de daño sobre los Pokémon y marcadores 3D de Veneno/Quemadura. | ⏳ Pendiente |
| **F6** | **Cámara y HUD Mínimo** | Controles de cámara en la mesa (`Tab` para vista cenital táctica, zoom con rueda, enfoque al centro) y HUD ligero no intrusivo para turno, fase y acciones de ataque/retirada. | ⏳ Pendiente |
| **F7** | **Motor de Reglas e Intérprete de Efectos** | Soporte completo de reglas (Mulligan, debilidad/resistencia, estados alterados, entrenadores/estadios) e intérprete declarativo en JSON para añadir ataques y habilidades fácilmente. | ⏳ Pendiente |
| **F8** | **IA del Oponente (NPC) y Mazos Temáticos** | Generación de mazos temáticos para los clientes según el nivel de la tienda y toma de decisiones del NPC con pausas naturales y animaciones 3D. | ⏳ Pendiente |
| **F9** | **Economía de Tienda y Reputación** | Flujo completo de fin de partida con pantalla de resultados, entrega de XP y reseña positiva al ganar, o pérdida de la tarifa de mesa si gana el NPC. | ⏳ Pendiente |
| **F10** | **Audio, Pulido y Herramientas de Expansión** | Efectos de sonido en mesa (barajar, robar, moneda, impacto), comando para generar plantillas JSON de nuevas expansiones de MyCurio y pulido de rendimiento. | ⏳ Pendiente |

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
