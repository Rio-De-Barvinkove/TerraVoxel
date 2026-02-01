# ROADMAP 

**Оновлено:** 23 січня 2026

---

## 🎯 ВІЗІЯ ПРОЕКТУ

гра-виживалка, натхненна Vintage Story та TerraFirmaCraft.

### Референси:
- Хардкорне реалістичне виживання як в TerraFirmaGreg/VintageStory
- Багато рівнів реалістичної ерозії як в TerraFirma 2(симулятор)
- Будівництво натхненне The Forest/Sons of The Forest
- Мікровокселі розміром як в Teardown/Lay of Land
- Система продвинутої геології натхненна DwarfFortress
- Тяжка індстріалізація натхненна Create/GregTech

### Унікальні механіки
- Кожен сезон "душить" по-своєму (весна - дощі, зима - холод)
- Зміна кута камери залежно від оточення
- Інвентар з вагою та об'ємом
- Обвали в шахтах (воксельна фізика)
- Реалістичний інвентар (вага/об'єм + фізична прив'язка до персонажа, обмеження швидкості)
- Три лінії прогресу: технологічна, магічна, комбінована (механізми на мані)
- Магія = нерозпізнана високорівнева технологія; механіки розкриваються поступово через інженерний аналіз
- Протагоніст-інженер знаходить закономірності між магією і механікою
- Бінарні операції для механізмів + магічні логічні елементи, що порушують класичну фізику
- Магічна ерозія світу: використання магії підсилює появу істот, біомів, міфрілових руд, мутантів та подій


### Цільовий вигляд терейну:
- **Геометрія:** Орієнтир - Lay of the Land
- **Логічний шар:** Воксельний (для деструкції, чанків)
- **Видимий терейн:** Мікровоксельний меш з greedy meshing, скосами, плавними схилами
- **Масштаб блоку:** 0.1 м "логічного" кроку, рендеринг об'єднує у більші сегменти
- **Камера/стиль:** Ізометрична 3/4, глибина різкості, bloom, піксельні текстури
--

