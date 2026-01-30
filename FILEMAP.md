# FILEMAP

Оновлено: 2026-01-23

## Структура voxel-підсистеми (Assets/Scripts/Voxel)
- Core/
  - `VoxelConstants.cs` — константи масштабу (ChunkSize=32, ColumnChunks=8, VoxelSize=0.1m).
  - `VoxelMath.cs` — clamp‑утиліти для безпечних конвертацій координат.
- `ChunkCoord.cs` — координата чанка (X,Y,Z), легкий GetHashCode/Equals/ToString.
  - `VoxelMaterial.cs` — ushort enum (Air, Dirt, Stone, Sand, Water).
  - `ChunkData.cs` — буфери NativeArray<ushort> Materials, NativeArray<float> Density (опційний); Index/Bounds; Allocate/Dispose.
- `Chunk.cs` — MonoBehaviour для інстансу чанка; MeshFilter/Renderer/Collider + Mesh; ApplyMesh/ApplySharedMesh вмикають renderer за наявності mesh; LodStep, UsesSvo, LodStartTime.
  - `ChunkPool.cs` — пул Chunk-інстансів; активує отримані.

- Generation/
  - `WorldGenConfig.cs` (SO) — Seed, ChunkSize, ColumnChunks, BaseHeight, HeightScale, HorizontalScale, EnableRivers, DefaultMaterialIndex; safe‑spawn платформа (EnableSafeSpawn, SizeChunks, Thickness, MaterialIndex, Snap, Revalidate).
  - `NoiseStack.cs` (SO) — масив NoiseLayer (Type: Perlin/Simplex/Voronoi, Scale, Octaves, Persistence, Lacunarity, Weight).
  - `RockStrataConfig.cs` (SO) — болванка для товщин шарів (sed/met/ig).
  - `IChunkGenerator.cs` — інтерфейс генератора (Schedule з опційним startIndex/count для slicing).
  - `ChunkGenerator.cs` — Burst IJobParallelFor для heightmap; підтримує генерацію по діапазону (slicing).

- Meshing/
  - `MeshData.cs` — NativeList<Vertex/Index/Normal>, Dispose.
  - `GreedyMesher.cs` — Burst greedy‑merge, face‑culling; NeighborData для меж чанків; опційний масштаб вокселя.
  - `MeshBuilder.cs` — копіює MeshData в Unity Mesh через NativeArray view.

- Streaming/
  - `PlayerTracker.cs` — перетворення world→chunk координат.
  - `ChunkTask.cs` — enum стани (PendingGen/…); struct для даних задач (не використовується поки).
- `ChunkManager.cs` — стрімінг чанків, pending/remesh/integration, streaming budget, preload, safe‑spawn + snap (safeSpawnTimeoutSeconds, fallback unsnapped), gen/mesh jobs, data cache (TryLoadFromCache з mod invalidation по GetDeltaCount), mesh cache (shared Mesh, hash з LodStep/neighbors/density, size‑based eviction по vertex count, memory pressure eviction), work‑drop epoch, reverse‑LOD по часу, gen slicing, адаптивні ліміти (memory + graphicsMemoryThresholdMb), removal time‑budget, pending HashSet; integration lock (_integrationLock) та recursion guards (maxRebuildNeighborsDepth, maxRequestRemeshNeighborsDepth); pendingQueueCap + DropOnePendingOldest (viewCone TryRemoveLowestPriority); ProcessPending + view cone angle (work drop); RequestRemesh з Y bounds (ColumnChunks); TryGetChunk false при chunk=null/gen; ApplyChunkLayer рекурсивно (SetLayerRecursively); far‑range LOD stub (_farRangeRenderQueue, ProcessFarRangeLod, enableFarRangeLod, farRangeRadius); при увімкненому view‑cone — pending у ChunkViewConePrioritizer; повний LOD (upgrade/downgrade), інтеграція occlusion/SVO.
  - `ChunkJobHandles.cs` — хендли Job + буфери gen/mesh (epoch/hash/lodStep).
  - `StreamingTimeBudget.cs` — ліміт часу на стрімінг за кадр; примітка про Jobs/Burst для оптимізації.
