# FILEMAP

Оновлено: 2026-01-21

## Структура voxel-підсистеми (Assets/Scripts/Voxel)
- Core/
  - `VoxelConstants.cs` — константи масштабу (ChunkSize=32, ColumnChunks=8, VoxelSize=0.1m).
  - `VoxelMath.cs` — clamp‑утиліти для безпечних конвертацій координат.
  - `ChunkCoord.cs` — координата чанка (X,Y,Z), хеш/ToString.
  - `VoxelMaterial.cs` — ushort enum (Air, Dirt, Stone, Sand, Water).
  - `ChunkData.cs` — буфери NativeArray<ushort> Materials, NativeArray<float> Density (опційний); Index/Bounds; Allocate/Dispose.
  - `Chunk.cs` — MonoBehaviour для інстансу чанка; тримає MeshFilter/Renderer/Collider, Mesh; ApplyMesh; ставить дефолтний URP Lit матеріал.
  - `ChunkPool.cs` — пул Chunk-інстансів; активує отримані.

- Generation/
  - `WorldGenConfig.cs` (SO) — Seed, ChunkSize, ColumnChunks, BaseHeight, HeightScale, HorizontalScale, EnableRivers, DefaultMaterialIndex; safe‑spawn платформа (EnableSafeSpawn, SizeChunks, Thickness, MaterialIndex, Snap, Revalidate).
  - `NoiseStack.cs` (SO) — масив NoiseLayer (Type: Perlin/Simplex/Voronoi, Scale, Octaves, Persistence, Lacunarity, Weight).
  - `RockStrataConfig.cs` (SO) — болванка для товщин шарів (sed/met/ig).
  - `IChunkGenerator.cs` — інтерфейс генератора.
  - `ChunkGenerator.cs` — Burst IJobParallelFor заповнює Materials за heightmap; шуми з NoiseStack у NativeArray; матеріал з DefaultMaterialIndex.

- Meshing/
  - `MeshData.cs` — NativeList<Vertex/Index/Normal>, Dispose.
  - `GreedyMesher.cs` — Burst greedy‑merge по 3 осях, face‑culling, коректне CW winding; підтримка NeighborData для меж чанків.
  - `MeshBuilder.cs` — копіює MeshData в Unity Mesh через NativeArray view.

- Streaming/
  - `PlayerTracker.cs` — перетворення world→chunk координат.
  - `ChunkTask.cs` — enum стани (PendingGen/…); struct для даних задач (не використовується поки).
  - `ChunkManager.cs` — стрімінг чанків, `_pending`/`maxSpawnsPerFrame`, ремеш‑черга `maxRemeshPerFrame`, safe‑spawn платформа + snap, jobs gen/mesh, preload, гібридні сейви, колайдери on/off, streaming pause.
  - `ChunkJobHandles.cs` — хендли Job + NativeArray буфери для gen/mesh.
  - `StreamingTimeBudget.cs` — ліміт часу на стрімінг за кадр.
  - `ChunkPhysicsOptimizer.cs` — колайдери тільки в активному радіусі.
  - `ChunkViewConePrioritizer.cs` — пріоритет pending‑черги по напрямку камери.

- Rendering/
  - `VoxelMaterialLibrary.cs` (SO) — Texture2DArray, TriplanarScale, NormalStrength, DefaultLayerIndex.
  - `VoxelMaterialBinder.cs` — на Renderer ставить `_MainTexArr`, `_TriplanarScale`, `_NormalStrength`, `_LayerIndex` з library.

- Systems/
  - `ChunkSaveStub.cs` — JSON‑stub (legacy, не використовується).
  - `ProfilerHooks.cs` — простий Stopwatch wrapper (stub).
  - `VoxelAnalysisMode.cs` — F2 fly/no‑clip, freeze streaming, shadow toggle, cursor lock; увімкнено в release за замовчуванням.
  - `VoxelDebugHUD.cs` — HUD, профайлер графіків, CSV‑експорт, async summary‑лог.

