# Burst для движка — E8 `BurstBootstrap`

Модуль `BurstBootstrapModule` (`Engine\Modules\BurstBootstrapModule.cs`, переключатель `BurstBootstrap`, по умолчанию
on) даёт джобам Real Portal Burst-код: подгружает нашу нативную библиотеку рядом с Burst-кодом самой Valheim, но только
когда джоб впервые о ней спросит (`Ensure()`, `ScheduleCull`, `LabCullBench`). Это E8 из `Docs\ARCHITECTURE-v2.md`
§5.2: нужен S5 (cull прокси) и S11 (все джобы); без него джобы идут управляемым кодом. Пока никто не спросил (до S5 —
никто, кроме лаборатории), модуль при старте только привязывает свой переключатель к конфигу: ни одного файла не
читает и ничего не грузит.

Отличие от контракта E8 («`LoadAdditionalLibrary` в Awake»): загрузка отложена с Awake на первый запрос. В Valheim
Awake движка выполняется раньше, чем игра регистрирует свои джобы и функции Burst/Collections (см. «Почему не в Awake»),
так что ранняя загрузка подменяла бы их код нашим.

В самой игре ещё не запускалось. Проверено: сборка пары файлов в Unity (batchmode, копия проекта во временной папке:
0 ошибок и 0 предупреждений, самопроверка в редакторе и в собранном плеере), и модуль — тот же исходник, собранный
`dotnet` как настоящий движок, — в отдельном тестовом плеере Unity 6000.0.75 (с Burst и Collections, как Valheim, и без
`RealPortal.Burst`) в 20 сценариях (см. «Проверка»). Это закрывает P6 из §8 вне игры; в игре его нужно подтвердить
одной строкой лога после `Ensure()`.

## Файлы

| Где (`RP` = `<repo>`) | Что |
|---|---|
| `RP\Unity\Packages\manifest.json` | `com.unity.burst` 1.8.29 и `com.unity.mathematics` 1.3.2; `com.unity.collections` **нет** (и не должно быть: см. «Правила») |
| `RP\Unity\Assets\RealPortal.Burst\RealPortal.Burst.asmdef` | сборка `RealPortal.Burst` (ссылки: `Unity.Burst`, `Unity.Mathematics`) |
| `…\RealPortal.Burst\BurstJobs.cs` | то, что зовёт движок: `Probe`, `ExpectedLayout`, `ProbeJobs`, `JobNames`, `ScheduleCull`, `ApiVersion` (= 2) |
| `…\RealPortal.Burst\ProbeJob.cs` | проба: Burst или управляемый код, и с какой раскладкой джобов скомпилирован нативный код |
| `…\RealPortal.Burst\ProxyCullJob.cs` | `ProxyCullJob` (`IJobFor` по батчам, сферы × 6 плоскостей) и `ProxyCompactJob` (`IJob`, упаковка) |
| `…\RealPortal.Burst\SelfTest\` | сборка `RealPortal.Burst.SelfTest` (`BurstSelfTest.cs`): проверки, которые сборка библиотеки делает в редакторе и в собранном плеере; не поставляется |
| `…\RealPortal.Burst\BurstBuild.unity` | пустая сцена для сборки плеера (скрипт создаёт её сам, если её нет) |
| `RP\Unity\Assets\RealPortal\Editor\BurstLibraryBuild.cs` | сборка: самопроверка, плеер, проверка в плеере, копирование, манифест |
| `RP\Engine\Modules\BurstBootstrapModule.cs` | загрузка в игре, проверки, API, управляемые двойники джобов |
| `RP\Build\RealPortal.Burst.dll` | управляемая сборка из плеера (`*_Data\Managed`), 9216 байт, SHA-256 `fe909cf9…` |
| `RP\Build\RealPortal.Burst_win_x86_64.dll` | её Burst-код: `lib_burst_generated.dll` того же плеера (`*_Data\Plugins\x86_64`), 10752 байта, SHA-256 `25d94dfc…` |
| `RP\Build\RealPortal.Burst.json` | версии, размеры `Unity.Burst.dll`/`UnityEngine.CoreModule.dll` плеера, размеры и SHA-256 обоих файлов; **обязателен**. Сейчас: `apiVersion` 2, `built` 2026-10-24T08:26:45Z, все четыре поля размеров |
| `RP\Unity\BurstBuild\` | плеер, из которого взята эта пара, с `selftest.txt`/`selftest.log` его самопроверки (временная папка сборки) |

Пара в `RP\Build` — из текущих исходников (ApiVersion 2, `IJobFor`, без Collections, `Distance` с приведениями к
float): собрана `BurstLibraryBuild.Build` в копии `RP\Unity` во временной папке (сборка только там; из другой папки
байты другие, см. «Сборка»), все три файла скопированы вместе, плеер — в `RP\Unity\BurstBuild`. Пара от прежних
исходников (ApiVersion 1, с Collections, 8704/56320 байт) движком отвергается до загрузки: «the pair is ApiVersion 1,
this engine calls 2» — до замены 2026-10-24 в `RP\Build` лежала именно она, и прогоны стенда (`RP_LAB_ROOT` → этот
путь) Burst не включали (P6 записался бы как managed). Пересобирать — всегда все три файла вместе.

Код в `RealPortal.Burst` самодостаточный: только CoreModule (`NativeArray`, `IJob`, `IJobFor`, атрибуты
`[ReadOnly]`/`[NativeDisableParallelForRestriction]` — они тоже в CoreModule), Burst и Mathematics — всё это игра
поставляет сама. Ни типов Valheim, ни пакета Collections. Сборка ссылается на `netstandard` (уровень API проекта —
.NET Standard 2.1), `netstandard.dll` в `valheim_Data\Managed` есть, как и для самого движка.

## Версии пакетов

Игра собрана на Unity 6000.0.75 (`UnityPlayer.dll` 6000.0.75.2503836 — тот же, что у редактора). У её
`Unity.Burst.dll` и `Unity.Mathematics.dll` версия сборки 0.0.0.0 и строк с версией нет, поэтому версии определены
сравнением:

- **Декомпиляция совпадает целиком.** `ilspycmd -p` по dll игры и по dll из нашего плеера (burst 1.8.29, mathematics
  1.3.2) даёт одинаковые файлы, отличается только целевой фреймворк в сгенерированном `.csproj`. Размеры dll совпадают
  байт в байт (`Unity.Burst.dll` 304640, `Unity.Mathematics.dll` 752640, `UnityEngine.CoreModule.dll` 1801128);
  содержимое различается только MVID/метаданными сборки. Эти два размера (`unityBurstBytes`, `coreModuleBytes`) пишутся
  в манифест и сверяются в игре.
- **Нативный код:** у нашей библиотеки 6 точек входа: наши `ProbeJob`, `ProxyCullJob` (`IJobForExtensions.ForJobStruct`),
  `ProxyCompactJob` и три служебные функции самого Unity.Burst, которые есть в любой Burst-сборке —
  `BurstCompilerHelper.IsBurstEnabled`, `X86.DoSetCSRTrampoline`, `X86.DoGetCSRTrampoline`. Эти три есть и в
  `lib_burst_generated.dll` игры под теми же хэшами; больше пересечений нет (раньше, с Collections в сборке, их было 27:
  джобы освобождения контейнеров, xxHash3, аллокаторы).
- Редактор 6000.0.75 сам несёт ровно эти версии (`Data\Resources\PackageManager\Editor`), сеть не нужна.

На уже существующую сборку `realportal.bundle` пакеты не влияют (бандл не содержит скриптов; редакторская сборка с
`RealPortalBuild.cs` и `RealPortalTests.cs` компилируется вместе с новой без ошибок и предупреждений).

## Сборка

```
"C:\Program Files\Unity\Hub\Editor\6000.0.75f1\Editor\Unity.exe" -batchmode -quit ^
  -projectPath <repo>\Unity ^
  -executeMethod RealPortal.EditorTools.BurstLibraryBuild.Build -logFile <лог>