## ✅ ЗРОБЛЕНО 
- [X] Базова воксельна архітектура (ChunkCoord/ChunkData/VoxelMaterial/Chunk/ChunkPool).
- [X] Генерація чанків (heightmap, Burst IJobParallelFor) + face-culling мешер (Burst).
- [X] Стрімінг: ChunkManager з чергою pending і лімітом спавнів/кадр.
- [X] Рендер: URP тріпланарний шейдер, VoxelMaterialLibrary, VoxelMaterialBinder.
- [X] Палетний Texture2DArray генератор (256 кольорів) + матеріали на шейдері.
- [X] FILEMAP.md з описом структури/потоків.
- [X] Safe spawn zone з валідацією колізій.
- [X] WorldLogger.performance: логування часу генерації/видалення.
- [X] Налаштування шарів (terrain, objects, player, UI).
- [X] Profiler hooks.
- [X] Збереження чанків (per-chunk data) — binary snapshot (LZ4), atomic/async.
- [X] ChunkModManager: збереження воксельних модифікацій чанків.
- [X] Hybrid save: delta vs snapshot.
- [X] Mesh Optimization: greedy meshing + cull hidden faces.
- [X] Chunk Boundaries: узгоджені шви по XZ, врахування сусідів.
- [X] Інкрементальне видалення чанків (черга + ліміт ops/кадр).
- [X] Асинхронна генерація/мешинг на Unity Jobs + ліміти in-flight.
- [X] Базовий mesh-пайплайн.
- [X] Fallback get_mesh_index_for_block (сумісність).
- [X] Режим аналізу: fly mode + регульована швидкість.
- [X] Тумблер освітлення (analysis mode, вимкнення тіней).
- [X] Chunk class: 32x32x32 voxels (float density, uint8_t material); column 8 chunks (VS height 256).
- [X] **Physics Optimization** (окрема фізика для активних чанків)
- [X] View‑cone пріоритетна черга: O(log n) dequeue (heap), EnqueueWithPriority, ComputeScore (distance/view/visual), surface band з WorldGenConfig; TryRemoveLowestPriority O(n), IsInViewCone, ваги не нормалізовані.
- [X] Streaming work‑drop: epoch + ігнор стейл‑job результатів.
- [X] Reverse‑LOD у радіусі (low→high апгрейд).
- [X] Generation slicing (startIndex/count) + HUD черги/інтеграція.
- [X] O(1) membership для pending/preload (HashSet + черга порядку).
- [X] Removal time‑budget + зниження GC у MaintainRadius (без HashSet keep/needed).
- [X] Reverse‑LOD таймер по часу (realtime), не по frameCount.
- [X] Occlusion culling (frustum + optional raycast, окремий файл).
- [X] SVO core (volume builder + mesh builder + cache, read‑mostly для дальніх LOD, окремі файли).
- [X] **ChunkManager hardening:** integration lock (race‑safe pending/integration); recursion depth guards (RebuildNeighbors, RequestRemesh); safe spawn timeout з fallback на unsnapped position.
- [X] **Mesh cache:** hash включає LodStep, neighbor hashes, density; size‑based eviction (vertex count) для великих meshes; memory pressure eviction.
- [X] **Data cache:** TryLoadFromCache інвалідує при mod (GetDeltaCount > 0); memory pressure eviction (GC/cacheCap).
- [X] **Adaptive limits:** memory pressure + graphics memory (SystemInfo.graphicsMemorySize) throttle.
- [X] **Pending queue:** drop oldest при cap (DropOnePendingOldest; viewCone TryRemoveLowestPriority); work dropping + view cone (angle check у ProcessPending).
- [X] **RequestRemesh:** Y bounds (ColumnChunks) для сусідів; TryGetChunk повертає false при chunk=null/gen.
- [X] **ApplyChunkLayer** рекурсивно (SetLayerRecursively на children).
- [X] **LOD:** ChunkLodLevel IsValid, MaxHysteresis, Mode Billboard/None; ChunkLodSettings OnValidate (overlap/duplicate, HashSet (int,int)), GetDetailRank інвертований, TryGetLevelForDistance fallback + coarsest по rank, DefaultLevelFarDistance, ResolveLevel симетрична hysteresis.
- [X] **Far‑range LOD stub:** окрема черга _farRangeRenderQueue для render‑only поза unloadRadius (ProcessFarRangeLod cap, без spawn).
- [X] **StreamingTimeBudget:** примітка про Jobs/Burst для оптимізації frame timing.
- [X] **SVO (харденінг):** SvoBuilder SampleNeighbor bounds + Dispose note; SvoManager lock для кешу, useGpuRaymarch tooltip; SvoMeshBuilder/SvoVolume документація (boundary, color R, Dispose).
- [X] **Occlusion (харденінг):** ChunkOcclusionCuller lock _occluded, очищення застарілих записів (_activeCoordsThisTick), GetRaycastMask попередження про відсутній шар, документація AnyRayUnblocked/GetChunkBounds/RestoreAll.
- [X] **ChunkPhysicsOptimizer:** lock _stateLock, tooltips (activeRadius/inactiveRadius, includeVerticalDistance, disablePreloaded), PruneMissingInner doc.
- [X] **ChunkManager (документація):** моноліт/main thread summary, UpdateAdaptiveLimits/DropWorkQueues/MaybeDropWork/SetPlayerFrozen/ActivatePreloadedChunk/TryInitSafeSpawn/ProcessGenJobs/ProcessMeshJobs XML, work drop tooltips.

---

## Оптимізація:



