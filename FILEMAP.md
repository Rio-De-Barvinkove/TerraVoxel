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
  - `GreedyMesher.cs` — Burst greedy‑merge, face‑culling; 4-wide interior mask fill (InteriorIndex, MaskCellFromPair); NeighborData для меж чанків; опційний масштаб вокселя.
  - `MeshBuilder.cs` — копіює MeshData в Unity Mesh через NativeArray view.

- Streaming/
  - `PlayerTracker.cs` — перетворення world→chunk координат.
  - `ChunkTask.cs` — enum стани (PendingGen/…); struct для даних задач (не використовується поки).
  - `ChunkManager.cs` — фасад MonoBehaviour (partial): поля, структури (GenTask, MeshTask, CachedChunkData тощо), буфери для DropWorkQueues (_dropPendingKeep тощо); Awake/Update, публічний API, делегування модулям; fallback DropWorkQueues/ActivatePreloadedChunk/OnDestroy, safe‑spawn stubs.
  - `ChunkManager.Context.cs` — partial: внутрішній клас Context, через який модулі отримують доступ до полів і методів Owner (Active, черги, ліміти, EnsurePrefab, IsChunkBusy, QueueRemesh тощо).
  - **Менеджер кешу:** `ChunkCacheManager.cs` — модуль (internal): CacheChunkData, ComputeMeshCacheHash, TryQueueCachedMesh, Register/Release/Evict mesh cache, TryLoadFromCache, ReleaseFaceCacheForChunk; при _cache!=null ChunkManager делегує йому. Fallback при _cache==null — `ChunkManager.Cache.cs` (partial): ті самі методи в тілі ChunkManager.
  - **Логіка сусідів:** `ChunkManager.Neighbors.cs` — partial ChunkManager (окремого менеджера немає): TryGetChunk, RequestRemesh, ApplyChunkLayer, SetLayerRecursively, RebuildNeighbors, RebuildNeighborsInner, InvalidateNeighborFace, QueueRemesh, TryDequeueClosestRemesh.
  - `ChunkManager.Removal.cs` — partial: ProcessRemovalQueue, QueueRemoval, RemoveChunk.
  - `ChunkManager.Pending.cs` — partial: pending queue, радіуси; TryFindClosestPending — O(log n) через min-heap за 2D відстанню (_pendingDistanceHeap) при відсутності viewCone; TryDequeuePending при viewCone.Enabled використовує viewCone heap (O(log n)); ShouldRebuildPending, RebuildPendingQueue, DropOnePendingOldest, TryFindFarthestPending, IsWithinKeepRadius, IsWithinLoadRadius, GetInitialLodStep.
  - `ChunkManager.Jobs.cs` — partial: gen/mesh/face jobs (CompleteAllJobs, ProcessGenJobs, ProcessMeshJobs, IsChunkBusy, IsChunkGenerating, ScheduleGenJob, ScheduleMeshForChunk, GatherNeighborCopies, GatherNeighborCopiesLod, DownsampleMaterials, GetMeshMaterialSettings, ProcessFaceRemeshQueue, ProcessFaceMeshJobs, ScheduleFaceRemeshJobAsync, ProcessRemeshQueue).
  - `ChunkManager.Spawn.cs` — partial: EnsurePrefab, ActivatePreloadedChunk, SpawnChunk.
  - `ChunkManager.Lifecycle.cs` — partial: MaintainRadius, ProcessFarRangeLod, ProcessPending, ProcessPreload.
  - `ChunkLoader.cs` — модуль (internal): MaintainRadius, ProcessPending, ProcessPreload, ProcessRemovalQueue; делегує SpawnChunk/RemoveChunk/QueueRemoval/TryDequeuePending/IsWithinLoadRadius до Owner.
  - `ChunkJobsManager.cs` — модуль: ProcessGenJobs, ProcessMeshJobs, ProcessFaceMeshJobs, ProcessFaceRemeshQueue, ProcessRemeshQueue, ScheduleGenJob, ScheduleMeshForChunk, ScheduleFaceRemeshJobAsync, GatherNeighborCopies, GatherNeighborCopiesLod, DownsampleMaterials, GetMeshMaterialSettings, CompleteAllJobs, IsChunkBusy, IsChunkGenerating; делегує до Owner.
  - `ChunkIntegrationManager.cs` — модуль: ProcessIntegrationQueue, HasAnySolid, IsInIntegrationSet.
  - `ChunkAdaptiveLimitsManager.cs` — модуль: UpdateAdaptiveLimits; throttle по gen/mesh/integration/memory/GPU, cooldown.
  - `ChunkWorkDropManager.cs` — модуль: MaybeDropWork, ResolveViewForward, DropWorkQueues (pending/preload/remesh/face/integration); DropWorkQueues використовує переиспользувані буфери з Context (без new List на виклик).
  - `ChunkSafeSpawnManager.cs` — модуль: TryInitSafeSpawn, ApplySafeSpawnToChunk, ReapplySafeSpawnToChunk, SnapPlayerToSafeSpawn, SetPlayerFrozen.
  - `ChunkPhysicsManager.cs` — модуль: SetCollidersEnabled, Tick; колайдери по радіусу (замість прямого виклику ChunkPhysicsOptimizer з ChunkManager).
  - `ChunkJobHandles.cs` — хендли Job + буфери gen/mesh/face (ChunkGenJobHandle, ChunkMeshJobHandle, FaceMeshJobHandle, NeighborDataBuffers).
  - `StreamingTimeBudget.cs` — ліміт часу на стрімінг за кадр.
  - `ChunkPhysicsOptimizer.cs` — колайдери тільки в активному радіусі; lock _stateLock; tooltips; PruneMissingInner doc (використовується ChunkPhysicsManager або безпосередньо ChunkManager).
  - `ChunkViewConePrioritizer.cs` — max-heap + min-heap; O(log n) dequeue (TryDequeue) і O(log n) remove-lowest (TryRemoveLowestPriority через _minHeap); при viewCone.Enabled TryDequeuePending циклом TryDequeue поки _pendingSet.Remove; EnqueueWithPriority, ComputeScore; DistanceOnly (default true) — score = 1/(1+dist); IsInViewCone; Clear() trim capacity.