```

Или меню `Real Portal → Build Burst library`. Код возврата 0 — успех, в логе строки `[BurstLibraryBuild] …` и
`RESULT: ok`. Первый запуск с импортом пакетов — ~1 мин, дальше 20–40 с. Игру не трогает. Байт в байт сборка не
воспроизводится: управляемая dll из той же папки проекта выходит та же, но нативная несёт время линковки в заголовке PE
(`TimeDateStamp`), и пересборка, которая заново компилирует Burst-код, даёт другой SHA-256 (две сборки подряд
2026-10-24 разошлись ровно этим полем; совпадают хэши, только если Burst взял библиотеку из своего кэша). Из другой
папки отличается и управляемая (в сборки попадают пути исходников). Поэтому SHA-256 сверяются с манифестом той же
сборки, а числа в этом файле описывают только пару, которая сейчас лежит в `RP\Build`.

Что делает `BurstLibraryBuild.Build()`:
1. Пишет в лог версии Unity и пакетов (`com.unity.collections missing (must be missing)`) и настройки Burst AOT (файла
   настроек нет — значения по умолчанию: x64 SSE2 + AVX2).
2. Самопроверка в редакторе (`BurstSelfTest.Run`, Burst JIT): Burst-компиляция включена (`BurstCompiler.IsEnabled`),
   проба прошла под Burst, раскладка совпала, каждый джоб из `JobNames` прошёл под Burst так, как его ставит
   `ScheduleCull` (`ProbeJobs`), cull на 21 случайном наборе (0…20000 сфер, батчи 1/7/64) равен обычному циклу на C#
   (расстояние с приведениями к float, как в джобе) и `Plane.GetDistanceToPoint` Unity, а на граничных сферах —
   ответу, известному заранее: касательные к плоскости, на шаг float меньше и больше, и две сферы, где округление
   до float и счёт в double дают разный ответ (батчи 1/7/64; эталон на C# обязан дать тот же ответ — так проверяются
   его приведения). Строка `boundary: the uncast C# expression keeps …` показывает, что этот рантайм делает с
   выражением без приведений. Любой провал — сборка падает. С выключенным Burst в редакторе (Jobs → Burst →
   Enable Compilation или `--burst-disable-compilation`) сборка падает сразу: управляемый код с управляемым она больше не
   сравнивает.
3. Если сцены `BurstBuild.unity` нет — создаёт её; в редакторе сначала предлагает сохранить открытые сцены (отмена
   останавливает сборку) и потом открывает их снова.
4. Плеер Windows x64, Mono, не development, пустая сцена → `RP\Unity\BurstBuild\RealPortalBurst.exe`. Если проект
   настроен иначе — на время сборки Mono и выключенный stripping, потом настройки возвращаются (если уже так, а сейчас
   так и есть, — `ProjectSettings.asset` не трогается). Если в плеере оказался `Unity.Collections.dll` — сборка падает.
