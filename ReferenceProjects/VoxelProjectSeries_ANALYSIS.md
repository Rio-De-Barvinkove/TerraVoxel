# Аналіз VoxelProjectSeries (Part12-TheEnd--Kinda)

Порівняння з TerraVoxel для покращення GPU-пайплайну.

---

## Архітектура VoxelProjectSeries

### 1. Pipeline генерації

```
Density (GPU) → AsyncGPUReadback(heightMap) → ProcessNoiseForStructs
    → Contour 3 kernels (GPU) → AsyncGPUReadback(transparentIndexBuffer) → UploadMesh
```

**Ключ:** AsyncGPUReadback — CPU не блокується під час генерації. Callback виконується пізніше (наступний кадр або коли GPU завершить).

### 2. Що відбувається в callback

**ProcessNoiseForStructs:**
- `noiseBuffer.GetData` — sync readback вокселів для структур/модів
- `countBuffer.GetData` — sync readback
- `specialBlocksBuffer.GetData` — sync readback
- Модифікації вокселів → `noiseBuffer.SetData` — перезапис
- Потім `Contour` (3 GPU dispatch)

**UploadMesh (callback після Contour):**
- `countBuffer.GetData` — sync readback face count
- `vertexBuffer.GetData`, `indexBuffer.GetData`, `colorBuffer.GetData`, `normalBuffer.GetData` — sync readback всього меша
- Створення Mesh, призначення MeshFilter.sharedMesh, MeshCollider.sharedMesh

**Важливо:** Callback виконується після того, як GPU завершив. GetData у callback все одно sync — але це вже після генерації, не блокуючи основний кадр. Unity викликає callback коли дані готові.

### 3. Рендеринг

- **Per-chunk mesh:** MeshFilter + MeshRenderer на кожен чанк
- **Багато draw calls** — один на чанк (або batch через SRP batching)
- **MeshCollider** — той самий mesh, без readback — меш вже на CPU

### 4. Параметри

| Параметр | Значення |
|----------|----------|
| chunkSize | 16 |
| maxHeight | 128 |
| renderDistance | 32 (≈32×32 chunks ≈1024) |
| maxChunksToProcessPerFrame | 2 (config) |

### 5. Оптимізації

- **Buffer pooling** — GenerationBuffer, MeshData перевикористовуються
- **Chunk pooling** — чанки повертаються в пул
- **Окремий thread** — CheckActiveChunksLoop (500ms) для визначення чанків на завантаження/вивантаження
- **maxChunksToProcessPerFrame** — обмеження навантаження на кадр
- **AsyncGPUReadback** — відсутність CPU stall під час генерації
- **Marching cubes** — VoxelContour використовує surface nets / contour (а не naive face extraction)

---

## Порівняння з TerraVoxel

| Аспект | TerraVoxel | VoxelProjectSeries |
|--------|------------|---------------------|
| Рендеринг | DrawProceduralIndirect (1 виклик) | Per-chunk MeshRenderer (багато викликів) |
| Mesh на CPU | Ні (для колайдерів — readback) | Так (все readback) |
| Readback | Sync (FaceCounter, mesh для collider) | AsyncGPUReadback, потім sync у callback |
| Collider | MeshCollider + readback, або BoxCollider | MeshCollider з того ж mesh |
| Генерація | GPU (VoxelGeneration.compute) | GPU (HeightMapDensity) |
| Мешинг | GPU (VoxelMeshing — face extraction) | GPU (VoxelContour — marching cubes) |
| Аналіз чанків | GpuChunkAnalyzer (empty/solid/mixed) | Немає |
| Culling | GpuCuller (frustum, occlusion) | Unity culling (per-mesh) |

---

## Що можна взяти з VoxelProjectSeries

### 1. AsyncGPUReadback замість sync

```
Спочатку: Gen → MeshChunk → GetData(faceCount) [STALL] → collider
Після:   Gen → MeshChunk → AsyncGPUReadback.Request(faceCount) 
          → callback: UpdateDescriptor + CreateColliderMesh
```

Callback виконується пізніше — CPU не блокується. Collider з’явиться через 1–2 кадри.

### 2. AsyncGPUReadback для mesh collider

Замість sync CreateColliderMeshFromGpu (readback MeshVertexBuffer):

```csharp
AsyncGPUReadback.Request(state.MeshVertexBuffer, vertexCount, meshVertexOffset, 
    (req) => {
        if (req.hasError) return;
        var verts = req.GetData<Vector3>();
        // build mesh, apply to chunk
    });
```

### 3. Обмеження workload на кадр

VoxelProjectSeries: `maxChunksToProcessPerFrame` (2–3) — не спавнять усі чанки одразу.

