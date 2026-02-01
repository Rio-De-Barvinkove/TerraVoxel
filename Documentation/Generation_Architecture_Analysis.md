# Аналіз архітектури генерації TerraVoxel

> **Оновлено:** Реалізовано жорсткий пріоритет за відстанню: RemeshQueue, Pending (fallback), ChunkViewConePrioritizer.distanceOnly.

---

## 1. Контекст: microvoxel-світ

| Параметр | Значення | Примітка |
|----------|----------|----------|
| VoxelSize | 0.1 (10 см) | Microvoxel |
| ChunkSize | 32 | 32³ вокселів ≈ 3.2 м |
| ColumnChunks | 8 | Висота світу |
| WorldHeight | 256 вокселів | 25.6 м |

**Наслідки:** велика кількість чанків на одиницю відстані, висока щільність полігонів. Потрібні пріоритет за відстанню, LOD, greedy meshing, occlusion.

---

## 2. Діаграма компонентів і потоку даних

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           ChunkManager (центральний контролер)           │
├─────────────────────────────────────────────────────────────────────────┤
│  MaintainRadius()          │  ProcessPending()     │  ProcessRemeshQueue()│
│  → Pending / Preload       │  → SpawnChunk         │  → ScheduleMeshForChunk│
│  → RemoveQueue             │  → ScheduleGenJob     │  → TryDequeueClosestRemesh│
├─────────────────────────────────────────────────────────────────────────┤
│  ProcessFullLod()          │  ChunkOcclusionCuller.Tick()                │
│  → LOD / SVO               │  → Frustum + Raycast → Renderer.enabled     │
└─────────────────────────────────────────────────────────────────────────┘
         │                              │                      │
         ▼                              ▼                      ▼
┌──────────────────┐    ┌──────────────────────┐    ┌─────────────────────┐
│ ChunkViewCone    │    │ SvoManager           │    │ GreedyMesher        │
│ Prioritizer      │    │ (дальні чанки)       │    │ (LOD1+ mesh)        │
│ heap / distance  │    │ SVO → mesh           │    │ Greedy meshing      │
└──────────────────┘    └──────────────────────┘    └─────────────────────┘

Потік одного чанка:
  [Pending] → SpawnChunk → GenJob → Integration → RemeshQueue → MeshJob → Integration
                                                       ↑
  [LOD transition] ←───────────────────────────────────┘  (ProcessFullLod)