5. Та же самопроверка в собранном плеере: `RealPortalBurst.exe -batchmode -nographics -realportal-burst-selftest
   <файл>` (до 120 с). Так проверяется именно поставляемый AOT-код (SSE2/AVX2, без проверок безопасности), а не только JIT
   редактора. Результат и лог плеера — `RP\Unity\BurstBuild\selftest.txt`, `selftest.log`.
6. Копирует `RealPortal.Burst.dll` и `lib_burst_generated.dll` → `RP\Build\RealPortal.Burst.dll` и
   `RP\Build\RealPortal.Burst_win_x86_64.dll`, пишет `RP\Build\RealPortal.Burst.json`, логирует размеры и SHA-256.
   Если игра держит файлы (запущена с ними), копирование падает с понятной ошибкой.

`RP\Unity\BurstBuild\` — временная папка сборки (можно удалять), `Unity_BurstDebugInformation_DoNotShip` в ней — не
поставлять. Путь проекта не должен быть слишком длинным: Burst AOT пишет файлы длиной ~150 символов поверх пути
проекта и падает за пределом Windows в 260 символов («Burst internal compiler error: DirectoryNotFoundException»).

## Установка

- Игроку: `RealPortal.Burst.dll`, `RealPortal.Burst_win_x86_64.dll` и `RealPortal.Burst.json` — в ту же папку, что
  `RealPortalEngine.dll`. Все три — из одной сборки; без JSON Burst не включится. BepInEx пропускает обе dll (в первой
  нет плагинов, вторая не .NET — в логе BepInEx будет отладочная строка о пропуске).
- Разработка и лаборатория: модуль ищет файлы по порядку: рядом с плагином → в папке из переменной окружения
  `REALPORTAL_BURST_DIR` (относительный путь приводится к полному) → в `<repo>\Build`, но
  только в прогонах стенда (задана переменная `RP_LAB_ROOT`, её ставит `Bench\bench.ps1`; у игрока этот путь не
  проверяется вовсе). Берётся первая папка, где есть обе dll.
- Стенд кладёт в прогон только `RealPortalEngine.dll` и пишет в `run.json` только её SHA-256. Какая пара Burst была в
  прогоне, видно по строкам `BurstBootstrap:` в логе (время сборки и SHA-256 обоих файлов). Чтобы прогоны не зависели
  от того, что сейчас лежит в `RP\Build` (и не держали эти файлы открытыми, пока идёт пересборка), стенду стоит
  копировать тройку файлов в прогон или задавать `REALPORTAL_BURST_DIR` — это правка `bench.ps1`, не модуля.

## Как модуль включает Burst

Модуль ничего в игре не патчит (Harmony не используется). Всё на главном потоке:
1. `Install` (внутри `EnginePlugin.Awake`) только привязывает свой переключатель к конфигу движка (плагин привязывает
   остальные после всех `Install`; `Feature.Bind` второй раз ничего не делает), чтобы `Ensure()` откуда угодно уже
   видел настройку игрока. Конфиг берётся у компонента `EnginePlugin` на `Chainloader.ManagerObject`. Больше при старте
   ничего не происходит.
2. Активация — один раз за сессию, при первом `Ensure()`, `ScheduleCull` или `LabCullBench`, если переключатель on.
   Сначала все проверки, которые могут отвергнуть пару, — до загрузки чего-либо:
   - поиск пары файлов (см. «Установка»);
   - `RealPortal.Burst.json` обязателен: `apiVersion` = `BurstBootstrapModule.ApiVersion`; `unity` =
     `Application.unityVersion` игры; `unityBurstBytes` и `coreModuleBytes` = размерам `Unity.Burst.dll` и
     `UnityEngine.CoreModule.dll` игры (пакет Burst и продюсеры джобов, под которые скомпилирован наш нативный код);
     размеры и SHA-256 обоих файлов = записанным (JSON читается через `IndexOf`, хэш — `SHA256Managed`);
   - `RealPortal.Burst` ещё не загружена из другого файла (иначе её код — не тот файл, за который ручается манифест);
   - `Unity.Burst.BurstCompiler.IsEnabled` (рефлексией): если Burst в плеере выключен (`--burst-disable-compilation`) —
     дальше не идём.
3. `Unity.Burst.BurstRuntime.LoadAdditionalLibrary(путь к RealPortal.Burst_win_x86_64.dll)` рефлексией (ссылки на
   `Unity.Burst` в проекте движка нет; метод есть в `Unity.Burst.dll` игры и сам проверяет
   `BurstCompiler.IsLoadAdditionalLibrarySupported()`, иначе возвращает false). Библиотека грузится до сборки и до
   первого использования джобов, как требует документация Burst («Modding support»).
4. `Assembly.LoadFrom(RealPortal.Burst.dll)`.
5. Инициализаторы `[RuntimeInitializeOnLoadMethod]` сборки вызываются рефлексией (Unity выполняет их только для сборок
   из `RuntimeInitializeOnLoads.json` плеера). Без Collections в проекте кодогенерации регистрации джобов нет, и у
   сборки их 0; данные джоба создаются при первом `Run`/`Schedule` (`IJobExtensions`/`IJobForExtensions.GetReflectionData<T>`
   CoreModule сами зовут `Initialize()`) — после шага 3, так что Burst находит наш нативный код.
6. `BurstJobs.ApiVersion` сборки должен совпасть с `BurstBootstrapModule.ApiVersion`.
7. `ProbeJob` через `Run()` на главном потоке. Под Burst вызов `[BurstDiscard]` вырезан и `Result[0] = 1`;
   управляемым кодом — 2. Ещё проба пишет `ApiVersion` и размеры `ProxyCullJob`/`ProxyCompactJob` такими, какими их
   видел компилятор нативного кода, а модуль сравнивает их с `BurstJobs.ExpectedLayout()` управляемой сборки.
8. Только после совпадения раскладки — `BurstJobs.ProbeJobs()`: настоящие джобы так, как их ставит `ScheduleCull`, на
   рабочих потоках: `ProxyCullJob.ScheduleParallel` на 16 индексов и `ProxyCompactJob.Schedule`, с `BatchSize = 0`
   (режим пробы: каждый индекс пишет 1 под Burst, 2 управляемым кодом). Так проверяется не только `IJob` в `Run`, но и
   продюсер `IJobFor` и точка входа каждого джоба: если её нет в библиотеке или продюсер игры с ней больше не
   совпадает, джоб молча пошёл бы управляемым кодом, а модуль считал бы его Burst.
9. Известный ответ (`KnownAnswer`): Burst-cull пары и управляемые двойники движка режут одни и те же фиксированные
   наборы, каждый батчами 1, 7 и 64, и должны вернуть одинаковые индексы и их число. Наборы: фиксированный фрустум с
   201 случайной сферой (seed) и 100 центрами вне ровно одной его плоскости, у каждого три сферы — радиус равен
   расстоянию до плоскости (касается, видима), на шаг float меньше (отсечена) и больше (видима), так что решает само
   сравнение; и набор плоскостей, где расстояние, округлённое до float, и посчитанное в double дают противоположный
   ответ. 6 cull-ов, 510 сфер; доля в активации — 4–11 мс (почти всё — первый запуск управляемых двойников).
10. Burst используется (`BurstActive = true`), только если проба прошла под Burst, раскладка совпала, каждый джоб
   прошёл под Burst и известный ответ совпал. Иначе предупреждение в лог с причиной, и всё работает на управляемых
   двойниках.

Зачем проверка раскладки, манифест и известный ответ: Burst находит нативный код джоба по строке сигнатуры (имя типа,
`Version=0.0.0.0`), а не по полям и не по телу. Нативная библиотека от другой сборки джобов читала бы поля не по тем
смещениям — порча памяти; а библиотека от другой Unity — работала бы со структурами (`JobRanges`) другой раскладки.
Манифест с хэшами показывает только, что два файла — из одной сборки и под эту Unity/Burst (до загрузки); с
двойниками движка и с текущими исходниками он пару не связывает. Проба ловит изменение размеров и `ApiVersion`,
`ProbeJobs` — джоб без Burst-кода, известный ответ — тело джоба, изменённое при тех же полях только с одной стороны
(в паре или в двойнике): иначе переключатель менял бы, какие прокси видны. Известный ответ ловит то, что меняет
ответ на его наборах (лишний тест, `<=` вместо `<`, другое округление), а не любую правку тела. Раскладка в плеере (и
в игре) — `2/72/56`; в редакторе — `2/200/152` (там у `NativeArray` есть поля проверок безопасности), это нормально:
сравниваются числа одного и того же рантайма.

### Почему не в Awake

BepInEx 5 в Valheim стартует из статического конструктора `GameObject` (`BepInEx.cfg`: `Entrypoint Type = GameObject`,
`Method = .cctor`), а игра нарочно вызывает его в `PlatformInitializer.EarlyInitialize` (`GameObject.Find("")`) — это
метод фазы `AfterAssembliesLoaded`, запись 2 в `valheim_Data\RuntimeInitializeOnLoads.json`. Записи той же фазы после
неё — регистрация джобов MagicaCloth (6), `$BurstDirectCallInitializer` Unity.Burst (12), регистрация джобов
Collections (13), `$BurstDirectCallInitializer` Collections (14); порядок внутри фазы Unity к тому же не обещает. Значит,
`EnginePlugin.Awake` идёт раньше, чем игра привязывает свои джобы и функции к своей библиотеке, и загрузка в Awake
отдала бы игре код из нашей библиотеки там, где хэши совпадают. Отложенная активация идёт после всего этого (первый
`Ensure()` — не раньше загрузки мира или лаборатории), а пересечение библиотек теперь — только три служебные функции
Unity.Burst, которые игра привязала при старте.

В логе при активации: строка с полями `RealPortal.Burst.json` (версии, `ApiVersion`, время сборки, оба SHA-256) и
версией Unity игры, затем
`BurstBootstrap: active (Burst code runs); switch on, files …, built …, sha256 <16 знаков> / <16 знаков>,
BurstCompiler.IsEnabled True, library loaded, assembly loaded, initialisers 0, probe Burst, layout 2/72/56 vs 2/72/56,
jobs ProxyCullJob Burst, ProxyCompactJob Burst, known answer equal (6 culls, 510 spheres, … ms), … ms` или
`Burst not used, jobs run managed. inactive (<причина>) …`.

## Переключатель `BurstBootstrap` и гарантии

- **Off в конфиге:** ничего не грузится и не выполняется, патчей у модуля нет вообще — игра ровно как без него.
- **On, но никто не спросил** (`Ensure`/`ScheduleCull`/`LabCullBench` не вызывались — сейчас так в любой сессии без
  лаборатории): то же самое — файлы не ищутся и не читаются, ничего не загружено. Стоимость — только привязка
  переключателя в `Install`.
- **On, после активации:** модуль ничего в игре не патчит, наш код работает только там, где его зовут
  (`ScheduleCull`). Общее с игрой — загруженная вторая нативная библиотека с тремя служебными функциями Unity.Burst под
  теми же хэшами, что и у игры (их игра привязала к своей библиотеке при старте). Отвергнутая после загрузки пара (проба,
  раскладка, `ProbeJobs`, известный ответ) остаётся загруженной до конца сессии, но не используется. В тестовом плеере
  после загрузки `BurstCompiler.IsEnabled` плеера остаётся true.
- **On → off в игре:** `BurstActive = false`, новые `ScheduleCull` идут управляемым двойником; уже поставленные
  джобы доделываются как были. Библиотеку и сборку выгрузить нельзя — они остаются, но не используются.
- **Off → on в игре:** активации сразу нет; она произойдёт при следующем `Ensure()`/`ScheduleCull` (поздняя загрузка
  безопасна: наши Burst-джобы до этого не инициализировались, двойник — другой тип). Стоимость — разовые ~40–60 мс
  (тестовый плеер; известный ответ из них — 4–11 мс), поэтому для A/B переключать лучше после первой активации.
- **Burst и двойник дают одно и то же:** одна и та же последовательность операций, и каждая округляется до float: Burst
  в `FloatMode.Strict` (без перестановок и без FMA) считает во float сам, а двойник приводит к `float` каждый шаг
  `Distance` (`conv.r4`). Без приведений Mono Unity (и в редакторе, и в плеере — тот же, что у игры) считает это
  выражение в double, и сфера в пределах шага float от плоскости отсекалась бы одним путём и не другим: самопроверка
  сборки это показывает (`the uncast C# expression keeps 1,2,4,5,7,8`, Burst и приведения — `0,2,3,5,6,8`).
  Округлять точный double-результат до float — то же, что считать во float (53 ≥ 2·24 + 2). Порядок вывода один
  (индексы по возрастанию). Активация отказывает паре, чей cull отвечает на известные наборы не так, как двойник. В
  тестовом плеере на 0…100000 сфер и батчах от 2 до `int.MaxValue` индексы совпали во всех прогонах.