- `ChunkPhysicsOptimizer.cs` — колайдери тільки в активному радіусі; вмикання лише якщо mesh має vertices.
  - `ChunkViewConePrioritizer.cs` — пріоритетна черга (heap) O(log n) dequeue; EnqueueWithPriority(coord, center, player) з обчисленням score при додаванні; ComputeScore (нормалізовані distance/view/visual), динамічний surface band з WorldGenConfig; TryRemoveLowestPriority(out coord) для drop при cap; IsInViewCone(coord, center, player) для work‑drop по куту; Clear() обрізає capacity; без fallback при порожній черзі.
- LOD/
  - `ChunkLodLevel.cs` — struct для LOD‑рівня (MinDistance, MaxDistance, LodStep, Hysteresis, Mode); IsValid (non‑negative, MaxDistance >= MinDistance); ChunkLodMode: Mesh, Svo, Billboard, None.
  - `ChunkLodSettings.cs` (SO) — налаштування LOD: список рівнів, hysteresis, mode (Mesh/SVO/Billboard/None), ring‑based distance calc; OnValidate (overlap/duplicate попередження); GetDetailRank (LodStep + Mode); TryGetLevelForDistance з fallback за MaxDistance; ResolveLevel з hysteresis.
- Occlusion/
  - `ChunkOcclusionCuller.cs` — frustum culling + optional raycast occlusion; окрема система, не чіпає геймплей.
- Svo/
  - `SvoVolume.cs` — структура SVO (Node з ChildMask/Material/FirstChild, recursive tree).
  - `SvoBuilder.cs` — побудова SVO з ChunkData (recursive subdivision, uniform region detection).
  - `SvoMeshBuilder.cs` — генерація Mesh з SVO (cube‑based для leaf nodes).
  - `SvoManager.cs` — кеш SVO‑мешів, hash‑based reuse, LRU evict; read‑mostly, не для setBlock().

- Rendering/
  - `VoxelMaterialLibrary.cs` (SO) — Texture2DArray, TriplanarScale, NormalStrength, DefaultLayerIndex.
  - `VoxelMaterialBinder.cs` — на Renderer ставить `_MainTexArr`, `_TriplanarScale`, `_NormalStrength`, `_LayerIndex` з library.

- Systems/
  - `ChunkSaveStub.cs` — JSON‑stub (legacy, не використовується).
  - `ProfilerHooks.cs` — простий Stopwatch wrapper (stub).
  - `VoxelAnalysisMode.cs` — F2 fly/no‑clip, freeze streaming, shadow toggle, cursor lock; увімкнено в release за замовчуванням.
  - `VoxelDebugHUD.cs` — HUD, графіки, CSV‑експорт, async summary‑лог, черги/інтеграція.

- Save/
  - `ChunkSaveBinary.cs` — бінарний формат, magic+version+flags; LZ4/GZip (v1) декомпресія; матеріали (ushort) + опційна щільність; CRC.
  - `ChunkModBinary.cs` — бінарні дельти (index+material ushort), LZ4, CRC.
  - `ChunkSaveMode.cs` — enum режимів сейву + ChunkMeta.
  - `ChunkSaveManager.cs` — async save‑черга, атомарний запис, load on spawn, save on unload/destroy; worldId/region‑папки; join timeout.
  - `ChunkModManager.cs` — менеджер модифікацій (delta‑сейви), async/atomic; пакетні правки; join timeout.
  - `ChunkHybridSaveManager.cs` — правила delta vs snapshot.
  - `VoxelModDebugInput.cs` — режим взаємодії (B), перемикання dig/build (V), raycast, brush size 1‑10, форми, підсвітка.
  - `Lz4Codec.cs` — C# LZ4‑кодек (вбудований, без зовнішніх залежностей).
  - `Crc32.cs` — CRC32 для файлів сейву.

- `TerraVoxel.Voxel.asmdef` — залежності Burst/Collections/Mathematics/Jobs/URP.

## Шейдери / матеріали
- `Assets/Shaders/VoxelTriplanarURP.shader` — URP opaque, тріпланар семпл Texture2DArray, параметри `_TriplanarScale`, `_LayerIndex`, `_NormalStrength`.