- LOD/
  - `ChunkLodLevel.cs` — struct LOD‑рівня (MinDistance, MaxDistance, LodStep, Hysteresis, Mode); IsValid; MaxDistanceWithHysteresis (overflow‑safe, XML: use for all runtime MaxDistance+Hysteresis, int.MaxValue handled); ChunkLodMode: Mesh, Svo, Billboard, None.
  - `ChunkLodSettings.cs` (SO) — список рівнів, DefaultLevelFarDistance (0 = disabled doc), UseDefaultHysteresisWhenZero; OnValidate (overlap/duplicate/gap/far‑range/single-chunk Min=Max warning, single pass); GetDetailRank, GetDetailRankFor; TryGetLevelForDistance; ResolveLevel hysteresis (overflow‑safe, target MaxDistance==int.MaxValue comment).
  - `ChunkLodManager.cs` — streaming-side LOD (internal): ProcessFullLod, ProcessLodUpgrades; main-thread only, no sync; dist >= 0 before ResolveLevel; делегує ProcessFarRangeLod, GetInitialLodStep до ChunkManager.
- Occlusion/
  - `ChunkOcclusionCuller.cs` — frustum + optional raycast occlusion; lock _occludedLock; _activeCoordsThisTick cleanup; recheckOccludedPerFrame (бюджет повторної перевірки _occluded щокадру); GetRaycastMask warning; AnyRayUnblocked, GetChunkBounds, RestoreAll doc.
- Svo/
  - `SvoVolume.cs` — структура SVO (Node byte Material/Density; RootSize, LeafSize, NativeList); Dispose() обовʼязково, safe to call multiple times; Material 0–255, >256 матеріалів потребує mapping.
  - `SvoBuilder.cs` — побудова SVO з ChunkData (queue‑based); SampleNeighbor bounds (XMin/XMax size³, Y/Z size²), early return when no face provided; caller must Dispose volume; IsUniformRegion/SampleRegionMaterialAndDensity O(size³).
  - `SvoMeshBuilder.cs` — генерація Mesh з SVO (stack traverse); BuildMesh/GetMaterialAt/HasSolidNeighbor (null/IsCreated checks, boundary = empty); AppendQuad doc; mesh color R channel = material index.
  - `SvoManager.cs` — кеш SVO‑мешів, lock _cacheLock; hash‑based reuse, LRU evict (LinkedList, O(1) per evict); TryGetOrBuildMesh exception → no cache, volume disposed in finally; useGpuRaymarch not implemented (tooltip); read‑mostly.