- Сетевые сообщения, ZDO, ревизии, владение — не затрагиваются. С Real Portal и Path of Valheiman не пересекается
  (нет общих методов; среди установленных модов ни один не грузит свой Burst-код).

## API (для FarWorld и других модулей)

Всё — статические члены `RealPortal.Engine.BurstBootstrapModule`, главный поток.

| Член | Что |
|---|---|
| `bool Ensure()` | активирует, если ещё не было и переключатель on; возвращает `BurstActive`. Звать один раз в месте загрузки (загрузка мира, включение FarWorld): активация стоит ~40–60 мс |
| `bool BurstActive` | Burst-код можно использовать (переключатель on и активация прошла все проверки); читать на горячем пути. До первого `Ensure()` — false |
| `JobHandle ScheduleCull(spheres, planes, visible, batchCounts, visibleCount, batchSize = 64, dependsOn = default)` | cull сфер (xyz — центр, w — радиус) шестью плоскостями: Burst при `BurstActive`, иначе двойник. Сам вызывает `JobHandle.ScheduleBatchedJobs()`, так что джобы сразу уходят рабочим потокам и идут, пока вызывающий занят своим; если активации ещё не было — делает её (этот кадр платит её цену) |
| `int BatchCount(length, batchSize)` | длина `batchCounts` (без переполнения при любом `batchSize > 0`) |
| `void WritePlanes(Plane[] six, NativeArray<float4> into)` | плоскости из `GeometryUtility.CalculateFrustumPlanes` в формат джоба (нормаль внутрь, `w` = distance) |
| `string Report()` | состояние одной строкой (лог, лаборатория) |
| `string LabCullBench(count = 20000, reps = 20, batchSize = 64)` | замер: одни и те же сферы обоими путями, мс и совпадение индексов; активирует, как `Ensure()` |
| `Status`, `FoundIn`, `Built`, `ManagedSha256`, `NativeSha256`, `ProbeMode`, `ProbeLayout`, `ExpectedLayout`, `LayoutMatches`, `JobNames`, `JobModes`, `LibraryLoaded`, `AssemblyLoaded`, `BurstCompilerChecked`, `BurstCompilerEnabled`, `Initializers`, `KnownAnswerCulls`, `KnownAnswerSpheres`, `KnownAnswerMs`, `KnownAnswerResult`, `ActivationMs`, `CullsBurst`, `CullsManaged`, `CullSpheres` | поля для лаборатории |