9. Occlusion Culling (software)
10. Hierarchical Occlusion Culling
11. Portal Culling (печери)
12. Chunk-based Culling
13. Distance-based Culling
14. LOD по чанках
15. LOD по мешах
16. LOD по матеріалах
17. LOD по симуляціях
18. Mesh Simplification для дальніх LOD
19. Impostors / Billboards
20. GPU Instancing
22. Static Batching
23. Material Atlasing
24. Texture Atlasing
25. Texture Arrays
26. Virtual Texturing
27. Mipmapping
28. Anisotropic Filtering Control
29. Shader Variant Stripping
30. Simplified Shaders для вокселів
31. Compute Shaders для генерації
32. GPU Meshing
33. GPU Culling
34. GPU-driven Rendering
35. Indirect Draw Calls
36. Async GPU Readback Control
37. Chunk Pooling
38. Mesh Pooling
39. Object Pooling
40. Memory Pooling
41. Struct of Arrays (SoA)
42. Cache-friendly Data Layout
43. Bitpacking воксельних даних
44. Palette-based Voxels
45. Compression (RLE)
47. DAG-based SVO
48. Sparse Chunks
49. Region-based Storage
50. Paging чанків
51. Chunk Streaming
57. Lock-free Queues
58. Double Buffering чанків
59. Dirty Chunk Updates
60. Partial Mesh Rebuild
61. Face-level Updates
63. Mesh Stitching між чанками
64. Neighbor-aware Meshing
65. Early Exit при генерації
66. Deterministic Noise Caching
67. Noise Lookup Tables
68. Heightmap Hybrid System
69. Column-based Terrain
70. 2.5D Terrain Optimization
71. Cave-only Voxelization
72. Density Field Thresholding
73. Signed Distance Fields
74. SDF Caching
75. Physics Proxy Meshes
76. Simplified Collision Meshes
77. Chunk-level Physics
78. Sleeping Physics Chunks
79. Distance-based Physics Disable
80. AI Tick Throttling
81. Simulation LOD
82. Time-sliced Simulation
83. Event-driven Simulation
84. Lazy Evaluation
85. Data-oriented ECS
86. Archetype-based ECS
87. System Ordering Optimization
88. Cache Line Alignment
89. False Sharing Avoidance
90. Branch Prediction Optimization
91. Integer Math замість float
92. Fixed-point Arithmetic
93. Fast Hash Functions
94. Spatial Hashing
95. Morton Codes (Z-order)
96. Chunk Index Packing
97. Bitmask Visibility
98. Precomputed Neighbor Masks
99. Deterministic World Seeds
100. Save Delta Encoding
101. Region File System
102. Chunk Diff Saving
103. Async Save/Load
104. I/O Batching
105. Memory-mapped Files
106. Background Garbage Collection Control
107. Manual GC Tuning
108. Allocation-free Update Loops
109. Frame Budgeting
110. Adaptive Quality Scaling
111. Dynamic Resolution Scaling
112. Fixed Update Decoupling
113. Simulation Step Quantization
114. Profiling-driven Hotspot Removal
115. Conditional Compilation
116. Editor-only Code Stripping
117. Debug Code Stripping
118. Platform-specific Optimizations
119. CPU Affinity Control
120. NUMA-aware Allocation


# ПЛАНИ
P0 (високий пріоритет)
- [ ] SaveLoadManager інтеграція — складність: середня.
- [ ] Збереження інвентаря гравця — складність: середня.
- [ ] Збереження позиції гравця — складність: низька.
- [ ] Priority near player (distance-based spawn order) — складність: низька.
P1 (середній пріоритет)
- [ ] Екран "Створити світ" — складність: середня.
- [ ] Екран "Список світів" — складність: середня.
- [ ] Інтегрувати day_and_night_cycle — складність: середня.
- [ ] Lighting optimization (лише активні чанки у світлі; опційно lightmap/probes) — складність: висока.
- [ ] Random ticks: growth/update per biome — складність: середня.
- [ ] X-ray: cave highlight shader — складність: низька/середня.
- [ ] Lighting toggle: dynamic/baked — складність: низька.
- [ ] Creative mode: dynamic scale area — складність: середня.
- [ ] Console: teleport/regen/profiler — складність: середня.
P2 (нижчий пріоритет)
- [ ] Octree compression: subdivide для LOD/SVO — складність: висока.
- [ ] Indexed Buffers/GPU Opt: Vulkan buffers + occlusion — складність: дуже висока.
- [ ] Spatial Partitioning: Quadtree 2D + Octree 3D — складність: висока.
- [ ] Pixel snapping для спрайтів — складність: низька.


Фаза 3: Виживання - Core механіки (5 місяців)

Gatherables: trees/stones/plants з animation/add to inv; drop if full.
Inventory: 48 slots; drag/drop/tooltip/split (Shift+click)/sort; rubonnek backend adapt to C++.
Recipes: CraftingRecipe db; UI menu; recipes stone axe/pick/campfire/bandage; categories.
Needs: hunger/thirst/sleep; effects STARVING/DEHYDRATED/EXHAUSTED; UI bars.
Food/water: consumables; hunger/thirst effects.
Sleep: bed/skip night.
Physiological: toilet/hygiene debuffs.
PlayerStats: health/stamina; damage fall/hunger/enemies; bandages/heal; regen slow; death respawn/item loss.
Temperature/weather: body temp; rain/snow/heat; clothing protect; campfire warm; hypo/hyper effects.
Seasons/day-night: time cycle; seasons affect temp/veg; dynamic light.

Фаза 4: Бойова система (4 місяці)

Melee: stone axe weapon; stamina attacks; hit detect/damage/knockback; Health/Hit/HurtBox.
Enemies: AI pathfind via VoxelNav; zombies/skeletons; aggression/patrol; loot on death.
Defense: block shield; dodge; armor clothing.

Фаза 5: Crafting та прогресія (5 місяців)