```

---

## 3. Поточний стан черг

| Черга | Тип | Пріоритет за відстанню? |
|-------|-----|-------------------------|
| **Pending (генерація)** | viewCone heap або _pendingSet + min-distance | **Так** (viewCone.distanceOnly або closest-first) |
| **RemeshQueue** | _remeshSet + TryDequeueClosestRemesh | **Так** (closest-first, min-heap semantics) |
| **FaceRemeshQueue** | FIFO Queue | Ні (лишається FIFO) |
| **IntegrationQueue** | FIFO Queue | Ні |
| **Preload** | FIFO Queue | Ні |
| **RemoveQueue** | FIFO + попередня сортування | **Так** (найдальші першими) |

---

## Pending (генерація чанків)

### Якщо `viewCone` увімкнено
- **ChunkViewConePrioritizer**: max-heap. Опція `distanceOnly` — суворий `priority = 1/(1+dist)`.
- Без distanceOnly: score = distanceWeight * (1/(1+dist)) + dotWeight * viewScore + visualBonus (view cone може домінувати).

### Якщо `viewCone` вимкнено
- **_pendingSet** + `TryFindClosestPending(center)` — **closest-first** (min-heap semantics).
- При cap — `TryFindFarthestPending` дропає найдальший.

---

## LOD / SVO / Occlusion — порядок пайплайну

```
[Генерація] → [Meshing] → [Integration] → [LOD] → [Occlusion] → [Рендер]
```

### LOD (ProcessFullLod)

- Запускається **після** RemeshQueue (меш уже застосований або в польоті).
- Для кожного активного чанка: `dist = max(|dx|, |dz|)` від гравця.
- **ChunkLodSettings.ResolveLevel(dist, currentStep, currentMode)** → target level (LodStep, Mode).
- **Режими (ChunkLodMode):** Mesh, Svo, Billboard, None.
- **LodStep:** 1 = full detail, 2+ = downsampled mesh (менше полігонів).
- Якщо `desired.Mode == Svo` → SvoManager будує SVO-меш, `chunk.UsesSvo = true`.
- Інакше → ScheduleMeshForChunk з потрібним LodStep, `chunk.UsesSvo = false`.
- Обмеження: `maxLodTransitionsPerFrame`, `lodTransitionCooldown`.

### SVO (Sparse Voxel Octree)

- Застосовується для **дальніх** чанків (згідно ChunkLodSettings).
- SVO дає компактне представлення + меш нижчої деталізації.
- `chunk.UsesSvo = true` → чанк рендерить SVO-меш замість greedy-mesh.
- SVO-чанки **не проходять** raycast occlusion (пропускаються для простіших bounds і швидкості).

### Occlusion (ChunkOcclusionCuller.Tick)

- Запускається **останнім** в Update (після LOD).
- **Frustum culling:** чанки поза камерою → `SetRendererEnabled(false)`.
- **Raycast occlusion:** лише для **повної деталізації** (`!UsesSvo && LodStep <= 1`). Якщо промені з камери до bounds блокується — чанк ховається.
- LOD/SVO чанки **не проходять** raycast — завжди видимі (єдинорідні bounds, менше raycast'ів).
- Сортування кандидатів за відстанню (closest-first) для послідовної перевірки.

### Взаємодія

| Система | Що змінює |
|--------|-----------|
| LOD | LodStep, Mode (Mesh/Svo/Billboard/None), UsesSvo |
| SVO | Представлення дальніх чанків (меш з SVO) |
| Occlusion | Renderer.enabled (видимість після frustum + raycast) |

- **Occlusion** працює **після** LOD — бачить фінальний LodStep/UsesSvo.
- **LOD** не залежить від occlusion — рішення лише за dist.
- **SVO** — один із режимів LOD для дальніх чанків.

---

## RemeshQueue

- **_remeshSet** + `TryDequeueClosestRemesh(center)` — **closest-first** (min-heap semantics).
- Кожен frame обробляються найближчі чанки з черги.

---

## Вплив на RemeshQ backlog

При 2000+ елементах у RemeshQ та FIFO:
- Обробляються спочатку старі (часто дальні) чанки
- Ближні можуть довго чекати
- Створюється відчуття "поганої" підгрузки

З пріоритетом за відстанню:
- Спочатку обробляються найближчі чанки
- Деталізація біля гравця оновлюється швидше

---

## 4. Масштабованість та обмеження

| Механізм | Значення | Призначення |
|----------|----------|-------------|
| pendingQueueCap | 4096 | Обмеження Pending; при перевищенні — DropOnePendingOldest |
| maxRemeshPerFrame | 10 | Макс. remesh на frame |
| maxLodTransitionsPerFrame | 8 | Макс. LOD-переходів на frame |
| farRangeQueueCap | 1024 | Cap для far-range LOD stub |
| maxIntegrationQueueSize | 2000 | Охорона IntegrationQueue |
| Adaptive limits | gen/mesh/integration | Зниження лімітів при перевантаженні |

**Обмеження черг:** Pending, IntegrationQueue, FarRange мають cap. RemeshSet/FaceRemeshSet — без cap; при великому backlog TryDequeueClosestRemesh робить O(n) scan на кожен dequeue.

---

## 5. Синхронізація черг (уникнення дублювання)

| Черга | Перевірка перед додаванням | Конфлікт з іншою чергою |
|-------|----------------------------|--------------------------|
| Pending | _pendingSet.Contains | — |
| RemeshQueue | _remeshSet.Add (idempotent) | _faceRemeshSet: якщо coord у _remeshSet → skip face-only |
| FaceRemeshQueue | _faceRemeshSet | Якщо _remeshSet.Contains → ProcessFaceRemeshQueue пропускає, викликає QueueRemesh |
| IntegrationQueue | _integrationSet (lock) | QueueRemesh/ProcessRemeshQueue: якщо IsInIntegrationSet → _remeshAfterIntegration |

**Інваріант:** один coord не може одночасно бути в _meshJobs і в черзі remesh. QueueRemesh перевіряє _meshJobs.ContainsKey перед додаванням.

---

## 6. Крайові випадки

| Ситуація | Обробка |
|----------|---------|
| Чанк видалено під час обробки | RemoveChunk: видаляє з _integrationSet, _pendingMeshJobs, _remeshSet; RebuildNeighbors |
| Budget exceeded | ProcessPending/ProcessRemeshQueue переривають цикл |
| Streaming paused | ProcessRemeshQueue, ProcessFaceRemeshQueue виконуються; Pending/Preload ні |
| Radius change (телепорт) | DropWorkQueues: скидає черги, зберігає тільки in-range |
| Chunk generating | QueueRemesh → re-enqueue; ProcessRemeshQueue → skip, Add назад |

---

## 7. Потенційні вразливості

1. **O(n) scan у TryDequeueClosestRemesh / TryFindClosestPending**  
   При n > 2000 — до 10 × 2000 ітерацій на frame. Для подальшої оптимізації: min-heap з distSq, перебудова при зміні center.

2. **FaceRemeshQueue FIFO**  
   Без пріоритету за відстанню. Можливе покращення — аналог TryDequeueClosestRemesh.

3. **RemeshSet без cap**  
   При дуже великому radius можливий необмежений backlog. Розглянути eviction за відстанню.

4. **IntegrationQueue FIFO**  
   Порядок = завершення job. Для microvoxel не критично; пріоритет за відстанню — опційно.

---

## 8. Відповідність вимогам

| Вимога | Статус |
|--------|--------|
| Масштабованість (великі світи) | Cap на Pending, Integration; adaptive limits. RemeshSet без cap — ризик. |
| Відмовостійкість | Re-enqueue при failed ScheduleMesh; RemoveChunk очищує стани. |
| Ефективність для microvoxel | Пріоритет за відстанню, LOD, SVO, occlusion, greedy meshing. |
| Логічна послідовність | Генерація → Meshing → Integration → LOD → Occlusion — чіткий pipeline. |