- Rendering/
  - `VoxelMaterialLibrary.cs` (SO) — Texture2DArray, TriplanarScale, NormalStrength, DefaultLayerIndex.
  - `VoxelMaterialBinder.cs` — на Renderer ставить `_MainTexArr`, `_TriplanarScale`, `_NormalStrength`, `_LayerIndex` з library.
  - `SrpBatchingConfig.cs` — конфіг для SRP Batching (voxelMaterial, voxelMaterialLibrary); Configure(), ApplyToChunk(); один шейдер, без MaterialPropertyBlock для batching‑критичних властивостей.

- Systems/
  - `ChunkSaveStub.cs` — JSON‑stub (legacy, не використовується).
  - `ProfilerHooks.cs` — простий Stopwatch wrapper (stub).
  - `VoxelAnalysisMode.cs` — F2 fly/no‑clip, freeze streaming, shadow toggle, cursor lock; увімкнено в release за замовчуванням.
  - `VoxelDebugHUD.cs` — HUD, графіки, CSV‑експорт, async summary‑лог, черги/інтеграція.

- Save/
  - `ChunkSaveBinary.cs` — бінарний формат, magic+version+flags; LZ4/GZip (v1) або RLE (useRle) декомпресія; матеріали (ushort) + опційна щільність; CRC.
  - `ChunkModBinary.cs` — бінарні дельти (index+material ushort), LZ4 або RLE (useRle), CRC.
  - `RleCompression.cs` — RLE compress/decompress для byte[] (run = value, count 1..255); використовується в ChunkSaveBinary та ChunkModBinary при useRle.
  - `ChunkSaveMode.cs` — enum режимів сейву + ChunkMeta.
  - `ChunkSaveManager.cs` — async save‑черга, атомарний запис, load on spawn, save on unload/destroy; worldId/region‑папки; useRle опція; join timeout.
  - `ChunkModManager.cs` — менеджер модифікацій (delta‑сейви), async/atomic; useRle опція; пакетні правки; join timeout.
  - `ChunkHybridSaveManager.cs` — правила delta vs snapshot.
  - `VoxelModDebugInput.cs` — режим взаємодії (B), перемикання dig/build (V), raycast, brush size 1‑10, форми, підсвітка.
  - `Lz4Codec.cs` — C# LZ4‑кодек (вбудований, без зовнішніх залежностей).
  - `Crc32.cs` — CRC32 для файлів сейву.

- `TerraVoxel.Voxel.asmdef` — залежності Burst/Collections/Mathematics/Jobs/URP.

## Editor (Assets/Editor)
- `PaletteTextureArrayBuilder.cs` — генерація Texture2DArray палітри (32 базові кольори × 8 яскравостей).
- `LayerSetupTool.cs` — налаштування шарів (terrain, objects, player, UI).
- `ForceActiveInputHandling.cs` — примусове активне оброблення вводу в Editor.

## Інші скрипти (Assets/Scripts)
- `PlayerSimpleController.cs` — простий контролер гравця (поза Voxel; можна замінити на зовнішній пакет).

## Документація (Documentation/)
- `Generation_Architecture_Analysis.md` — архітектура генерації, черги, LOD/SVO/occlusion.

## Шейдери / матеріали
- `Assets/Shaders/VoxelTriplanarURP.shader` — URP opaque, тріпланар семпл Texture2DArray, параметри `_TriplanarScale`, `_LayerIndex`, `_NormalStrength`.