CraftingSystem: recipes db; workbench tiers; stations furnace/anvil/loom; tier stone/iron/steel; all items recipes.
Tools/weapons: durability; TFC style knapping mini-game для tool creation.

Фаза 6: Розширені механіки (ongoing)

SaveLoad: chunk JSON (density/material/modifs); player inv/pos; create world screen (name/seed/preset); worlds list (continue/delete/rename).
Primitive survival: tinder gather; food process bare hands; geology check rocks; primitive traps; temp shelters; danger recognition; social traces; manual material process; water find/containers; fatigue from manual; fire from natural; natural shelters; food adaptation; smells/tracks; disposable tools; plant bundles; sleep prep; memory mapping; heavy carry; wood charring; meteo limits; skill barrier tools; natural hazards; pre-tool containers; natural glues; water limits; skins from carrion; nature observation tech; geologic markers; wood check handles; residue packs; stone heat treat; cross-resources craft; primitive logistics; first tool trial; work pads; branched starts; plant experiments glues; animal behavior catalyst; archaeological hints; social knowledge exchange; material expertise; alternative tool chains.

# Ерозія 
- [ ] Sediment channel per-voxel.
- [ ] Erosion simulation: full hydraulic (D8/D∞ flow algo з web1/8); detachment/transport-limited models (TFC style, diffusion equation); iterative sediment transport/gravity; GPU shaders для real-time on chunks.
- [ ] Physics Opt: PhysX тільки active chunks; erosion physics integrate з sediment.
- [ ] Water sim: 0-7 levels; even/odd update queues; integrate з erosion flow.
- [ ] Гідроерозія з перенесенням осаду: моделювання стоку води, розмиву/відкладання матеріалу, випаровування; багатокрокові ітерації по висотній сітці.
- [ ] Теплова ерозія/обвали: послаблення крутих схилів, зсув матеріалу вниз за кутом природного укосу.
- [ ] Потокові мережі (flow accumulation): обчислення водозборів, русел, розгалужень річок; можливе використання напрямних карт (flow direction) і маски водності.
- [ ] Карсти/печери: 3D-шум + «розчинення» порід за вологістю/часом або спеціальні алгоритми карстових пустот.
- [ ] Стратифікація/шари: окремі матеріальні шари з різною стійкістю до ерозії; м’якші породи розмиваються швидше.
- [ ] Осадові процеси: відкладання матеріалу в долинах/дельтах, згладження дна водойм.
- [ ] Постпроцес згладжування/варп: різні масштаби шуму (low/high frequency), domain warp для природності ліній річок і схилів.
- [ ] Мультипрохідна генерація: базовий рельєф → річкові мережі → гідро/термальна ерозія (кілька ітерацій) → осади/дельти → рослинність.






# Геологія
- [ ] Геологічна система: rock layers з VS categories (igneous/volcanic exceptions: basalt/marble/obsidian); ore veins geology-dependent; regions >1km; geologic maps для research.
- [ ] Multi-pass gen: EnumWorldGenPass (0-6 як VS); pass1: terrain noise + GenRockStrataNew (sedimentary/metamorphic/igneous layers з max thickness table); pass2: deposits; pass3: vegetation.
- [ ] IWorldGenBlockAccessor: interface з BeginColumn() для column cache clear.
- [ ] GeologicProvinces: JSON config (Shield/Platform/Orogen/Basin/LIP/Extended); max thickness для layers (e.g., Platform: sed 40/met 10/ig 255).
- [ ] Bugs simulation: optional toggle для VS bugs (granite double thickness, strata order bias).
- [ ] Large-scale: ridged/billow noise для mountains/canyons; landforms JSON (thresholds, Y keys як VS variants).
- [ ] Cave gen: 3D Perlin/cellular noise + thresholds/curves; subtract mode; Mersenne Twister для diversity (VS CaveTweaks).
- [ ] Points of Interest: ruins/craters з suvite (VS); resource zones з geologic markers.
- [ ] Розширена біоми: FBM/domain warp/ridged/billow layering; humidity/temperature maps з VS climate noise.
- [ ] Deposits: GenDeposits class; JSON configs для shapes/orientation; distortion maps (shape/vertical/ore); child deposits.
- [ ] Indices/weights per material (VS rock types).
- [ ] Multipass gen: base → features (veins/structures/lakes) → vegetation → light flood → pre-done (mobs) → done.
- [ ] Noise stack: FBM/ridged/billow; seed save; VS weighted octave.
- [ ] Геологічна система: шари породи, руди залежно від геології, метаморфізм
  - Розширення voxel системи для типів породи
  - Генерація геологічних регіонів (>1км)
  - Рудні жили з геологічними залежностями
  - Геологічні карти для дослідження