## Потік даних (мінімальна реалізація)
1) `ChunkManager.Update` оновлює jobs (gen/mesh), підтримує радіуси, _pending/_preload, ремеш/видалення.
2) `ProcessPending` бере до `maxSpawnsPerFrame` з pending: при увімкненому view‑cone — `ChunkViewConePrioritizer.TryDequeue` (O(log n)), інакше `_pending.Dequeue`; при workDropAngleDeg > 0 і viewCone — skip spawn для coords поза view cone (IsInViewCone); додавання в pending — EnqueueWithPriority або _pending.Enqueue; при pendingQueueCap — DropOnePendingOldest (viewCone TryRemoveLowestPriority або _pending.Dequeue); PendingCount враховує viewCone.Count.
3) `SpawnChunk`: Allocate `ChunkData` → спроба snapshot‑load (hybrid або save manager) → якщо немає, планується gen‑job.
4) Завершення gen‑job: apply safe‑spawn (якщо треба), apply delta‑mods → план mesh‑job.
5) Завершення mesh‑job: постановка в integration queue → `Chunk.ApplyMesh` (ліміт/кадр), ремеш сусідів, колайдери за умовами.
6) `ProcessFullLod` (якщо `enableFullLod`): перевіряє дистанцію, обирає LOD‑рівень з `ChunkLodSettings`, виконує upgrade/downgrade (mesh або SVO) з cooldown.
7) `ChunkOcclusionCuller.Tick`: frustum culling + optional raycast, вимикає renderer для occluded чанків.
8) При `RemoveChunk` (вихід з радіуса) `ChunkHybridSaveManager` або `ChunkSaveManager` + `ChunkModManager` → atomic write; `SvoManager.ReleaseForChunk`; при `OnDestroy` — save all активних.

## Що вже зроблено / статус TODO
- Готово: константи/структури/пул; генератор (heightmap, Burst) + slicing; greedy‑meshing з neighbor‑culling; стрімінг із чергами/лімітами + інтеграція; view‑cone з пріоритетною чергою (heap, TryDequeue, EnqueueWithPriority, TryRemoveLowestPriority, IsInViewCone, ComputeScore, surface band); mesh cache (hash LodStep/neighbors/density, size‑based eviction, memory pressure); data cache з mod invalidation; integration lock + recursion guards; safe spawn timeout + fallback; pending cap drop oldest; work drop + view cone angle; RequestRemesh Y bounds; TryGetChunk null/gen; ApplyChunkLayer recursive; adaptive limits (memory + GPU); reverse‑LOD апгрейд; повний LOD (ChunkLodLevel IsValid, Mode Billboard/None; ChunkLodSettings OnValidate, GetDetailRank, fallback за MaxDistance); occlusion culling (frustum + raycast); SVO core (volume builder + mesh builder + cache); far‑range LOD stub (черга _farRangeRenderQueue, ProcessFarRangeLod cap); StreamingTimeBudget примітка Jobs/Burst; тріпланар шейдер+SO+биндер; LZ4‑chunk save (async + atomic); 256‑палітра; analysis mode.
- Немає (потрібно доробити): strata/rivers/biomes/erosion; вода; far‑range LOD pipeline (реальний spawn render‑only чанків поза unloadRadius з low LOD/SVO); greedy‑зшивання між чанками; повноцінний save/load менеджер (світ/інв/позиція); рушійний контролер гравця (зовнішній).

### Палітра 256 кольорів (індекси → базові кольори × яскравість)
- Сгенеровано в `Assets/Editor/PaletteTextureArrayBuilder.cs`.
- 32 базові кольори, кожен має 8 варіантів яскравості (множники 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4).
- Індекс = baseIndex * 8 + variantIndex.
  - variantIndex: 0..7 відповідає множнику 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4.