TerraVoxel: `maxSpawnsPerFrame` є, але можливо треба зменшити для GPU path або розділити gen/mesh/collider на окремі кадри.

### 4. Buffer pooling

VoxelProjectSeries пулить GenerationBuffer. TerraVoxel має GpuWorldState з фіксованими буферами — інший підхід. Але можна пулити тимчасові масиви для readback (Vector3[], int[]).

### 5. Чанки: один mesh vs instanced mesh

- **VoxelProjectSeries:** один mesh на чанк — більше draw calls, але MeshCollider без додаткового readback.
- **TerraVoxel:** один великий буфер, DrawProceduralIndirect — менше draw calls, але MeshCollider потребує readback.

Треба вибирати: чи важливіше менше draw calls, чи відсутність readback для колайдерів.

---

## Висновки

1. **AsyncGPUReadback** — головна причина відсутності лагів: CPU не чекає на GPU.
2. **Mesh на CPU** — MeshCollider працює без додаткового readback.
3. **Обмеження per frame** — не перевантажувати кадр.
4. **DrawProceduralIndirect vs per-chunk:** TerraVoxel має більш ефективний рендеринг через один draw call, але потребує readback для колайдерів. VoxelProjectSeries платить за mesh readback, але все робить один раз — і mesh, і collider.

### Рекомендації для TerraVoxel

- **Пріоритет 1:** AsyncGPUReadback для FaceCounter і mesh collider — замінити sync readback.
- **Пріоритет 2:** Обмежити readback на кадр (наприклад, 1 collider mesh на кадр).
- **Пріоритет 3:** Оцінити DrawProceduralIndirect vs per-chunk mesh — якщо FPS все ще низький, варто зняти профіль і порівняти.

---

## Структура файлів VoxelProjectSeries

- `GenerationManager.cs` — оркестрація, AsyncGPUReadback
- `Chunk.cs` — ProcessNoiseForStructs, UploadMesh
- `World.cs` — Tick, Update, chunk queue
- `InfiniteTerrain.cs` — ExecuteDensityStage, CheckActiveChunksLoop
- `VoxelContour.compute` — CalculateVertices, SumNormals, GenerateFaces (marching cubes)
- `HeightMapDensity.compute` — density generation

---

# Аналіз шейдерів і compute shaders

## 1. VoxelProjectSeries — генерація (HeightMapDensity)

### Підхід: двоступенева density

**Kernel 0 — GenHeightMap** (8×1×8):
- Генерує heightmap з кроком 4×4 (downsample)
- `fractalNoise` — 4 octaves, BCC8 noise, biome weights
- `getDensityAtPoint` — біоми, відстань, weighted blend
- Результат: `float2(trueHeight, biomeIndex)` на кожну клітинку 4×4

**Kernel 1 — FillArray** (8×8×8):
- Читає heightmap (інтерполяція з 4×4)
- `getDensityAtPoint` для кожного вокселя — surface/subsurface, water (240)
- **Sub-voxel density** — 4×4×4 біти на воксель (densityData, densityDataB) для smooth surface
- **Foliage** — SimplexNoise3D для трав/дерев, InterlockedAdd для specialBlocksBuffer
- **Caves** — `fractalNoise` cave noise > 0.75

### Структура Voxel (Voxel.cginc)

```hlsl
struct Voxel {
    int voxelData;      // id (8 bit), activeValue (8 bit)
    uint densityData;   // 32 bits sub-voxel density
    uint densityDataB;  // ще 32 bits
};
```

64 sub-voxels на воксель (4×4×4) — для smooth/rounded terrain замість блокових кутів.

### Noise (WorldStructs)

- **BCC8** — BCC lattice, gradient noise, повертає float4(dF/dx, dF/dy, dF/dz, value)
- **Simplex 2D/3D** — webgl-noise
- **ClassicNoise** — Perlin-style
- **Voronoi** — cellular
- **cnoise** — ClassicNoise3D

---

## 2. VoxelProjectSeries — мешинг (VoxelContour)

### Marching cubes–style surface extraction

**Kernel 0 — CalculateVertices** (8×8×8):
- Шукає zero-crossing між сусідніми вокселями (`AppproximateZeroCrossing`)
- `getVoxelDensity` для sub-voxel — 4×4×4 біти
- `cellVertices` — позиція на surface (vertex)

**Kernel 1 — SumNormals** (8×8×8):
- Накопичує нормалі для кожного cellVertex (shared vertices)
- Cross product для кожної грані, додає до сусідніх вершин

