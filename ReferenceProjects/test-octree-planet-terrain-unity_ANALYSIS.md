# Аналіз test-octree-planet-terrain-unity

Порівняння з TerraVoxel для можливого покращення LOD та планетарної геометрії.

**Джерело:** [PaperPrototype/test-octree-planet-terrain-unity](https://github.com/PaperPrototype/test-octree-planet-terrain-unity)

---

## Архітектура проекту

Проект містить два незалежні режими:

1. **Planet** — октавне LOD для сферичної планети
2. **Minecraft** — плоский чанковий террейн з frustum culling

Обидва використовують **CPU Jobs (Burst)** для мешингу, без GPU compute shaders.

---

## 1. Planet — Octree LOD

### Структура

```
Octree (MonoBehaviour)
  └── Node (root)
        └── Node[8] children (рекурсивно)
```

- **Node** — вузол октави; leaf node має mesh, non-leaf — 8 дочірніх
- **divisions** — рівень деталізації (11 → nodeResolution = 2^10 = 1024)
- **chunkResolution** — вокселів на ребро ноди (16)
- **nodeScale** = chunkResolution × nodeResolution

### LOD логіка

**ShouldSubdivide(node):** перевіряє, чи `priority.position` (камера/гравці) всередині AABB ноди з padding `innerRadiusPadding`:

```csharp
float3 scale = node.NodeScale() * innerRadiusPadding + 1;
Vector3 minBound = center - scale;
Vector3 maxBound = center + scale;
return insideX && insideY && insideZ;
```

- Якщо всередині — створює 8 дочірніх (subdivide)
- Якщо зовні — видаляє дочірніх (collapse)

**Обмеження:** `maxNodeCreationsPerFrame = 50` — не більше 50 нових нод за кадр.

### Мешинг (NodeJob)

- **Burst IJob** — face extraction (greedy/cubic), як TerraVoxel CPU path
- **Генерація:** FastNoiseLite в job; `IsAirWorldPosition` — відстань від центру планети + noise distortion
- **Mesh:** Mesh.AllocateWritableMeshData → job → Mesh.ApplyAndDisposeWritableMeshData
- **JobCompleter** — schedule у Update, complete у LateUpdate (JobHandle.CompleteAll)

### Потік кадру

```
Update:   Traverse(root) → Schedule(root)
LateUpdate: JobHandle.CompleteAll → onComplete (mesh → MeshFilter)
```

---

## 2. Minecraft — плоский чанковий террейн

### Структура

- **Chunk** — 16×256×16 вокселів, один mesh
- **Dictionary&lt;int3, Chunk&gt;** — фіксована сітка Distance×Distance на старті
- **Frustum culling:** GeometryUtility.TestPlanesAABB перед Draw

### Мешинг (ChunkJob)

- Аналог NodeJob, але плоска висота з noise
- `IsAir`: `worldVoxelPosition.y > height` (height з noise)

### Рендеринг

- `Graphics.DrawMesh(mesh, boundary.min, ...)` — один draw call на чанк
- Mesh генерується lazy при першому Draw (якщо frustum видимий)

---

## 3. Спільні компоненти

| Файл | Призначення |
|------|-------------|
| **Tables** | Offsets для октави (8 дочірніх), Vertices/Normals/BuildOrder для куба, NeighborOffset |
| **MeshingUtility** | ApplyMesh — копіює indices/vertices/normals у Mesh.MeshDataArray |
| **IndexUtilities** | XyzToIndex, IndexToXyz |
| **JobCompleter** | Func&lt;JobHandle&gt; schedule + Action onComplete — відкладений complete |
| **FastNoiseLite** | Noise в Jobs (Burst-сумісний) |

---

## Порівняння з TerraVoxel

| Аспект | TerraVoxel | test-octree-planet-terrain-unity |
|--------|------------|----------------------------------|
| **LOD** | ChunkLodManager (distance, LodStep, SVO) | Octree subdivide/collapse по AABB |
| **Геометрія** | Плоский/колонний террейн | Планета (сфера) або плоский |
| **Мешинг** | GPU (face extraction) або CPU GreedyMesher | CPU Burst (face extraction) |
| **Генерація** | GPU VoxelGeneration.compute | CPU FastNoiseLite в Job |
| **Рендеринг** | DrawProceduralIndirect (GPU) або per-chunk MeshRenderer | Per-chunk Graphics.DrawMesh |
| **Collider** | MeshCollider + async readback | Немає (не реалізовано) |
| **Throttling** | gpuMaxSpawnsPerFrame, maxColliderReadbacksPerFrame | maxNodeCreationsPerFrame |
| **Job complete** | Immediate або в ProcessMeshJobs | LateUpdate, CompleteAll |

---

## Що можна взяти для TerraVoxel

### 1. Octree LOD для планетарного режиму

Якщо TerraVoxel колись підтримає сферичну планету:

- **ShouldSubdivide** — AABB + padding замість чистої відстані
- **maxNodeCreationsPerFrame** — обмеження створення нод за кадр
- **Traverse → Schedule** — окремий прохід для LOD і для мешингу

### 2. JobCompleter-подібний патерн

Відкласти complete до LateUpdate, щоб не блокувати Update. TerraVoxel використовує ProcessMeshJobs у Update — можна розглянути batch complete у LateUpdate для CPU path.

### 3. Tables / MeshingUtility

Статичні таблиці Vertices, Normals, BuildOrder, NeighborOffset — можна порівняти з TerraVoxel.GreedyMesher або compute shader константами.

### 4. Обмеження

- **Без GPU** — весь мешинг на CPU; для великих світів TerraVoxel GPU path ефективніший
- **Без collider** — проект не реалізує колайдери
- **Планета** — специфічна геометрія (відстань від центру); TerraVoxel орієнтований на плоский террейн

---

## Висновок

Проект демонструє **октавне LOD для планетарного воксельного террейну** з CPU Burst jobs. Для TerraVoxel корисні:

- Ідея AABB-based subdivide/collapse
- Throttling створення нод
- Патерн JobCompleter + LateUpdate complete

GPU pipeline TerraVoxel залишається іншим підходом; октавний LOD можна розглянути для майбутнього планетарного режиму або гібридного CPU/GPU варіанту.