```csharp
// при загрузке мира, один раз:
BurstBootstrapModule.Ensure();
// каждый кадр — visible: spheres.Length; batchCounts: BatchCount(spheres.Length, 64); visibleCount: 1 — массивы свои, переиспользуемые
JobHandle h = BurstBootstrapModule.ScheduleCull(spheres, planes, visible, batchCounts, visibleCount, 64, deps);
// … своя работа, джобы идут на рабочих потоках; позже, до чтения
h.Complete();
for (int k = 0; k < visibleCount[0]; k++) Draw(visible[k]);   // индексы по возрастанию
```

Сфера видима, если `dot(n, c) + d >= -r` для всех шести плоскостей. `ProxyCullJob` — `IJobFor` по номерам батчей
(`ScheduleParallel(batchCount, 1)`): батч b проверяет сферы `[b*64, (b+1)*64)`, пишет свои видимые индексы в
`visible[b*64 …]` и их число в `batchCounts[b]`, затем `ProxyCompactJob` сдвигает всё к началу. Это каркас шага 2
§3.7: LOD, плоскость выхода, объём теней от солнца и вывод `(batch, matrix)` добавит S5, сохранив форму.
`LabCullBench` — единственное место, где модуль сам что-то считает, и только по вызову.

RealPortal (FarWorld) берёт жёсткую зависимость от движка (§5.2), так что зовёт это напрямую. Свой двойник в плагине
(§3.1) не нужен, пока он совпадает с двойником движка.