**Kernel 2 — GenerateFaces** (8×8×8):
- Для кожного solid вокселя — 6 граней
- Перевірка `isOpaque()`, `isTransparent()` для water
- `InterlockedAdd(count[2], 6)` — vertex count
- `InterlockedAdd(count[3/4], 6)` — index count (opaque/transparent)
- Записує в vertexBuffer, normalBuffer, colorBuffer, indexBuffer, transparentIndexBuffer

### Відмінність від TerraVoxel

| Аспект | TerraVoxel (VoxelMeshing) | VoxelProjectSeries (VoxelContour) |
|--------|---------------------------|-----------------------------------|
| Мешинг | Naive face extraction (кубічні грані) | Marching cubes (smooth vertices) |
| Sub-voxel | Ні | Так (4×4×4 density) |
| Вершини | Кожен cube face = 6 vertices | Shared vertices, smooth |
| Окремі буфери | Один MeshVertexBuffer | vertexBuffer, normalBuffer, colorBuffer, indexBuffer |
| Transparency | Ні | Так (transparentIndexBuffer, submesh 2) |

---

## 3. TerraVoxel — поточний стан

### VoxelGeneration.compute

- **Фіксований noise** — value noise 2D, 3 octaves (0.5, 0.25 weights)
- **Немає NoiseStack** — CPU має Layers (Perlin, Simplex, Voronoi), octaves, persistence; GPU — ні
- **Простий heightmap** — ` BaseHeight + SampleNoise * HeightScale`

### VoxelMeshing.compute

- **Naive face extraction** — для кожного вокселя перевіряє 6 сусідів, якщо порожній — face
- **InterlockedAdd** для FaceCounter
- **Кубічні вершини** — GetFaceCorner повертає кубичні кути

### VoxelTriplanarURP_Instanced

- **Triplanar** — Texture2DArray, UV по позиції world
- **Один layer** — `_LayerIndex` для всіх вокселів (матеріал однаковий)
- **Немає per-vertex color** — vertex color не використовується для texture layer

---

## 4. Що можна винести на GPU (рекомендації)

### A. NoiseStack на GPU

**Проблема:** GpuChunkGenerator не передає NoiseStack. GPU має фіксований 3-octave value noise.

**Рішення:** Додати в VoxelGeneration.compute підтримку NoiseStack:
- StructuredBuffer<NoiseLayer> (Type, Scale, Octaves, Persistence, Lacunarity, Weight)
- Port Simplex noise (наприклад, BCC8 або snoise з keijiro/NoiseShader)
- Цикл по layers у SampleNoise

**Складність:** Середня. Потрібні HLSL-версії Noise.

### B. Покращений noise

**VoxelProjectSeries** використовує BCC8 (gradient noise, smooth). TerraVoxel — value noise (hash-based).

**Рішення:** Замінити або додати Simplex/BCC8 в VoxelGeneration.compute. Бібліотека: https://github.com/keijiro/NoiseShader (BCCNoise8.hlsl, SimplexNoise3D.hlsl).

### C. Heightmap pre-pass (опційно)

**VoxelProjectSeries:** Окремий kernel GenHeightMap (4×4 downsample) — один прохід на low-res, потім FillArray читає його.

**TerraVoxel:** GenerateChunk робить все за один прохід — кожен воксель викликає SampleNoise.

**Рішення:** Для великих чанків — можна додати heightmap kernel (4×4 або 8×8), якщо noise є bottleneck. Неочевидно для chunkSize 32.

### D. Per-vertex material / texture layer

**VoxelProjectSeries:** colorBuffer передає blockID — shader вибирає texture layer з blockID.

**TerraVoxel:** _LayerIndex — один для всього. Mesh vertex не має material ID.

**Рішення:** Pack material в vertex (наприклад, нормаль.w або окремий color) — фрагмент шейдера читає layer з vertex. Потрібно змінити VoxelMeshing (GenerateVertices) і VoxelTriplanarURP_Instanced.

### E. Smooth normals (marching cubes) — SKIP

**VoxelProjectSeries:** Smooth vertices на surface через sub-voxel density; shared vertices.

**TerraVoxel:** Кубічні грані — flat shading.

**Рішення:** Великий рефактор — sub-voxel density, marching cubes. **Не потрібно** — кубічний стиль залишається.

---

## 5. Пріоритети

| Зміна | Impact | Складність | Рекомендація |
|-------|--------|------------|--------------|
| NoiseStack на GPU | Висока (parity з CPU) | Середня | Так |
| BCC8/Simplex noise | Середня (якість terrain) | Низька | Так |
| Per-vertex material | Висока (multi-material) | Середня | Після |
| Heightmap pre-pass | Низька | Низька | За потреби |
| Marching cubes | — | — | **Skip** |