- [ ] Vertical distortion: domain warp noise для layer inversion/duplicates.
Integrate з gen: post-terrain pass; real-time local, offline global.
TFC erosion: detachment/transport models; hex/poly voxels optional (adapt chunk to hex grid).
VS integration: erosion in distortion via noise; apply to strata for inversions.


# Відкладені плани для оптимізації, за рахунок непотрібності на зараз

- [ ] Greedy meshing між чанками
- [ ] Lazy removal: clear columns lazy.
- [ ] For Late phases: гібрид: greedy для статичного, cull‑only для редагованого + фоновий greedy‑ребілд 
- [ ] **Memory Pooling** (для блоків/mesh buffers)

//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
### (довгостроково)
- [ ] Пошук глиняних покладів: відсутність гарантованого спавну, розвідка берегів/низин/пагорбів за натяками від NPC, картами, осадовими слідами
- [ ] Первинне добування: копання лопатами, малі шахти з дерев’яними підйомниками, складання глини у вали/ями для “пріння”
- [ ] Дозрівання глини: відкрите зберігання під дощем/сонцем/морозами з таймером у кілька ігрових днів, цикли вологості й висушування
- [ ] Подрібнення та перемішування: ручні товкачі, поливання й перемішування для полегшення відділення домішок та підготовки “жирної” маси
- [ ] Механіка очищення: замочування у воді, відстоювання, зняття верхнього шару органіки, збір очищеної глини після осідання важчих частинок
- [ ] Ритуали добування: молитви/символічні дії перед копанням, що тимчасово підвищують шанс знайти родовища з високою якістю
- [ ] Соціальний контекст: NPC-гончарі з сімейними традиціями, що продають карти, підказки або допомагають облаштувати глинище в обмін на ресурси/послуги
- [ ] Тест якості глини: міні-гра з перевіркою пластичності, кількості домішок і придатності перед подальшою обробкою
- [ ] Ігрові ефекти: складніший старт (дослідження, праця, час), стратегічний вибір між негайним копанням і витримкою, ризик/нагорода (чиста vs бракована глина впливає на ремесла), історичне занурення