## Правила для новых Burst-джобов

- Джоб — в `Unity\Assets\RealPortal.Burst`, без типов Valheim, только CoreModule (`NativeArray`, `IJob`, `IJobFor`,
  `IJobParallelFor`) и Mathematics. **Пакет Collections не добавлять** (ни `IJobParallelForBatch`, ни контейнеры): Burst
  компилирует в одну библиотеку все джобы и функции сборки, и с пакетом наша библиотека снова несла бы код Collections
  игры под её хэшами. `BurstLibraryBuild` падает, если `Unity.Collections.dll` попала в плеер.
- Вход для движка — статический метод в `BurstJobs` с параметрами из типов, которые движок знает (CoreModule,
  Mathematics), и управляемый двойник с тем же кодом в движке (или плагине); если важно совпадение с двойником —
  `FloatMode.Strict` у джоба и приведение к `float` после каждой операции с float в двойнике (как в `Distance`: без
  него Mono считает в double), и случай для джоба в известном ответе движка и в граничных случаях самопроверки.
- Размер нового джоба — в `ProbeJob.Execute` (следующий слот) и в `BurstJobs.ExpectedLayout`. У каждого джоба — режим
  пробы без новых полей (как `BatchSize <= 0` у cull: пишет `ModeBurst`, `[BurstDiscard]`-метод перезаписывает
  `ModeManaged`), его имя — в `BurstJobs.JobNames`, его запуск так, как его ставит движок, — в `BurstJobs.ProbeJobs`.
- При любом изменении полей джоба, его интерфейса или входа в `BurstJobs` — `ApiVersion + 1` в `BurstJobs` и в
  `BurstBootstrapModule`. Изменённое тело при тех же полях — тот же коммит меняет двойник в движке, и пара
  пересобирается. Манифест такое не ловит: он лишь связывает два файла пары друг с другом. Ловит известный ответ при
  активации — если правка меняет ответ на его наборах (иначе — расширить наборы под новое правило).
- `ProbeJob` и `BurstJobs.ProbeLength` (16) не менять никогда: проба выполняется до проверки раскладки, и только
  стабильность её полей делает проверку безопасной (нативная проба пишет лишь в слоты, которые есть в массиве).
- После изменения — пересобрать (`BurstLibraryBuild.Build`) и класть все три файла вместе.

## Проверка

- **Сборка пары в Unity** (batchmode, 2026-10-24, копия `RP\Unity` во временной папке; пакеты разрешились без
  Collections, `packages-lock.json` перенесён в проект): 0 ошибок и 0 предупреждений компиляции и плеера; в редакторе —
  проба Burst, раскладка `2/200/152`, оба джоба Burst, cull на 21 случайном наборе = эталону, граничные сферы = известному
  ответу; в собранном плеере — проба Burst, раскладка `2/72/56`, оба джоба Burst, 21 набор = эталону, граничные = ответу;
  `RESULT: ok`. И в редакторе, и в плеере выражение без приведений даёт `1,2,4,5,7,8` вместо `0,2,3,5,6,8` (Mono
  считает его в double), приведения и Burst — `0,2,3,5,6,8`. Две пересборки подряд: управляемая dll та же
  (`fe909cf9…`), нативная отличается одним полем `TimeDateStamp` (`d5f21d9e…` → `25d94dfc…`); в `RP\Build` — вторая.
  С `--burst-disable-compilation` у редактора — `RESULT: failed` (4 проверки: IsEnabled, проба, оба джоба), ничего не
  скопировано (проверено на сборке до приведений). Экспорт нативной библиотеки: 6 точек входа (те же хэши, что до
  приведений), с библиотекой Valheim пересекаются только 3 функции Unity.Burst (было 27).
- **Сборка движка** с модулем (`dotnet build -c Release`, отдельная копия): 0 ошибок, только обычные MSB3277; со
  всеми текущими модулями `Engine\Modules` — тоже.
- **Тестовый плеер** Unity 6000.0.75 (Windows x64, Mono, не development; Burst 1.8.29 / Collections 2.6.5 /
  Mathematics 1.3.2, как в игре; свой `lib_burst_generated.dll` без наших джобов — как у игры). `BurstBootstrapModule.cs`
  и `Features.cs` без изменений собраны `dotnet` в `RealPortalEngine.dll` (как настоящий движок), вместо BepInEx/Harmony —
  заглушки (`ConfigFile`, `ConfigEntry`, `Chainloader.ManagerObject`, `ManualLogSource`). Прогон:
  `Test.exe -batchmode -nographics -scenario=<имя>`, каждый сценарий — отдельный процесс. «До загрузки» значит: после
  отказа `RealPortal.Burst` нет в домене и `LoadAdditionalLibrary` не вызывался.