## Потік даних (мінімальна реалізація)
1) `ChunkManager.Update` оновлює jobs (gen/mesh), підтримує радіуси, _pending/_preload, ремеш/видалення. При наявності модулів виклики делегуються (_loader.MaintainRadius, _jobs.ProcessGenJobs тощо), інакше виконуються методи самого ChunkManager.
2) `ProcessPending` бере до `maxSpawnsPerFrame` з pending: якщо viewCone увімкнено і **не** DistanceOnly — `viewCone.TryDequeue` (heap); інакше — `TryFindClosestPending(center)` (пріоритет за поточною відстанню до гравця). Додавання в pending — _pendingSet.Add + EnqueueWithPriority або _pendingSet; при cap — DropOnePendingOldest.
3) `SpawnChunk`: Allocate `ChunkData` → спроба snapshot‑load (hybrid або save manager) → якщо немає, планується gen‑job.
4) Завершення gen‑job: apply safe‑spawn (якщо треба), apply delta‑mods. Якщо initialLodFromDistance — ResolveLevel( dist ); для None — вимкнути renderer/collider; для Svo — TryGetOrBuildMesh; для Mesh — план mesh‑job з потрібним LodStep.
5) Завершення mesh‑job: постановка в integration queue → ProcessIntegrationQueue → ApplyMesh/ApplySharedMesh (ліміт/кадр), ремеш сусідів (edge‑only або full), колайдери. Порожні чанки (HasAnySolid=false) не ремешаються безкінечно — renderer/collider вимикаються.
6) `ProcessFullLod` (якщо `enableFullLod`): перевіряє дистанцію, обирає LOD‑рівень з `ChunkLodSettings`, виконує upgrade/downgrade (mesh або SVO) з cooldown.
7) `ChunkOcclusionCuller.Tick`: frustum culling + optional raycast, вимикає renderer для occluded чанків; lock _occludedLock; очищення застарілих _occluded по _activeCoordsThisTick.
8) При `RemoveChunk` (вихід з радіуса) `ChunkHybridSaveManager` або `ChunkSaveManager` + `ChunkModManager` → atomic write; `SvoManager.ReleaseForChunk`; при `OnDestroy` — save all активних.

## Що вже зроблено / статус TODO
- Готово: константи/структури/пул; генератор (heightmap, Burst) + slicing; greedy‑meshing з neighbor‑culling; стрімінг із чергами/лімітами + інтеграція; view‑cone з DistanceOnly (пріоритет за відстанню через TryFindClosestPending); mesh/data cache; lock-free integration queue (ConcurrentQueue + ConcurrentDictionary) + recursion guards; safe spawn timeout + fallback; pending _pendingSet membership; initial LOD from distance; edge‑only remesh async; scaleJobsByProcessorCount; SRP Batching; LOD overflow/hysteresis fix; empty chunk remesh fix; RebuildNeighborsInner Y bounds; seam skirts; повний LOD + occlusion + SVO; StreamingTimeBudget; тріпланар шейдер + SrpBatchingConfig/VoxelMaterialLibrary/VoxelMaterialBinder; LZ4‑chunk save; 256‑палітра; analysis mode; VoxelDebugHUD; **модуляризація ChunkManager** (ChunkLoader, ChunkJobsManager, ChunkIntegrationManager, ChunkLodManager, **ChunkCacheManager** (менеджер кешу), **ChunkManager.Neighbors** (partial — логіка сусідів), ChunkAdaptiveLimitsManager, ChunkWorkDropManager, ChunkSafeSpawnManager, ChunkPhysicsManager + ChunkManager.Context).
- Немає (потрібно доробити): strata/rivers/biomes/erosion; вода; far‑range LOD pipeline (spawn render‑only поза unloadRadius); greedy‑зшивання між чанками; Impostors/Billboards рендер; GPU Instancing; повноцінний save/load менеджер (світ/інв/позиція); рушійний контролер гравця (зовнішній).

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
- Occlusion (опційно): додай `ChunkOcclusionCuller` на той самий GameObject, налаштуй `frustumCulling`, `raycastOcclusion`, `maxChecksPerFrame`, `recheckOccludedPerFrame`, `tickBudgetMs`; при відсутності шару `occluderLayerName` використовується occluderMask (warning в лог).
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
1) CPU‑оптимізації (ROADMAP): TryFindClosestPending O(n)→просторова структура; pool для List у DropWorkQueues/MaintainRadius; троттлинг ProcessFullLod; frame budgeting у всіх циклах.
2) Greedy‑зшивання між чанками (зараз тільки cull на межах).
3) Strata/rivers: RockStrataConfig + NoiseStack, матеріал по шарах.
4) Вода: рівні 0–7, висотний light/ambient.
5) Far‑range LOD pipeline: spawn render‑only з _farRangeRenderQueue (low LOD/SVO поза unloadRadius).
6) SaveLoadManager: інвентар гравця, позиція гравця, екрани світів.