- Save/
  - `ChunkSaveBinary.cs` — бінарний формат, magic+version+flags; LZ4/GZip (v1) декомпресія; матеріали (ushort) + опційна щільність; CRC.
  - `ChunkModBinary.cs` — бінарні дельти (index+material ushort), LZ4, CRC.
  - `ChunkSaveMode.cs` — enum режимів сейву + ChunkMeta.
  - `ChunkSaveManager.cs` — async save‑черга, атомарний запис, load on spawn, save on unload/destroy; worldId/region‑папки.
  - `ChunkModManager.cs` — менеджер модифікацій (delta‑сейви), async/atomic; пакетні правки вокселів.
  - `ChunkHybridSaveManager.cs` — правила delta vs snapshot.
  - `VoxelModDebugInput.cs` — режим взаємодії (B), перемикання dig/build (V), raycast, brush size 1‑10, форми, підсвітка.
  - `Lz4Codec.cs` — C# LZ4‑кодек (вбудований, без зовнішніх залежностей).
  - `Crc32.cs` — CRC32 для файлів сейву.

- `TerraVoxel.Voxel.asmdef` — залежності Burst/Collections/Mathematics/Jobs/URP.

## Шейдери / матеріали
- `Assets/Shaders/VoxelTriplanarURP.shader` — URP opaque, тріпланар семпл Texture2DArray, параметри `_TriplanarScale`, `_LayerIndex`, `_NormalStrength`.

## Потік даних (мінімальна реалізація)
1) `ChunkManager.Update` оновлює jobs (gen/mesh), підтримує радіуси, _pending/_preload, ремеш/видалення.
2) `ProcessPending` бере до `maxSpawnsPerFrame` → `SpawnChunk` (pending може бути пріоритизований view‑cone).
3) `SpawnChunk`: Allocate `ChunkData` → спроба snapshot‑load (hybrid або save manager) → якщо немає, планується gen‑job.
4) Завершення gen‑job: apply safe‑spawn (якщо треба), apply delta‑mods → план mesh‑job.
5) Завершення mesh‑job: `Chunk.ApplyMesh`, ремеш сусідів, активація колайдера за умовами.
6) При `RemoveChunk` (вихід з радіуса) `ChunkHybridSaveManager` або `ChunkSaveManager` + `ChunkModManager` → atomic write; при `OnDestroy` — save all активних.

## Що вже зроблено / статус TODO
- Готово: константи/структури/пул; генератор (heightmap, Burst); greedy‑meshing з neighbor‑culling; стрімінг із чергою/лімітом + ремеш‑чергою; тріпланар шейдер+SO+биндер; LZ4‑chunk save (async + atomic); 256‑палітра; analysis mode (noclip, freeze streaming, HUD, toggle shadows).
- Немає (потрібно доробити): strata/rivers/biomes/erosion; вода/LOD/occlusion; greedy‑зшивання між чанками; повноцінний save/load менеджер (світ/інв/позиція); рушійний контролер гравця (зовнішній).

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

## Відомі обмеження/артефакти
- Немає greedy‑зшивання між чанками (тільки cull на межах).
- Колонкова висота обмежується `ColumnChunks`; для тестів став 1.
- Немає LOD.
- Немає контролера камери/гравця у репо.
- Немає води/освітлення/оклюзії, лише простий ламберт у шейдері.

## Налаштування за замовчуванням (рекомендовано для тесту)
- `WorldGenConfig`: ChunkSize=32, ColumnChunks=1, BaseHeight=8..16, HeightScale=12..24, HorizontalScale=0.015..0.02, Seed=будь-який.
- `NoiseStack`: один шар Perlin, Scale=0.5..1.0, Octaves=4, Persistence=0.5, Lacunarity=2.0, Weight=1.0.
- `ChunkManager`: loadRadius=1..2, maxSpawnsPerFrame=1..2, AddColliders=false (спочатку).

## Що робити далі (пріоритети)
1) Шви між чанками: кеш сусідніх даних, зняття граней тільки якщо сусідній чанк має блок.
2) Greedy merge у мешері + простий LOD (дальній чанк — агрегація).
3) Профайлінг HUD: час gen/mesh/apply, активні чанки, трикутники.
4) Strata/rivers: використати RockStrataConfig + NoiseStack (heightmask), зберігати матеріал по шарах.
5) Вода/occlusion: рівні 0–7, висотний light/ambient простий.