| Сценарий | Что | Результат |
|---|---|---|
| `normal` | конфиг on, установка после загрузки сцены; пара = `RP\Build` | после `Install` ничего не загружено; `Ensure()` → `active`, probe Burst, `2/72/56 vs 2/72/56`, оба джоба Burst, `known answer equal (6 culls, 510 spheres)`; cull Burst = эталону, джобы закончились на рабочих потоках до `Complete`; off → управляемый, on → снова Burst, индексы те же; `LabCullBench(20000, 20, int.MaxValue)` и `(0, 3)` работают, индексы равны |
| `oldpair` | пара до приведений (`dde51a32…`/`edb7c102…`, `Distance` без приведений) | `active`, известный ответ равен: Burst и без приведений считает во float, так что приведения в двойнике его код не меняют по смыслу |
| `body` | сборка, где у `ProxyCullJob` `<=` вместо `<` (поля те же, верный JSON) | probe Burst, раскладка совпала, оба джоба Burst, но `known answer DIFFER: frustum set, batch 1: Burst 136 visible, managed 236` (100 касательных сфер) → `inactive`, cull управляемый |
| `early` | установка на `SubsystemRegistration` | ничего не загружено до `Ensure()`, дальше как `normal` |
| `relenv` | `REALPORTAL_BURST_DIR` относительный | приведён к полному пути, `active` |
| `off` | `BurstBootstrap = false` | `Ensure()` → false, ничего не загружено; `Features.Set on` — всё ещё ничего; следующий `Ensure()` → `active` |
| `noconfig` | нет `Chainloader.ManagerObject` | первый `ScheduleCull` активирует и уже идёт через Burst |
| `nojson` | пара без JSON | отказ до загрузки (3 мс), всё управляемое и верное |
| `unity` | JSON с `unity` 6000.0.99f1 | «built for Unity 6000.0.99f1, the game runs 6000.0.75f1», до загрузки |
| `burstsize`, `coresize` | другой размер `Unity.Burst.dll` / `UnityEngine.CoreModule.dll` в JSON | отказ с обоими размерами, до загрузки |
| `mismatch`, `sizes` | чужой SHA-256 / размер файла в JSON | «not one build», до загрузки |
| `apiold` | пара прежних исходников (ApiVersion 1) | «the pair is ApiVersion 1, this engine calls 2», до загрузки |
| `devdir`, `nodev` | `RP_LAB_ROOT` задан → `RP\Build`; без него | с прежней парой в `RP\Build` — «the pair is ApiVersion 1…» (так было до её замены); с текущей — `active`, известный ответ равен. Без `RP_LAB_ROOT` (`nodev`) путь разработки не проверяется |
| `preloaded` | `RealPortal.Burst` уже загружена из другой папки | отказ до `LoadAdditionalLibrary` |
| `managedlib` | наша dll + `lib_burst_generated.dll` плеера (верный JSON) | библиотека загружена, probe managed → `inactive` |
| `layout` | новая dll + нативная библиотека прежних исходников (верный JSON) | probe Burst, `1/72/56 vs 2/72/56` → `inactive` |
| `nocull` | сборка, где у `ProxyCullJob` нет `[BurstCompile]` (верный JSON) | probe Burst, раскладка совпала, но `jobs ProxyCullJob managed` → `inactive`, `CullsBurst` не растёт |

Время активации (`ActivationMs`, разово): 30–38 мс без известного ответа; с ним — 44–62 мс в прогонах, где рядом шла
игра стенда (до 120 мс в серии из 20 прогонов подряд), из них `KnownAnswerMs` — 4–11 мс; отказ до загрузки — 2–21 мс.
Скорость каркаса cull (`LabCullBench`, schedule + Complete на главном потоке, среднее, два прогона на свободной машине,
двойник ещё без приведений — с ними у управляемого пути на каждый шаг `Distance` по `conv.r4`; Burst-код не
изменился по смыслу):

| Сфер | Управляемый | Burst | × |
|---|---|---|---|
| 1000 (200 повторов) | 0,012–0,020 мс | 0,006–0,007 мс | 2,0–3,1 |
| 20000 (50) | 0,096–0,100 мс | 0,023–0,036 мс | 2,7–4,3 |
| 100000 (20) | 0,37–0,40 мс | 0,14 мс | 2,6–2,9 |

Каркас почти ничего не считает (6 скалярных произведений на сферу), так что время — в основном постановка двух
джобов и обход памяти; оценка «~5× медленнее без Burst» из §5.2 относится к полному cull S5 (LOD, плоскость выхода,
объём теней, вывод матриц) и в игре не проверена. Плеер был без графики (`-nographics`), рабочие потоки свободны.

Стенд для повторения (временный, не в репозитории): тестовый проект Unity с пакетами как выше, пустой сценой,
`Assets\Plugins\RealPortalEngine.dll` (модуль + `Features.cs` + заглушки, собранные `dotnet`) и `Runner.cs` с
`[RuntimeInitializeOnLoadMethod]`, который делает сценарий, пишет `result-<сценарий>.txt` рядом с exe и выходит;
наборы файлов `files-<сценарий>` с JSON, пересчитанным под каждый случай.

## Риски

