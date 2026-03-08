---
name: ""
overview: ""
todos: []
isProject: false
---

# GPU Pipeline Fix & Refactor (розширений)

## Поточна ситуація

- **CPU (loadRadius 20):** 60–80 FPS, стабільно
- **GPU (loadRadius 7–10):** 7–10 FPS
- **Архітектура:** CPU оркеструє, GPU виконує обчислення

---

## Критичні вузькі місця (з аналізу FILEMAP + коду)

### 1. GpuChunkAnalyzer — обробка всіх maxChunks

**Проблема:** `ScheduleAnalysis` обробляє `maxChunks × voxelsPerChunk` вокселів (≈117M при 3579 чанках), хоча активних слотів може бути ~500–2000.

**Рішення:** Аналізувати лише активні слоти за допомогою буфера `ActiveSlotIndices`.

**Файли:** GpuWorldState.cs, GpuChunkAnalyzer.cs, ChunkAnalysis.compute

---

### 2. GpuMesher — sync readback face count

**Проблема:** `_faceCounter.GetData(_faceCountReadback)` блокує CPU після кожного `MeshChunk`.

**Рішення:** GenerateVertices читає FaceCounter з буфера замість отримання faceCount через readback.

**Файли:** GpuMesher.cs, VoxelMeshing.compute

---

### 3. ChunkLodManager.ProcessFullLod — O(n) кожен кадр

**Проблема:** При `enableFullLod` ітерація по всіх `_ctx.Active` (2000+ чанків), кожен chunk: `GetGpuChunkFlags`, `IsChunkBusy`, `Integration.IsInIntegrationSet`, `PendingCachedMeshes.ContainsKey`. `new List<>` для upgrades/downgrades кожен виклик.

**Рішення:** Троттлинг ProcessFullLod (FILEMAP "Що робити далі" line 385), переиспользувані списки для upgrades/downgrades замість `new List` на виклик.

**Файли:** ChunkLodManager.cs

---

### 4. ChunkManager.MaintainRadius — цикл і remove candidates

**Проблема:** При loadRadius 7: 15×15×8 = 1800 ітерацій. Кожна: `_active.TryGetValue`, `_pendingSet.Contains`, можливо `DropOnePendingOldest` (O(n) по _pendingSet). `_removeCandidates` — foreach по _active, sort O(n log n).

**Рішення:** `DropOnePendingOldest` при TryFindFarthestPending — O(n) scan. Можливо обмежити кількість перевірок або вести просторову структуру.

---

### 5. RepopulateViewConeFromPendingSet — O(n) heap ops

**Проблема:** Коли viewCone.Count == 0 і _pendingSet.Count > 0, foreach по _pendingSet + EnqueueWithPriority для кожного. При GPU path viewCone може бути порожнім — умова може спрацьовувати. EnqueueWithPriority = O(log n) × n.

**Рішення:** При UseGpuPipeline не викликати RepopulateViewConeFromPendingSet (або viewCone не використовується для dequeue).

---

### 6. DropWorkQueues — O(n) по 8 множинах

**Проблема:** При work drop: foreach по _pendingSet, _preloadSet, _remeshSet, _faceRemeshSet, _faceMeshJobs, _pendingMeshJobs, _pendingCachedMeshes, _remeshAfterIntegration. Потім viewCone.EnqueueWithPriority для кожного _dropPendingKeep.

**Рішення:** Рідко викликається (cooldown). Залишити; при потребі — обмежити кількість перевірок за виклик.

---

### 7. GpuCuller — алокація Plane[] і Vector4[] кожен кадр

**Проблема:** `GeometryUtility.CalculateFrustumPlanes(camera)` + `new Vector4[6]` кожен кадр. Дрібна алокація, але GC pressure.

**Рішення:** Кешувати `Vector4[6]` в полі класу, перезаписувати кожен кадр.

**Файли:** GpuCuller.cs

---

### 8. ChunkHybridSaveManager / readback при unload

**Проблема:** `HandleChunkUnloadedGpu` → `EnqueueSaveFromGpu` → async readback. При швидкому руху гравця багато unload per frame → багато readback в чергу. Чи є cap?

**Рішення:** Перевірити чи є ліміт readback-запитів; при потребі додати throttle.

---

### 9. DownsampleLOD — мертвий код

**Проблема:** ChunkAnalysis.compute має kernel DownsampleLOD, але GpuChunkAnalyzer.ScheduleAnalysis його НЕ викликає. ChunkLodManager не викликає DownsampleLOD. FILEMAP помилково пише "делегує GpuChunkAnalyzer (DownsampleLOD на GPU)".

**Рішення:** Видалити DownsampleLOD з ChunkAnalysis.compute або реалізувати виклик; оновити FILEMAP.

---

### 10. VoxelMeshing.compute — PrefixSum

**Проблема:** FILEMAP згадує "DetectFaces, PrefixSum, GenerateVertices", але GpuMesher використовує лише DetectFaces, GenerateVertices, PadSlot. PrefixSum не використовується.

**Рішення:** Або оновити FILEMAP, або використати PrefixSum якщо він потрібен для іншої логіки.

---

### 11. ChunkManager.Cache fallback — new List при eviction

**Проблема:** Коли `_cache == null`, `EvictMeshCacheIfNeeded` створює `new List<...>(_meshCache.Count)` що кадр. Рідко (завжди є _cache), але для узгодженості — переиспользути список.