###  (наддовгостроково)
- [ ] Збір придатних порід для вогню: окремі ресурси труту (сухі волокна кори, гнила деревина, сухі трави) як вимога перед першою іскрою
- [ ] Примітивна обробка їжі: викопування корінців палкою-копачем, збір комах/личинок/меду голими руками, розколювання горіхів між камінням
- [ ] Базова геологія: пошук валунів потрібної твердості/форми, перевірка кількох каменів до появи першого “сколу” для інструментів
- [ ] Примітивні пастки: ями з листям, петлі зі стебел, камінь-на-жердині як стартовий спосіб добути білок без зброї
- [ ] Тимчасові укриття: завалена гілляка + листяні мати/трав’яні рулони, камені як вітрозахист для переживання дощу/холоду
- [ ] Розпізнавання небезпек: сліди тварин, напрямок вітру, маскування запаху гряззю/вугіллям, крики хижаків як сигнали уникати зіткнень без зброї
- [ ] Соціальні сліди: стежки, купки каміння, залишені кістки як натяки на попередніх людей і гачок для мікроісторій
- [ ] Ручна обробка матеріалів: плетіння мотузок зі смолистих волокон/трав після замочування, без верстатів
- [ ] Пошук питної води та контейнерів: орієнтація на вологий ґрунт, звуки течії, тваринні стежки; поки не створено природний контейнер (гарбуз, мушля, кошик, береста) гравець прив’язаний до джерела
- [ ] Втома від ручної праці: базові дії (розколоти горіх, вирвати корінь, обшукати кущ) споживають багато часу/стаміни до появи інструментів
- [ ] Вогонь із природних джерел: перший жар тільки з блискавок, вулканічних тріщин або тліючих стовбурів; транспортування жару гніздом, складне збереження (ризик згасання)
- [ ] Природні укриття: пошук печер, повалених дерев чи природних заглибин як єдиний спосіб пережити перші ночі до появи будівельних інструментів
- [ ] Харчова адаптація: персонаж не знає безпечних рослин, мусить тестувати з ризиком отруєння; деякі рослини потребують вимочування/сушіння/нагріву для видалення токсинів
- [ ] Система запахів/слідів: хижаки реагують на запах гравця, змушуючи враховувати вітер і використовувати маскування
- [ ] Одноразові природні інструменти: гострі гілки, кора, кістки, колючки — виконують одну просту дію (розріз, прокол, дрібна яма) і одразу ламаються
- [ ] Рослинні скрутки: стартовий інвентар обмежений пакунками з трави/кори; поки не вивчені мотузки, ресурси переноситься маленькими “скрутками”
- [ ] Підготовка місця для сну: збір листя, моху, трави; погано підготовлене ложе дає дебаффи (холод, вологість, паразити)
- [ ] Пам’ятна картографія: без мапи — орієнтири з’являються лише після багаторазового відвідування локацій, моделюючи природну навігацію
- [ ] Перенесення важких предметів: великі камені/колоди можна тільки тягнути чи котити поштовхами з дуже низькою швидкістю, бо немає важелів і мотузок
- [ ] Обпалювання деревини: перші палки здобуваються випалюванням пня й розкришуванням твердим каменем замість рубання
- [ ] Метеорологічні обмеження старту: дощ ускладнює пошук труту, холод підвищує витрати енергії, спека пришвидшує зневоднення — критично до появи технологій
- [ ] Навичковий бар’єр для інструментів: перші спроби сколювання каменю майже завжди провальні; гравець мусить дослідити природні уламки та “навчитися” техніки перед створенням справжнього знаряддя
- [ ] Природні небезпеки без інструментів: токсичні кущі, агресивні птахи, дрібні хижаки, падіння гілок під час вітру — середовище само по собі загрожує гравцю
- [ ] Контейнери до інструментів: без листкових згортків/кошиків/плетених сіток переноска обмежена 3-4 предметами; рослинні контейнери намокають і псуються
- [ ] Природні клеї: доступ до смоли, соків чи пташиного посліду дозволяє зв’язувати базові конструкції до появи справжніх мотузок
- [ ] Водні обмеження: вода біля спавну, але переносити не можна, поки не створено листковий згорток чи шкіряний мішок; перенесена вода випаровується/забруднюється
- [ ] Перші шкіри без ножів: гравець шукає падаль, залишені шкури чи роги; знімання шкіри можливе лише якщо вона вже надірвана
- [ ] Спостереження за природою як технологія: нові рецепти відкриваються після того, як гравець побачить, як тварини викопують коріння, розбивають мушлі чи горіхи
- [ ] Геологічні маркери: біоми мають видимі індикатори (рослинність, колір ґрунту), що дають шанси на глину/ресурси; гравець навчається читати їх через знайдені записи або NPC
- [ ] Перевірка деревини для держаків: різні породи мають параметри (міцність, еластичність); гравець тестує/оцінює деревину перед використанням в інструментах
- [ ] Сировинні “пакети” зі слідами: залишки діяльності (кемпі, обпалені камені, корчі) містять ресурси або підказки щодо матеріалів, потребують навички спостережливості
- [ ] Термообробка каменю: нагрівання та охолодження каменів підвищує шанс отримати якісний флейк, але має ризик тріщин
- [ ] Крос-ресурси (мотузки, контейнери) як умова крафту: більшість інструментів вимагає попередньо виготовлених шнурів/обмоток з конкретних рослин, реалізованих через міні-пазл
- [ ] Примітивна логістика: інвентар з об’ємами; великі предмети доводиться волочити або розколювати на місці за витрату витривалості/часу
- [ ] Випробування першого інструмента: стартові знаряддя мають високий шанс поломки; виконання серії базових завдань підвищує “контроль” і довговічність майбутніх інструментів
- [ ] Робочі настили: перед крафтом треба зробити просту підстилку/рабочий стіл (гілки + мотузка), що знижує шанс браку інструментів
- [ ] Розгалужені старти (походження): вибір ролі (мисливець/збирач/ремісник) дає різні стартові навички й предмети, впливаючи на ранню стратегію
- [ ] Експерименти з рослинами для клеїв: смола/соки/корені дозволяють створити клей до глини, необхідний для кріплення каменів
- [ ] Поведінка тварин як каталізатор: шум/запах гравця впливає на реакцію фауни; деякі ресурси доступні лише в певний час доби
- [ ] Археологічні підказки: руїни/виїмки містять сліди попередніх інструментів, дають рецепти або підказки при дослідженні
- [ ] Соціальні обміни знаннями: випадкові NPC/мандрівники діляться інформацією про ресурси в обмін на дрібні задачі або бартер
- [ ] Первинна експертиза матеріалів: тест “стук/тертя” дає рейтинг придатності каменю/дерева (0–3), витрачаючи час
- [ ] Альтернативні інструментальні ланцюги: кілька рецептів для одного інструмента (різні компоненти → різні характеристики); гравець обирає стиль гри
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
//////////////////
Геологічні ідеї:
0. Фундаментальні принципи
Геологія ≠ noise
Геологія ≠ біоми
Геологія = процеси + час + наслідки
Світ має історію
Генерація ≠ симуляція
Симуляція → дані → вокселі
Причина важливіша за вигляд
1. Геологічний час
Дискретні таймстепи
Геологічні епохи
Послідовність подій
Незворотність процесів
Збереження історії шарів
Можливість прокрутки часу
2. Просторові масштаби
Континентальний рівень
Регіональний рівень
Локальний рівень
Scale separation
Узгодження меж між рівнями
3. Тектоніка плит
Континентальні плити
Океанічні плити
Межі плит:
конвергентні
дивергентні
трансформні
Субдукція
Рифти
Орогенез
Повільний рух у часі
Low-res модель
4. Ізостатія та навантаження
Підняття кори
Осідання
Реакція на ерозію
Реакція на осадження
Льодовикове навантаження
5. Термальна еволюція
Геотермічний градієнт
Мантійні плюми
Теплові аномалії
Охолодження інтрузій
Pressure–temperature зони
Вплив температури на флюїди
6. Вулканізм
Магматичні камери
Інтрузії
Ефузивні виливи
Попіл
Лавові потоки
Формування магматичних порід
Вплив на рудоутворення
7. Літологія (lithology)
Магматичні породи
Осадові породи
Метаморфічні породи
Властивості:
щільність
твердість
ерозійна стійкість
водопроникність
теплопровідність
хімічна активність
Повна незалежність від біомів
8. Стратиграфія
Послідовне нашарування
Вік шарів
Горизонтальні шари за замовчуванням
Складки
Розломи
Зсуви
Unconformities
Неможливість «перемішування без причини»
9. Структурна геологія
Розломи
Скиди
Насуви
Антикліналі
Синкліналі
Зони дроблення
10. Ерозія (процесна)
Гідравлічна
Термальна
Хімічна
Вітрова
Гравітаційна
Залежність від:
матеріалу
нахилу
клімату
Накопичення осаду
Heightmap erosion (precompute)
11. Осадження
Річкове
Озерне
Морське
Дельтове
Заплавне
Еолове
Формування осадових порід з часом
12. Гідрологія
Водозбірні басейни
Дренажні системи
Річки як наслідок
Підземні води
Карст
Вплив на ерозію і руди
13. Клімат (геологічний)
Температурні пояси
Опади
Кліматичні цикли
Льодовикові періоди
Рівень моря
Вплив на ерозію і осадження
14. Льодовикова геологія
Формування льодовиків
Льодовикова ерозія
Морени
Фіорди
Післяльодовиковий рельєф
15. Прибережні та морські процеси
Хвильова ерозія
Припливи
Континентальний шельф
Берегові відклади
Морські трансгресії та регресії
16. Хімічне вивітрювання
Карбонатне розчинення
Окислення
Формування глин
Кліматозалежні реакції
Карстові системи
17. Біогеологія
Ґрунтоутворення
Біотурбація
Органогенні осади
Рифи
Вуглецеві формації
18. Рудоутворення (ore genesis)
Магматичне
Гідротермальне
Осадове
Метаморфічне
Залежність від:
температури
флюїдів
порід
часу
Руди як наслідок процесів
19. Масові переміщення
Обвали
Зсуви
Талюсні схили
Колапси порожнин
Voxel erosion (локально)
20. Катастрофічні події
Прориви озер
Лахари
Мегаобвали
Імпактні кратери
Супервулканізм
21. Поверхневі форми (результат)
Каньйони
Плато
Фіорди
Карстові воронки
Ескарпи
Долини різних типів
22. Невизначеність і стохастика
Ймовірнісні події
Діапазони параметрів
Seed ≠ ідентичний світ
Контрольований хаос
23. Дані для «читання світу»
Виходи порід
Осадові структури
Тектонічні маркери
Геологічні аномалії
Логічні підказки для гравця
24. Дані та представлення
Геологічні карти
Стратиграфічні колонки
Вік кожного шару
Тип процесу походження
Метадані без вокселів
25. Вокселізація
Конвертація геомоделі → вокселі
LOD по геології
Chunk-level геодані
Кешування
Незалежність від рендера
26. Інтеракція з гравцем
Видобуток змінює напруження
Підкоп → обвал
Вода реагує на зміну рельєфу
Локальні ресими
Помилки гравця мають наслідки
27. Обмеження (свідомі)
Ніякої глобальної runtime-симуляції
Важке — лише precompute
Runtime — локальні події
Частковий ресим
Детермінований replay
28. Технічна інфраструктура
Версіонування світу
Debug-візуалізація процесів
Логи геологічних подій
Інструменти аналізу
Тестові сценарії
29. Мінімальний критерій «перевершив TFC / VS»
Геологічна вісь часу
Причинне рудоутворення
Рельєф має історію
Один seed → різні світи
Ландшафт читається логічно
30. Геомеханіка (механічна поведінка порід)
Не плутати з ерозією чи обвалами — це внутрішня міцність середовища.
Напружено-деформований стан порід
Крихке vs пластичне руйнування
Залежність міцності від:
глибини
температури
вологості
Повільна деформація (creep)
Зони концентрації напружень
Передумови для розломів ще ДО їх появи
31. Сейсмічність
Не катастрофи, а фонова тектонічна активність.
Акумуляція напружень
Скидання напружень
Мікросейсміка
Землетруси як наслідок, не івент
Вплив на:
тріщинуватість
флюїдні канали
обвали
рудні жили
32. Фрактурні мережі
Ключ до води, руд і стабільності.
Первинна тріщинуватість
Вторинні тріщини
Орієнтація тріщин
Зв’язність мереж
Проникність масиву
Контроль потоків води і флюїдів
33. Пористість і проникність
Не те саме, що «водопроникність» як властивість.
Первинна пористість
Вторинна пористість
Закупорювання пор
Розкриття пор з часом
Вплив на:
підземні води
нафту/газ
гідротермальні системи
34. Басейнова геологія
Великий масштаб осадових систем.
Осадові басейни
Швидкість заповнення
Прогин кори
Довготривале осадження
Перехід від м’яких осадів до каменю
Контроль розміщення ресурсів
35. Діагенез
Критично відсутній у більшості симуляцій.
Ущільнення осадів
Цементація
Зміна мінералогії
Втрата пористості
Перетворення «піску» → «пісковик»
Час як обов’язкова умова
36. Метасоматоз
Геохімія, яку ігри ігнорують.
Обмін речовиною між флюїдом і породою
Заміщення мінералів
Формування зон змінених порід
Просторові «ореоли» змін
Маркери рудних тіл
37. Геоморфологічні рівні
Не форма, а вік поверхні.
Давні поверхні вирівнювання
Перерізані тераси
Палеорельєф
Накладення нових форм на старі
Читання віку ландшафту
38. Палеогеографія
Світ змінюється не лише вертикально.
Міграція континентів
Старі берегові лінії
Давні річкові системи
Поховані долини
Невідповідність сучасного рельєфу підповерхневим структурам
39. Палеоклімат
Клімат як історія, не стан.
Давні льодовикові періоди
Аридні епохи
Тропічні фази
Кліматичні маркери в породах
Зв’язок з осадженням і ерозією
40. Геобіохімічні цикли
Не біоми, а кругообіг речовин.
Вуглецевий цикл
Сірчаний цикл
Залізний цикл
Окисно-відновні умови
Контроль типів порід і руд
41. Глибинні флюїди
Не лише вода.
CO₂
CH₄
Сірководень
Магматичні гази
Тиск флюїдів
Роль у розломах і вибухових процесах
42. Геологічна пам’ять світу
Метасистема над геологією.
Ланцюжки причин
Подієві графи
Неможливі стани
Перевірка консистентності
Виявлення «фізично неможливих» конфігурацій
43. Внутрішні інваріанти
Те, що не можна порушити, навіть гравцем.
Закон збереження маси
Закон збереження енергії (спрощено)
Неможливість створення порід з нічого
Неможливість миттєвих великих змін
44. Деградація даних з масштабом
Технічно, але критично.
Втрата деталей при агрегації
Узгодження low-res ↔ high-res
Неможливість «вгадати» деталі без історії
Чітке походження кожного вокселя
45. Перевірка реалізму (sanity layer)
Автоматичний контроль.
Виявлення геологічних абсурдів
Валідація стратиграфії
Перевірка рудних асоціацій
Блокування неможливих результатів
46. Інструменти для розробника
Не для гравця.
Перегляд геологічної історії точки
Візуалізація часу
Шари «чому це тут»
Дебаг симуляцій
Відтворення еволюції світу