1. **Общий с игрой нативный код.** В нашей библиотеке, кроме наших джобов, только `IsBurstEnabled` и два
   CSR-трамплина Unity.Burst под теми же хэшами, что у игры. Игра привязывает их при старте (`$BurstDirectCallInitializer`
   Unity.Burst, фаза `AfterAssembliesLoaded`), задолго до первого `Ensure()`; код Collections и MagicaCloth с нашей
   библиотекой больше не пересекается. Проверять в лаборатории всё равно: ткань MagicaCloth и всё, что использует
   Collections, ведут себя одинаково в сессии с активацией и без неё (сравнивать перезапуском — после загрузки off её
   не выгружает).
2. **Обновление игры** на другую Unity или Burst: манифест не совпадёт (`unity`, размеры `Unity.Burst.dll` или
   `UnityEngine.CoreModule.dll`) → отказ до загрузки, с причиной в логе, всё на двойниках. Если обновление сохранит
   версию Unity и размеры, но сменит продюсер джоба, `ProbeJobs` поймает джоб, оставшийся без Burst-кода. Чего не поймает
   ничто: другая раскладка служебной структуры CoreModule при той же версии Unity и том же размере dll — на практике
   так не бывает. Пересобрать в редакторе той же версии, что у игры, и обновить пакеты по разделу «Версии».
3. **Пара из разных сборок** ловится манифестом (SHA-256 обоих файлов) до загрузки, и ещё раз — пробой и `ProbeJobs`.
   Без манифеста пара не используется. **Пара из одной сборки, но от других тел джобов**, чем двойники этого движка
   (тело правили с одной стороны), — известным ответом при активации, если правка меняет ответ на его наборах; правку,
   которая на них ответа не меняет (например, новый порог LOD в S5), он не увидит — для неё расширить наборы.
4. **Момент активации.** Первый `Ensure()`/`ScheduleCull` стоит ~40–60 мс на главном потоке. Потребитель (FarWorld)
   должен звать `Ensure()` в месте загрузки; если первым окажется `ScheduleCull` посреди кадра — один такой фриз.
   Проба идёт через `Run()` и `Complete()` на главном потоке, так что взаимоблокировок со статическими конструкторами
   нет.
5. **Путь разработки** `<repo>\Build` зашит в модуль, но смотрится только при
   `RP_LAB_ROOT` (прогоны стенда). Для другой машины или прогона с конкретной парой — `REALPORTAL_BURST_DIR`.
6. **Плавающая точка.** Mono Unity (тот же, что у игры) считает выражения с float в double, пока результат не приведён
   к `float`: без приведений двойник отсекал бы иначе, чем Burst, сферы в пределах шага float от плоскости (самопроверка
   это показывает на подобранных сферах). Поэтому `Distance` в двойнике, в `ProxyCullJob` и в эталоне самопроверки
   приводит к `float` каждый шаг; Burst в `FloatMode.Strict` считает во float сам. Расхождение, если его снова внесут,
   ловят граничные случаи самопроверки (сборка падает) и известный ответ при активации (Burst не включается).
   Денормализованные радиусы в наборы не входят. Визуально разницы и раньше не было бы: такие сферы — объекты ровно на
   границе кадра.
7. **Выигрыш каркаса мал** (2–4,3× на тестовом плеере): ворота S11 (≥3×) проверять на настоящих джобах, а не на
   каркасе. Счётчики `CullsBurst`/`CullsManaged` и `JobModes` показывают, что шло на самом деле.
8. Неподписанная нативная dll рядом с плагином может насторожить антивирус (как любая нативная dll мода).

## Что мерить в лаборатории

- **Активация (P6 из §8):** вызвать `BurstBootstrapModule.Ensure()` рефлексией (или `LabCullBench`, он активирует сам)
  и записать `Report()`: `active`, `probe Burst`, раскладка `2/72/56 vs 2/72/56`, `jobs ProxyCullJob Burst,
  ProxyCompactJob Burst`, `known answer equal (6 culls, 510 spheres, …)`, `ActivationMs`, SHA-256 пары (с текущей
  парой в `RP\Build` — `fe909cf90d565a42 / 25d94dfcbb9c4c73`). Записать ответ в `Lab\Out\probes.json`
  (`P6: Burst | managed`). Если `inactive` — сначала причина в `Report()`: «the pair is ApiVersion 1» значит не
  отсутствие Burst, а устаревшую пару в папке, откуда она взята (`files …`).
  Отдельно — запуск игры с `--burst-disable-compilation`: ожидается `inactive (Burst is disabled …)`, игра работает.
- **Скорость cull:** `BurstBootstrapModule.LabCullBench(20000, 50)` и `(100000, 20)`: `indices equal`, мс обоих путей;
  сравнить с таблицей выше (в игре рабочие потоки заняты её джобами — ускорение может быть другим).
- **Переключение:** `ab burst A="feat BurstBootstrap off" B="feat BurstBootstrap on"` имеет смысл, когда FarWorld
  (S5) начнёт звать `ScheduleCull`. До первого `Ensure()`/`ScheduleCull`/`LabCullBench` модуль не читает ни одного
  файла и ничего не грузит, так что замеры других модулей (кадр, длительность загрузки) от него не зависят — если
  лаборатория в этом прогоне сама его не активировала. После активации библиотека остаётся до конца сессии: для
  сравнений «без Burst вообще» — новый прогон без вызова или с `BurstBootstrap = false`. Счётчики `CullsBurst`,
  `CullsManaged` подтверждают, какой путь шёл.
- **Нет вреда игре:** прогон с активацией и без — тот же кадр (p50/p99) в обычной сцене, ткань MagicaCloth (плащи)
  ведёт себя одинаково, в логе нет ошибок Burst/джобов.