**Файли:** ChunkManager.Cache.cs

---

### 12. ChunkData при GPU — пам’ять CPU

**Проблема:** При `loadedFromCache` зберігаємо `chunk.Data` з NativeArray на CPU (для mods). 2000 чанків × 64KB ≈ 128MB.

**Рішення:** На майбутнє — можливо apply mods на GPU без збереження повної копії.

---

### 13. ApplySafeSpawnToGpu — sync readback

**Проблема:** `VoxelMaterialBuffer.GetData` + патч + SetData. Рідко (лише при safe spawn). Низький пріоритет.

---

### 14. GpuDrivenRenderer — debug GetData

**Проблема:** `DrawArgsBuffer.GetData` при `debugLogDrawArgs`. Обгорнути в `#if UNITY_EDITOR` або перевірку.

**Файли:** GpuDrivenRenderer.cs

---

### 15. ChunkCulling.compute — ChunkCount_ = MaxChunks

**Проблема:** FrustumCull, OcclusionCull, BuildDrawCommands ітерують по `ChunkCount`_ = MaxChunks (з GpuCuller). При 3579 чанках — 3579 потоків. Дефіцит: обробляємо порожні слоти.

**Рішення:** Якщо Culler має `ActiveSlotIndices` або ChunkCount, можна обмежити. Але Culler використовує ChunkDescriptors — порожній slot має vertexCount=0, flags=Empty. FrustumCull вже перевіряє `desc.vertexCount == 0` і `flags & EMPTY`. Тому порожні слоти швидко відсікаються. Менш критично.

---

### 16. ChunkManager.Pending — BuildPendingDistanceHeap

**Проблема:** При `!viewCone.Enabled` (або UseGpuPipeline) використовується TryFindClosestPending → BuildPendingDistanceHeap при зміні center. BuildPendingDistanceHeap: foreach по _pendingSet + heapify O(n). Center змінюється часто при русі — можливо heap rebuild занадто частий.

**Рішення:** Перевірити чи при GPU path використовується viewCone чи distance heap. При UseGpuPipeline viewCone не наповнюється в MaintainRadius — отже viewCone.Count може бути 0. TryDequeuePending: if viewCone.Enabled && !TryDequeue → TryFindClosestPending. Якщо viewCone.Enabled але порожній — fallback на distance. Потрібно переконатися, що при UseGpuPipeline viewCone не наповнюється в MaintainRadius — отже viewCone.Count=0, RepopulateViewConeFromPendingSet може заповнити. При viewCone.Enabled && UseGpuPipeline ми не додаємо в viewCone в MaintainRadius. Отже viewCone.Count залишається 0. RepopulateViewConeFromPendingSet: viewCone.Count > 0 → return. Значить ніколи не заповнюємо. TryDequeuePending: viewCone.TryDequeue → false (heap empty), потім TryFindClosestPending. Отже при GPU path використовується TryFindClosestPending, який потребує BuildPendingDistanceHeap. Center змінюється кожен кадр (player рухається). Тому _pendingDequeueCenter != center кожен кадр → BuildPendingDistanceHeap кожен кадр. Це O(n) по _pendingSet + O(n) heapify. При 1000+ pending — дорого кожен кадр!

**Рішення:** Кешувати center з толерансом (наприклад, оновлювати heap лише при зміні center на цілий chunk), або використовувати відстань без повного перебудови heap.

---

## Порядок виконання

1. **GpuChunkAnalyzer — активні слоти** (максимальний вплив)
2. **GpuMesher — усунення readback**
3. **ChunkManager.Pending — BuildPendingDistanceHeap throttling** (при GPU path впливає кожен кадр)
4. **ChunkLodManager — троттлинг і переиспользувані списки**
5. **GpuCuller — кеш Vector4[] для frustum**
6. **RepopulateViewConeFromPendingSet — skip при UseGpuPipeline**
7. **GpuDrivenRenderer — debug GetData**
8. **ChunkManager.Cache fallback — reuse list**
9. **DownsampleLOD — видалити або задокументувати**
10. **FILEMAP — виправити помилки (DownsampleLOD, PrefixSum, GpuCuller sync)**

---

## Файли для змін


| Файл                    | Зміни                                                    |
| ----------------------- | -------------------------------------------------------- |
| GpuWorldState.cs        | ActiveSlotIndicesBuffer                                  |
| GpuChunkAnalyzer.cs     | ActiveSlotIndices, ітерація по активних                  |
| ChunkAnalysis.compute   | Kernel AnalyzeChunkCount з ActiveSlotIndices             |
| GpuMesher.cs            | GenerateVertices читає FaceCounter з буфера              |
| VoxelMeshing.compute    | GenerateVertices читає face count з буфера               |
| ChunkManager.Pending.cs | Throttle BuildPendingDistanceHeap при зміні center       |
| ChunkLodManager.cs      | Троттлинг, reuse lists                                   |
| GpuCuller.cs            | Кеш Vector4[6]                                           |
| ChunkManager.Pending.cs | RepopulateViewConeFromPendingSet skip при UseGpuPipeline |
| GpuDrivenRenderer.cs    | debug GetData умова                                      |
| ChunkManager.Cache.cs   | fallback reuse list                                      |
| FILEMAP.md              | Виправити DownsampleLOD, PrefixSum, GpuCuller            |