- Базові кольори (baseIndex, RGBA):
  0: sand light (205,189,155,255)
  1: sand mid (181,161,123,255)
  2: dirt light (138,114,83,255)
  3: dirt mid (110,88,62,255)
  4: dirt dark (78,60,44,255)
  5: soil dark (58,50,47,255)
  6: stone cool (105,112,117,255)
  7: stone mid (88,90,94,255)
  8: stone dark (60,62,68,255)
  9: basalt (46,50,56,255)
  10: moss stone (74,84,66,255)
  11: grass light (86,112,66,255)
  12: grass mid (70,99,55,255)
  13: grass dark (54,84,44,255)
  14: wood light (93,73,44,255)
  15: wood mid (74,57,35,255)
  16: wood dark (56,42,27,255)
  17: water shallow (33,120,154,255)
  18: water mid (18,88,125,255)
  19: water deep (10,66,102,255)
  20: snow light (188,198,210,255)
  21: snow mid (160,172,186,255)
  22: snow shaded (132,144,160,255)
  23: clay red (196,110,86,255)
  24: clay mid (167,96,74,255)
  25: clay dark (137,82,66,255)
  26: shale/leaf dark (116,130,112,255)
  27: leaf light (146,160,124,255)
  28: leaf mid (118,148,92,255)
  29: metal worn (120,120,120,255)
  30: metal dark (96,96,96,255)
  31: metal deep (68,68,68,255)

## Як підключати (коротко)
- Створи SO: `WorldGenConfig` (ChunkSize=32, ColumnChunks=1..8, BaseHeight/HeightScale > 0), `NoiseStack` (мінімум один Perlin).
- Додай `ChunkManager` у сцену, задай Player (камера), WorldGen/NoiseStack, loadRadius, maxSpawnsPerFrame, optional chunkPrefab (можна залишити None).
- Додай `ChunkSaveManager` на той самий GameObject, задай WorldGenConfig; налаштуй `loadOnSpawn`, `saveOnUnload`, `saveOnDestroy`, `compress`, `asyncWrite`, `regionSize` за потребою.
- Матеріал: зроби URP матеріал на `TerraVoxel/VoxelTriplanarURP`, вкажи Texture2DArray, TriplanarScale~0.1, LayerIndex=0; признач на префаб Chunk або через `VoxelMaterialBinder` + `VoxelMaterialLibrary`.
- LOD (опційно): створи SO `ChunkLodSettings`, налаштуй рівні (MinDistance/MaxDistance/LodStep/Hysteresis/Mode); на `ChunkManager` встанови `enableFullLod=true`, признач `lodSettings`.
- Occlusion (опційно): додай `ChunkOcclusionCuller` на той самий GameObject, налаштуй `frustumCulling`, `raycastOcclusion`, `maxChecksPerFrame`.
- SVO (опційно): додай `SvoManager` на той самий GameObject; SVO використовується автоматично якщо LOD‑рівень має `Mode=Svo`.

## Відомі обмеження/артефакти
- Немає greedy‑зшивання між чанками (тільки cull на межах).
- Колонкова висота обмежується `ColumnChunks`; для тестів став 1.
- Far‑range LOD: черга _farRangeRenderQueue заповнюється (enableFarRangeLod, farRangeRadius), але spawn render‑only чанків з low LOD/SVO ще не реалізовано; LOD працює в межах активного радіуса.
- Немає fade‑переходів LOD (hard swap + hysteresis).
- Немає контролера камери/гравця у репо.
- Немає води/освітлення, лише простий ламберт у шейдері.

## Налаштування за замовчуванням (рекомендовано для тесту)
- `WorldGenConfig`: ChunkSize=32, ColumnChunks=1, BaseHeight=8..16, HeightScale=12..24, HorizontalScale=0.015..0.02, Seed=будь-який.
- `NoiseStack`: один шар Perlin, Scale=0.5..1.0, Octaves=4, Persistence=0.5, Lacunarity=2.0, Weight=1.0.
- `ChunkManager`: loadRadius=1..2, maxSpawnsPerFrame=1..2, AddColliders=false (спочатку).

## Що робити далі (пріоритети)
1) Шви між чанками: кеш сусідніх даних, зняття граней тільки якщо сусідній чанк має блок.
2) Greedy merge у мешері.
3) Профайлінг HUD: час gen/mesh/apply, активні чанки, трикутники.
4) Strata/rivers: використати RockStrataConfig + NoiseStack (heightmask), зберігати матеріал по шарах.
5) Вода: рівні 0–7, висотний light/ambient простий.
6) Far‑range LOD pipeline: реалізувати spawn render‑only чанків з _farRangeRenderQueue (low LOD/SVO поза unloadRadius); зараз лише черга + cap.

