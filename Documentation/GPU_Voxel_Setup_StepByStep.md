# Налаштування GPU-вокселів: покрокова інструкція

Щоб на екрані з’явився террейн, потрібно виконати **одну** з двох схем малювання і переконатися, що камера та чанки в порядку.

---

## Частина 1. Хто малює вокселі

Є два варіанти. Обери **один**.

### Варіант A (простіший): малює сам GpuDrivenRenderer

1. У **Hierarchy** вибери об’єкт, на якому висить **ChunkManager** (або об’єкт, де є компонент **Gpu Driven Renderer**).
2. У **Inspector** знайди компонент **Gpu Driven Renderer (Script)**.
3. Знайди галочку **Draw Via Render Feature**.
4. **Зніми** її (має бути **false**).
5. Збережи сцену (Ctrl+S).

У такому режимі вокселі малює сам цей компонент у `Update`. Нічого більше налаштовувати не потрібно.

---

### Варіант B: малює URP Render Feature

Якщо варіант A не малює (наприклад, помилки DX12) або ти хочеш малювання через URP:

#### Крок 1. Додати feature до рендерера

1. У верхньому меню Unity натисни **Tools**.
2. Обери **TerraVoxel** → **Add Gpu Driven Render Feature to URP Renderer**.
3. У консолі має з’явитися повідомлення на кшталт: *"Added Gpu Driven Render Feature to PC_Renderer"*. Якщо буде попередження, що рендерер не знайдено — перейди до розділу «Якщо рендерер не знайдено» внизу.

#### Крок 2. Призначити GpuDrivenRenderer у feature

1. У **Project** відкрий папку **Assets/Settings** (або там, де лежить твій URP Renderer).
2. Клікни по **рендереру** (наприклад **PC_Renderer** або **Mobile_Renderer**). У Inspector з’явиться його налаштування.
3. У блоці **Renderer List** / **Renderer Features** знайди елемент **Gpu Driven Render Feature** (або **GpuDrivenRenderFeature**).
4. Розгорни його (стрілка зліва).
5. Побачиш поле **Gpu Driven Renderer** (None (Game Object) або пусте).
6. З **Hierarchy** перетягни сюди той самий об’єкт, на якому висить компонент **Gpu Driven Renderer** (той, що використовує ChunkManager). Або натисни кружечок поруч і вибери цей об’єкт у списку.
7. Збережи проект (Ctrl+S).

#### Крок 3. Увімкнути малювання через feature

1. У **Hierarchy** вибери об’єкт з компонентом **Gpu Driven Renderer**.
2. У **Inspector** у компоненті **Gpu Driven Renderer (Script)** постав **галочку Draw Via Render Feature** (true).
3. Збережи сцену.

У такому режимі вокселі малює URP Render Feature, а не сам компонент GpuDrivenRenderer.

---

## Частина 2. Камера

Щоб щось малювалося, малювання викликається для **Camera.main**.

1. У **Hierarchy** знайди об’єкт з **Camera** (наприклад **Main Camera** або камера всередині гравця).
2. Вибери його.
3. У **Inspector** у самому верху об’єкта є поле **Tag**.
4. Постав тег **MainCamera** (саме так, одне слово). Якщо такого тегу немає в списку — створи його в **Edit → Project Settings → Tags and Layers** і признач камері.

Без тегу MainCamera нічого не буде малюватися.

---

## Частина 3. Якщо все ще «Visible: 0» і нічого не видно

Повідомлення *"Visible: 0 but ChunkCount > 0"* означає: чанки є, але у жодного немає геометрії (VertexCount = 0). Cull їх не вважає видимими, тому нічого не малюється.

Що зробити по черзі:

1. **Перезапусти сцену**  
   Зупини Play (Stop), знову натисни Play. Старі GPU-слоти скинуться, чанки згенеруються і відмешаться заново.

2. **Перевір WorldGenConfig**  
   У **Project** знайди **WorldGenConfig** (наприклад **WorldGenConfig.asset**). Вибери його. У Inspector переконайся, що **Default Material Index** не 0 (наприклад 2). 0 = повітря, тоді меш не будується.

3. **Перевір ChunkManager**  
   На об’єкті з ChunkManager у Inspector:  
   - **Use Gpu Pipeline** — увімкнено (галочка).  
   - **Gpu Driven Renderer** — посилання на об’єкт з компонентом GpuDrivenRenderer.  
   - **World Gen** — посилання на твій WorldGenConfig.  
   - **Gpu Max Chunks** — достатньо велике число (наприклад 8192), щоб не впиратися в ліміт одразу після старту.

Після цих кроків знову запусти сцену і подивись консоль: якщо з’явиться щось на кшталт *"First 5 slots descriptor VertexCount: 0=1234, 1=..."* з ненульовими числами — меші є, малювання має з’явитися.

---

## Якщо рендерер не знайдено (варіант B)

Якщо після **Tools → TerraVoxel → Add Gpu Driven Render Feature** у консолі пишуть, що рендерер не знайдено:

1. **Edit → Project Settings → Graphics**.  
   У полі **Scriptable Render Pipeline Settings** має бути призначений твій URP Asset (наприклад **PC_RPAsset**). Якщо пусто — признач URP Asset.
2. Потім знову виконай **Tools → TerraVoxel → Add Gpu Driven Render Feature to URP Renderer**.

Якщо feature все одно не додається:

1. У **Project** відкрий папку з налаштуваннями (наприклад **Assets/Settings**).
2. Вибери **рендерер** (наприклад **PC_Renderer**).
3. У **Inspector** натисни **Add Renderer Feature**.
4. У списку шукай пункт на кшталт **Gpu Driven Render Feature** або **GpuDrivenRenderFeature**. Якщо є — вибери його.
5. Далі знову виконай **Крок 2** і **Крок 3** з варіанта B вище (призначити GpuDrivenRenderer і ввімкнути **Draw Via Render Feature**).

---

## Короткий чеклист

- [ ] Обрано варіант: **A** (Draw Via Render Feature = false) або **B** (feature додано, посилання призначено, Draw Via Render Feature = true).
- [ ] Камера має тег **MainCamera**.
- [ ] ChunkManager: **Use Gpu Pipeline** увімкнено, **Gpu Driven Renderer** і **World Gen** заповнені.
- [ ] WorldGenConfig: **Default Material Index** не 0.
- [ ] Після змін зроблено **перезапуск сцени** (Stop → Play).
