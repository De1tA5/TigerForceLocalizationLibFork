# TerrariaTigerForceLocalizationLib 工程分析报告

分析日期：2026-11-05  
分析对象：当前本地工作区；`build.txt` 版本为 **0.3.2**，HEAD 为 `4150e67`。  
范围：功能、工程结构、实现细节、具体优化建议、使用方法、库维护者与汉化开发者建议。

本报告以本地代码为依据，没有将在线仓库、历史对话中的需求或其他工程当作本工程已实现的功能。下文明确区分“现有能力”“已复现问题”“代码审查发现”“待运行验证风险”和“建议设计”。本次没有修改 `Temp` 之外的工程内容。

## 1. 整体判断

这是一个面向 tModLoader 的**硬编码字符串本地化工具库**。它使用反射定位方法，借助 MonoMod 修改 IL 中的 `ldstr` 指令，把原本写死在方法中的文本替换成译文，或者替换成运行时本地化键查询。单独启用该库不会自动翻译其他模组，需要汉化模组主动调用 API。

现有设计的主要价值是：汉化者可以先扫描目标模组生成翻译键，之后主要在 HJSON 中维护译文；对于少量特殊方法，也能直接通过字典配置替换规则。过滤器可以组合，避免将一部分资源路径、语言键和内部标识误当作显示文本。

**当前最需要优先解决的是键名稳定性、IL 调用识别正确性和钩子重复执行时的状态管理。** 当前库已经具备基础动态取词能力，但还不具备完整的静态缓存刷新、外部配置控制、条件翻译覆盖和可审计的补丁管理系统。不能把“能扫描到字符串”理解成“所有字符串都适合翻译”，也不能把“每次执行时查语言键”理解成“所有缓存文本都会立即变化”。

建议保留现有公开 API，在内部逐步拆分扫描、规则解析和补丁应用，而不是立即推倒重写。先修复可以小范围验证的正确性问题，再扩展配置和性能能力，可以减少已有汉化包的迁移成本。

## 2. 工程结构与依赖

### 2.1 文件职责

工程包含 8 个正式 C# 源文件，共约 1,804 行，包含注释和空行；不计 `Temp`、构建中间文件及 IDE 文件。

| 文件/目录 | 职责 | 需要关注的点 |
| --- | --- | --- |
| `TigerForceLocalizationLib.cs` | 空的 `Mod` 入口类 | 没有显式加载/卸载管理逻辑 |
| `TigerForceLocalizationHelper.cs` | 批量扫描、方法本地化、键名生成、HJSON 值转义、导出进度钩子 | 核心功能集中；同时包含运行时功能和开发辅助功能 |
| `ForceLocalizeSystem.cs` | 手动映射式本地化、按类型/方法定位、顺序替换、派生方法处理、类型缓存 | 名称中虽有 System，实际并不继承 `ModSystem`，不会自动参与 tML 生命周期 |
| `TMLReflections.cs` | 缓存 tML 公共/非公共方法、字段和加载界面访问器 | 对 tML 内部实现和成员名有较强耦合 |
| `Filters/FilterBase.cs` | 包装一个 `Func<T, bool>` | 通用过滤器基础 |
| `Filters/TypeFilter.cs` | 类型名过滤及逻辑组合 | 支持完整类型名集合匹配 |
| `Filters/MethodFilter.cs` | 方法名、声明类型过滤及默认方法过滤 | 默认规则会主动排除一些加载/初始化方法 |
| `Filters/ILCursorFilter.cs` | 根据字符串的 IL 使用位置过滤 | 依赖简化的栈跟踪；有具体计算问题 |
| `Localization/en-US_Mods.TigerForceLocalizationLib.hjson` | 库自身本地化文件 | 当前仅有极少内容，不是目标模组的翻译词库 |
| `TigerForceLocalizationLib.csproj` | 引入上一级 `tModLoader.targets`；启用 Nullable 和 XML 文档 | 构建依赖本机 tML targets，没有自包含依赖版本清单 |
| `build.txt` | 模组名、作者、版本、源代码及打包忽略设置 | 当前未排除 `Temp` |
| `README.md` | 基础安装、扫描和手动本地化说明 | 使用示例与 API 签名存在容易误解之处；缺少完整故障排查 |
| `Properties/launchSettings.json` | tML 客户端/服务器调试启动配置 | 不是自动化测试 |
| `LICENSE` | MIT 许可文本 | 分发时应保留许可证声明 |

### 2.2 当前本机的依赖链

本工程导入 `..\tModLoader.targets`，后者导入本机 `D:\steam\steamapps\common\tModLoader\tMLMod.targets`。当前该 targets 指定：

- 目标框架 `net8.0`，语言版本 C# 12。
- 引用本机 `tModLoader.dll` 和 `Libraries` 下的托管依赖。
- tML 编译常量包含 `TML_2026_09`。
- 正常构建还会执行 tML 的模组打包目标，可能调用游戏服务器入口。

以上是**当前本机读取到的构建环境**，不能据此承诺兼容所有 tModLoader 版本。已有 `obj/project.assets.json` 中的框架下载元数据还记录了 8.0.31；本次独立编译实际使用本机可用的 8.0.30 引用程序集。正常还原/打包流程没有在本次执行。

工程目前没有独立测试项目、CI 配置、版本兼容矩阵或稳定性基准。维护者电脑上能构建，不等于其他开发者克隆后无需配置就能构建。

## 3. 功能和具体实现

### 3.1 批量扫描与 HJSON 驱动

入口：`TigerForceLocalizationHelper.LocalizeAll(...)`。

执行过程：

1. 根据目标模组内部名取得 `Mod`；找不到会抛异常。
2. 使用 `AssemblyManager.GetModAssemblies` 取得程序集并枚举可加载类型。
3. 遵循目标模组的 `PreJITFilter`；检查嵌套类型的外层类型。
4. 排除开放泛型、接口等类型，并应用可选类型过滤器。
5. 枚举声明在该类型中的方法和构造函数，排除抽象、开放泛型、无方法体等方法。
6. 生成每个方法的键名，调用 `LocalizeMethod`。
7. 读取已有 `OldString`/`NewString` 条目，并通过 `MonoModHooks.Modify` 挂 IL 补丁。
8. 注册模式下，把新发现的字符串注册到 tML 本地化系统；后续由 tML 的本地化文件更新流程导出。

典型键路径：

```text
Mods.<汉化模组内部名>.ForceLocalizations.<类型完整名>.<方法键>.<序号>.OldString
Mods.<汉化模组内部名>.ForceLocalizations.<类型完整名>.<方法键>.<序号>.NewString
```

重要细节：

- `registerKey: true` 不只是“扫描/导出”：仍会进入 IL 挂钩流程。
- `registerKey: false` 不会完全免除程序集反射扫描；它主要通过已有翻译键判断是否需要处理某个方法。
- 同一个方法中相同的原字符串默认合并为一组条目；不是每条 `ldstr` 都单独编号。
- 翻译组序号从 1 开始，遇到第一个缺失的 `OldString` 就停止读取后续组。
- 同文不同译使用 `NewString_1`、`NewString_2` 等，编号也必须连续。
- 次序译文对应的是 **IL 中匹配位置的先后顺序**，不是游戏运行时第一次、第二次执行方法的次数。
- 存在 `.1.OldString` 才会进入普通运行模式的该方法处理；空缺会使整个方法被跳过。
- 该流程依赖 tML 本地化文件更新机制；库中没有单独的 HJSON 文件写入器或文件监听器。

### 3.2 动态取词与固定替换

| 模式 | 实际做法 | 优点 | 边界 |
| --- | --- | --- | --- |
| `useLocalizedText: true`，默认 | 将字符串位置改为加载键名并调用 `GetTextValueModified(key)` | 执行到该位置时读取当前语言值 | 依赖 tML 已刷新本地化数据；无法自动追溯修改旧缓存 |
| `useLocalizedText: false` | 挂钩时读取译文，直接改 `ldstr` 的操作数 | 不增加该位置的每次语言键查询 | 已挂补丁不会自动随语言切换或译文文件变化而重新取值 |

源码注释提到 `LocalizedText.Value`，但该动态分支的实际代码是调用 `Language.GetTextValue`，再处理本库的前导空白转义，并没有把 `LocalizedText` 对象绑定到每个位置。

“支持热重载”应分成三个层次：

| 变更 | 当前能力 |
| --- | --- |
| 修改已经绑定的 `NewString` 值 | 动态模式下，在 tML 完成语言数据刷新、目标代码再次执行后可以读取新值 |
| 修改 `OldString`、新增/删除映射、更改方法定位或出现次序规则 | 规则字典和补丁在挂钩时确定；没有自动重新解析与补丁差异更新机制 |
| 修改初始化时写入字段、数组、字典、UI 对象的字符串 | 没有通用的缓存刷新机制，需要额外适配 |

### 3.3 手动指定替换

入口类：`ForceLocalizeSystem<TSelf>` 及其派生类。

| API/类 | 现有用途 |
| --- | --- |
| `Localize(MethodBase, Dictionary<string,string>)` | 指定方法内，按原文匹配并替换全部匹配位置 |
| `LocalizeByType*` | 按类型、类型短名或类型全名定位方法 |
| `LocalizeInOrder*` | 按给定原文序列依次消费匹配项，支持相同原文的不同译文 |
| `LocalizeDerived*` | 寻找目标模组内的子类型同签名方法并替换，可包含原方法 |
| `ForceLocalizeSystemImpl<TMod>` | 固定文本替换的便捷实现 |
| `ForceLocalizeSystemByLocalizeText<TSelf>` | 将字典的值解释为本地化键，在执行时查询 |
| `ForceLocalizeSystemByLocalizeTextImpl<TMod>` | 绑定模组类型的动态取词便捷实现 |

注意：按方法名定位的重载使用 `GetMethod(name, flags)`，不能可靠区分多个重载。遇到歧义应自己用参数类型取得 `MethodInfo`，再调用 `Localize`。构造函数也应直接取得 `ConstructorInfo` 后传入。

`LocalizeInOrder` 的实际行为是沿 IL 向后搜索下一项期望的原文，不匹配的 `ldstr` 会被跳过。它不是严格的完整指令序列校验器；最终没有全部匹配时，也没有完整的失败计数反馈。

`TMod` 便捷类使用 `typeof(TMod).Name` 推导目标模组名。若主类名与模组内部名不一致，需要特别核对；自定义派生类直接覆盖 `ModName` 更明确。

### 3.4 过滤器

三个过滤层分别针对 Type、MethodBase、ILCursor，支持 `&`、`|`、`!`、`MatchAll`、`MatchAny` 等组合。

`CommonMethodFilter` 会跳过一批不适合自动翻译的方法，例如资源属性、存档读写、加载卸载、配方注册等。它也会排除 `SetDefaults`、`SetStaticDefaults` 等初始化入口，所以初始化阶段设置的显示文本可能不会进入默认扫描结果。

`CommonCursorFilter` 尝试识别字符串被哪个方法消费，通过被调用方法的声明类型黑名单过滤资源访问、本地化、存档和反射等场景。其判断不是完整数据流分析：

- 字符串先进入局部变量、字段、容器时，通常无法继续追踪。
- 遇到分支等控制流时会放弃分析。
- 找不到用途时默认放行，仍可能导出不应翻译的内部键。
- 按类型整体屏蔽可能同时误排显示文本，不能代替人工审核。

### 3.5 开发辅助与 tML 私有反射

`ShowLocalizationRegisterProgress` 对本地化文件更新流程挂钩，将键注册进度显示到加载界面。成功结束后会主动释放该组进度钩子。

`ForceAllWeakReferences = false` 通过修改 tML 本地化文件更新方法的 IL，尝试绕过缺失相关模组导致不更新文件的分支。它是一个进程范围的开发辅助开关，不是单目标隔离的功能，也不是 HJSON 条件覆盖规则。

`TMLReflections` 集中缓存私有成员，便于定位耦合点，但大部分反射结果直接使用 `!`。成员不存在时，没有统一的版本探测和明确降级提示。

## 4. 已确认问题与具体优化建议

优先级含义：P1 为优先解决的正确性问题；P2 为稳定性、兼容性或性能改进；P3 为维护与体验改进。下述建议均未修改到正式源码。

### 4.1 问题总表

| 编号 | 优先级 | 问题 | 证据与定位 | 建议 |
| --- | --- | --- | --- | --- |
| F01 | P1 | 方法键冲突检测使用的键形式不一致 | `TigerForceLocalizationHelper.cs:184–207`；已做隔离复现 | 使用同一形式的完整键检查与记录；参数签名加入完整类型信息 |
| F02 | P1 | `LocalizeMethodByRoot` 可能把已经存在的正确键视为冲突 | 同文件 `191–207`、`319–324`；已做键选择复现 | 将确定性键解析与新增条目分配分开；已有键不应导致定位漂移 |
| F03 | P1 | IL 用途识别把 void 返回值当成压栈，并忽略实例接收者 | `Filters/ILCursorFilter.cs:418–439`；两个输入已复现 | 按 call/callvirt/newobj/calli 分别计算栈效果，正确区分 Void 和 HasThis |
| F04 | P1 | 同文不同译的消费游标跨 IL manipulator 执行保留 | `TigerForceLocalizationHelper.cs:240–306`、`337–346`；已复现计数状态问题 | 每次 manipulator 执行使用独立游标或纯函数式位置索引 |
| F05 | P1 | 部分缺损/重复 HJSON 条目会导致丢映射、占号或异常 | 同文件 `242–269`、`287–293`；代码路径审查 | 解析与校验分离；报告缺项和重复原文；新编号基于已存在索引而非映射数量 |
| F06 | P2 | 弱引用检查的对象与文档描述不一致 | 同文件 `120–137` 使用目标 `mod.TranslationForMods` | 根据实际导出归属验证汉化模组；区分 `translationForMods` 与一般弱依赖 |
| F07 | P2 | 发布构建中错误日志可能完全消失 | `ForceLocalizeSystem.cs:329–369` 的 `[Conditional("DEBUG")]` | 错误和重要警告保留到 Release；仅详细诊断受 DEBUG 控制 |
| F08 | P2 | 私有反射、硬编码局部变量槽和裸 Hook 的生命周期脆弱 | Helper `69–82`、`383–443`；ForceSystem `373–382`；`TMLReflections.cs` | 建立兼容探测、Hook 注册表、失败回滚和统一释放入口 |
| F09 | P2 | 注册扫描对 IL 的分析与挂钩重复，缺少逐方法隔离 | Helper `145–186`、`234–237`、`274` | 分离只读扫描和实际补丁；单方法失败应可诊断并按策略继续 |
| F10 | P2 | 方法过滤变化、重载变化会使翻译键漂移 | Helper `175–184`、`199–200` | 用稳定签名生成 ID；提供键迁移和规则变更检查 |
| F11 | P2 | “派生方法”识别不等于“重写方法”识别 | ForceSystem `217–242` 只检查继承关系与同名同参数 | 比较 `GetBaseDefinition()`；排除 new 隐藏的独立方法 |
| F12 | P3 | 类型缓存短名歧义、清理后不可重新初始化、仅主程序集索引 | ForceSystem `389–472` | 短名歧义时要求全名；明确生命周期；统一程序集扫描策略 |
| F13 | P3 | 构建、文档与打包规则不够自包含 | csproj、README、build.txt | 补齐版本矩阵、可构建示例、CI 和工作目录排除规则 |

### 4.2 F01：键碰撞未被正确消解

调用方把完整的 `methodKey` 放入 `usedMethodKey`，但 `GetMethodKey` 检查的是尚未附加 root 和类型名的 `realMethodKey`，两者不能相等。

同时，重载签名只拼接 `ParameterType.Name`。例如两个参数分别是 `A.Token` 和 `B.Token`，都被表示为 `Token`。隔离检查用原源码方法复现得到：

```text
第一个重载：Root.Example.Target.Say_Token
第二个重载：Root.Example.Target.Say_Token
```

影响：不同方法会共享翻译路径，同名原文可能错误复用译文，注册数据也难以区分来源。

修复建议：

1. 冲突检测使用与保存完全一致的键。
2. 定义确定性的签名格式，保留参数完整类型名、泛型参数、数组维度和 by-ref 等信息。
3. 不依赖反射枚举顺序分配后缀；确需短键时，可以附加稳定签名摘要。
4. 改键前提供旧键兼容读取或迁移清单，避免已有翻译失效。

### 4.3 F02：已有翻译使 `LocalizeMethodByRoot` 选择其他键

`GetMethodKey` 未收到显式检查函数时，会调用 `Language.Exists` 检查某个候选路径的 `.1.OldString`。这把“已有正确映射”当作“需要避开的占用”。

隔离检查中，已有：

```text
Root.Example.Target.Simple.1.OldString
```

再次解析得到：

```text
Root.Example.Target.Simple_2
```

随后在普通模式中，`LocalizeMethod` 检查 `_2.1.OldString`，不存在便返回。结果可能表现为首次注册正常，之后使用同一 Root 却不生效；注册模式还可能继续产生不必要的后缀。

建议将“从方法确定规则键”和“为冲突的新定义分配键”拆成两个操作。目前使用者可自行选择固定、明确的完整 `methodKey`，直接调用 `LocalizeMethod`，绕过这条不稳定路径。

### 4.4 F03：IL 调用栈跟踪不准确

当前使用 `methodReference.ReturnType != null` 判断是否压入返回值，但 Cecil 对 `void` 方法也提供表示 Void 的 TypeReference，所以该判断不能表示“存在返回值”。

此外，对实例调用只减去显式参数个数，没有减去 `this` 接收者；`calli` 还需要考虑函数指针等单独语义。`newobj` 的栈效果也应与普通实例调用分开实现。

已复现：

- 直接消费字符串的普通静态单参数调用可以识别，作为正常对照。
- 字符串加载后先调用不返回值的零参数方法，再被目标方法消费，当前识别结果为 `null`。
- 字符串作为实例接收者调用 `Trim()`，当前识别结果为 `null`。

因 `CommonCursorFilter` 在识别失败时放行，错误不仅是“少一个过滤结果”：内部资源标识、语言键等可能被误导出，人工若照单翻译，会改变功能行为。

建议先修复基本栈效果，并用小型 IL 片段覆盖 call、callvirt、newobj、void、dup、局部变量和分支。后续如果不做完整控制流分析，应返回“已确认显示文本/已确认内部标识/无法确定”三态，将无法确定的条目标记为待审核，不要声称它一定可翻译。

### 4.5 F04：重复应用时的出现顺序状态

`localizations` 字典在 `MonoModHooks.Modify` 的回调外建立，其中的 `MultipleNewString.count` 是可变字段。回调执行时依次消费该计数，但重新应用时没有重置。

隔离检查对同一对象模拟两次补丁遍历：

```text
第一次：first, second
第二次：fallback, fallback
```

该结果已确认对象状态无法复用；真实游戏中其他钩子增减是否触发相关 manipulator 重新执行，及具体触发顺序，还需要加载组合测试，不能把该隔离检查等同于已观察到游戏内串译。

修复方向是每次回调创建新的出现计数，以 `(方法, 原文)` 为维度记录当前 IL 位置，或将规则定义为不可变数据，消费状态仅保存在本次回调的局部变量中。

### 4.6 F05：HJSON 结构缺损没有明确校验

当前代码存在以下可直接推导的行为：

| 输入情况 | 当前结果 | 建议 |
| --- | --- | --- |
| 序号 1、3 存在，2 缺失 | 遇到 2 后停止，3 不读取 | 至少输出含键路径的明确警告；未来支持独立命名映射 |
| 同一方法两组 `OldString` 相同 | `Dictionary.Add` 可能抛出重复键异常 | 检测并解释应合并还是使用 `NewString_n` |
| 有 `OldString`，但无 `NewString` 且无 `NewString_1` | 不加入映射字典 | 输出缺项诊断；不要默默丢弃 |
| 上述缺项与注册模式同时存在 | 新项编号按 `localizations.Count + 1` 分配，可能重新使用已有编号 | 独立跟踪已有条目索引和下一个可用编号 |
| 新增 `NewString_n` 后不重建补丁 | 挂钩时的出现顺序映射保持原样 | 明确要求重载模组，或实现规则差异应用 |

建议增加一个只读校验阶段，输出每个方法的规则数量、缺项、重复项和计划替换位置，校验成功后再提交补丁。

### 4.7 兼容性和生命周期改进

以下是代码审查风险，尚未通过完整游戏运行验证：

- `TMLReflections` 对私有成员查找结果使用空值抑制，tML 更新后容易在远处发生空引用或类型初始化错误。应在功能启用时验证所需签名，给出包含成员名、tML 版本和功能名的错误。
- 弱引用辅助钩子依赖局部变量槽 12、13，进度钩子依赖槽 9。编译器或 tML 实现变动后可能失配；应验证局部变量类型和邻近语义，匹配失败时明确停用该辅助功能。
- `IL_NotAllWeakReferences` 前半段匹配失败会静默返回，后半段使用 `GotoNext` 却可能抛异常；开关状态不能完整代表补丁确实生效。
- 进度钩子只在成功路径中执行清理。若构造阶段部分挂钩成功后失败，或 `orig()` 抛异常，缺少统一 `try/finally` 和部分初始化回滚。
- `ForceAllWeakReferences` 持有的裸 `ILHook`、泛型静态构造函数创建的裸 `Hook` 不应直接假定等同于 tML 的自动管理钩子。需要明确归属、成功/失败卸载路径和重复初始化策略。
- `TypeHelper.Clear` 将 `cleared` 永久设为 true；同一个已闭合泛型类型在清理后不能再次建立索引。该行为应在 API 中明确为加载阶段限制，或者改为可重新建立的生命周期。
- 类型短名重复时当前仍选用首次索引的类型；Release 中又可能没有警告。建议对歧义直接返回失败并要求全名。
- `LocalizeDerived` 应检查虚方法基定义，避免处理 `new` 隐藏出来的另一条方法槽。

### 4.8 日志和结果反馈

`ForceLocalizeSystem` 的多处错误/警告函数标注了 `[Conditional("DEBUG")]`。在没有定义 DEBUG 的调用方编译中，这些内部调用会被编译器省略。`ThrowException` 的逻辑又放在这些函数内部，因此也不能指望它在 Release 中可靠抛出。

建议所有公开补丁操作返回可检查的结果，例如：

```text
目标模组 / 方法签名 / 规则键 / 预期替换数 / 实际替换数 / 跳过原因 / 异常摘要
```

错误应始终可见，逐指令调试日志才按详细程度开关控制。对于单个目标方法失败，库可以支持“严格开发模式抛错、发布模式隔离并记录”，但不能无条件吞掉所有异常。

## 5. 性能优化建议

本次没有测量真实模组组合下的耗时或帧率；以下建议来自执行路径分析，不能声称有确定的百分比收益。

### 5.1 分开优化加载期和游戏运行期

| 阶段 | 当前开销来源 | 建议 | 验证指标 |
| --- | --- | --- | --- |
| 扫描 | 类型/方法反射、读取方法体、DynamicMethodDefinition 和 IL 枚举 | 注册扫描使用只读 IL 读取；避免为了发现候选就给全部方法挂钩 | 扫描耗时、分配量、候选/总方法数 |
| 导出 | 大量 `GetOrRegister`，后续更新 HJSON 与 UI 进度 | 扫描生成不可变清单，再统一注册；进度刷新节流 | 导出耗时、键数量、进度 UI 更新频次 |
| 发布加载 | 即使不注册仍遍历程序集 | 根据已验证的方法清单定位；保留签名/版本检查和失败回退 | 反射次数、实际 Hook 数、加载耗时 |
| 游戏执行 | 每个被替换位置都做语言键查询及转义处理 | 按语言刷新更新可变文本槽，执行时读取槽值；只对需要的位置使用动态模式 | 每帧调用数、耗时、分配量 |
| 重新加载 | 资源、委托、反射缓存和补丁状态保留 | 统一拥有者与释放流程，避免重复扫描/重复 Hook | 连续加载后的 Hook 数和内存趋势 |

### 5.2 可以先做的小改动

1. 让扫描中创建的 `DynamicMethodDefinition`、`ILContext` 有清晰的释放范围；当前前置检查没有显式释放路径。
2. 原文未命中任何条目的方法不要创建无效果补丁；已有 `.1.OldString` 不等于实际存在匹配字符串。
3. 扫描时收集统计，区分“无 IL”“被 JIT 限制排除”“规则缺失”“零匹配”“挂钩失败”。
4. 加载期可以缓存确定性的方法签名和筛选结果，卸载时清除涉及程序集的引用。
5. 默认过滤器静态类型名集合大量使用 `Type.FullName`，触发 Nullable 警告；可通过专门帮助函数保证类型全名有效，而不是全局关闭警告。

### 5.3 缓存不能破坏正确性

如果增加扫描缓存，至少应记录目标模组版本、程序集 MVID 或内容指纹、过滤器/规则格式版本和方法签名。MetadataToken 或 IL 偏移只适合在已确认同一程序集版本时使用，不能单独作为跨版本永久 ID。

动态文本缓存必须在语言切换和译文更新后失效；扫描缓存和译文缓存应分开。不要把旧 `MethodInfo`、旧委托或旧程序集长期保存在跨模组重载的全局容器中。

## 6. 当前库的使用方法

以下代码是使用说明示例，没有写入正式项目。`MyChinesePack`、`TargetMod`、完整类型名和原文均为占位示例，需要替换为实际值。示例未在真实目标模组中运行。

### 6.1 安装与引用

README 提供两种依赖方式，应选择一种运行时分发方案：

| 方式 | `build.txt` 配置 | 场景 |
| --- | --- | --- |
| 独立前置模组 | `modReferences = TigerForceLocalizationLib` | 汉化包依赖用户启用本库模组 |
| 随包携带 DLL | `dllReferences = TigerForceLocalizationLib` | 汉化包携带经过验证的库版本 |

如果已有相应字段，应将名称合并到原列表中。随包引用的 DLL 放在汉化模组的 `lib` 目录，XML 文档可一并携带以提供 IntelliSense。编译期还需让 IDE/工程能够解析同一版本程序集，例如：

```xml
<ItemGroup>
  <Reference Include="TigerForceLocalizationLib">
    <HintPath>lib\TigerForceLocalizationLib.dll</HintPath>
  </Reference>
</ItemGroup>
```

目标模组是可选集成时，在汉化包的 tML 构建配置中声明相应弱依赖，并在执行前判断是否加载。直接引用目标模组类型的集成还要考虑 `JITWhenModsEnabled`、`ExtendsFromMod` 等加载约束；单纯运行时 `if` 不一定能保护提前发生的类型解析。

### 6.2 批量扫描生成翻译条目

在汉化模组中添加一个真正的 `ModSystem`，在 `PostSetupContent` 调用：

```csharp
using Terraria.ModLoader;
using TigerForceLocalizationLib;
using TigerForceLocalizationLib.Filters;

namespace MyChinesePack;

public sealed class HardcodedLocalizationSystem : ModSystem
{
    // 只在开发导出阶段设为 true；发布时改为 false。
    private const bool RegisterKeys = true;

    public override void PostSetupContent()
    {
        const string targetModName = "TargetMod";
        if (!ModLoader.HasMod(targetModName))
            return;

        if (RegisterKeys)
            TigerForceLocalizationHelper.ShowLocalizationRegisterProgress();

        TigerForceLocalizationHelper.LocalizeAll(
            selfModName: Mod.Name,
            modName: targetModName,
            registerKey: RegisterKeys,
            useLocalizedText: true,
            filters: RegisterKeys
                ? new TigerForceLocalizationHelper.LocalizeFilters
                {
                    MethodFilter = MethodFilter.CommonMethodFilter,
                    CursorFilter = ILCursorFilter.CommonCursorFilter
                }
                : null);
    }
}
```

`LocalizeAll` 前两个参数是 **string 内部名**，不是 `Mod` 对象。`Mod.Name` 是当前汉化模组的内部名，目标也必须使用内部名，不能传创意工坊标题或本地化显示名。

操作步骤：

1. 在开发环境备份翻译文件，启用目标模组，设 `RegisterKeys = true` 后构建并加载。
2. 等待 tML 完成本地化文件更新。自动输出依赖源码目录和 tML 导出条件，不能保证在没有源码的普通玩家环境生成文件。
3. 检查导出的条目，剔除资源路径、内部名称、存档键、协议内容和其他不应翻译的字符串。
4. 在 `en-US` 保留稳定的原文/回退，其他语言修改 `NewString`，保持 `OldString` 与 IL 原文一致。
5. 将开发开关改为 false，重新构建并验证普通运行模式。
6. 验证语言切换、译文刷新、卸载重载以及目标模组未启用时的行为。

示例沿用 README 的默认过滤器使用方式。**如果自定义 MethodFilter 改变了同名重载的候选数量，不能直接在运行模式去掉它**：当前键名是否带参数后缀取决于候选重载数量，可能发生键变化。此时应保持一致，或对相关方法显式指定稳定键。

### 6.3 HJSON 结构与同文不同译

假设目标类型为 `TargetMod.UI.NoticePanel`，方法为无重载的 `GetText`，汉化模组内部名是 `MyChinesePack`，默认根下的简化结构如下。实际以生成键为准：

```hjson
Mods: {
  MyChinesePack: {
    ForceLocalizations: {
      TargetMod: {
        UI: {
          NoticePanel: {
            GetText: {
              1: {
                OldString: Hello
                NewString: 你好
              }
              2: {
                OldString: Ready
                NewString: 就绪
                NewString_1: 准备好了
                NewString_2: 可以开始
              }
            }
          }
        }
      }
    }
  }
}
```

上例中，第一个 `Ready` 的匹配位置使用“准备好了”，第二个使用“可以开始”，后续使用默认“就绪”。如果没有 `NewString`，后续位置回退到 `NewString_1`。

维护规则：

- `OldString` 是匹配规则，不能随语言翻译；不要把它改成中文原文，除非 IL 原文就是中文。
- `NewString_n` 的“第几次”针对同一个方法内同一原文的匹配位置计数。
- 删除组时必须保证读取序号连续；例如删除 2 后，应把后续组重新连续编号。
- 对于 `{0}`、`{1}`、游戏内颜色标记、物品标签和换行，要保留对应语法；本库没有格式占位符校验器。
- 更改键名、组结构和匹配原文后，应重新加载模组验证，而不是仅观察文件值热更新。

### 6.4 只处理指定方法：建议使用固定键

```csharp
using System;
using System.Reflection;
using Terraria.ModLoader;
using TigerForceLocalizationLib;

// 放在已有 ModSystem 的 PostSetupContent 等加载期方法内。
if (!ModLoader.TryGetMod("TargetMod", out Mod target))
    return;

Type? type = target.Code.GetType("TargetMod.UI.NoticePanel");
MethodInfo? method = type?.GetMethod(
    "GetText",
    BindingFlags.Public | BindingFlags.NonPublic |
    BindingFlags.Instance | BindingFlags.Static,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);

if (method is null)
{
    Mod.Logger.Warn("无法定位 TargetMod.UI.NoticePanel.GetText()，已跳过本地化。");
    return;
}

TigerForceLocalizationHelper.LocalizeMethod(
    method,
    methodKey: $"Mods.{Mod.Name}.ForceLocalizations.Manual.NoticeText",
    registerKey: false,
    useLocalizedText: true);
```

第一次生成此方法的条目时将 `registerKey` 改为 true，生成后改回 false。路径使用显式 `methodKey`，不再依赖 `LocalizeMethodByRoot` 的自动键选择。目标方法有参数时，应把 `Type.EmptyTypes` 换成准确的参数类型数组；若目标类型位于附属程序集，需在目标的相关已加载程序集里定位。

### 6.5 手动映射并保留 HJSON 动态取词

可以定义一个不直接引用目标模组类型的适配器：

```csharp
using TigerForceLocalizationLib;

namespace MyChinesePack;

public sealed class TargetModText : ForceLocalizeSystemByLocalizeText<TargetModText>
{
    protected override string ModName => "TargetMod";
}
```

然后在汉化模组加载阶段调用：

```csharp
if (ModLoader.HasMod("TargetMod"))
{
    TargetModText.LocalizeByTypeFullName(
        "TargetMod.UI.NoticePanel",
        "GetText",
        new()
        {
            ["Hello"] = "Mods.MyChinesePack.Custom.Notice"
        });
}
```

对应自己的 HJSON：

```hjson
Mods: {
  MyChinesePack: {
    Custom: {
      Notice: 你好
    }
  }
}
```

这个分支中，字典的 **value 是本地化键**。如果继承基础 `ForceLocalizeSystem<TSelf>`，value 默认会直接作为最终替换文本。两种模式不能混用。

`GetText` 有重载时，使用前一节的精确反射方式取得方法，再传给 `TargetModText.Localize(method, dictionary)`。全名定位辅助接口不提供重载参数签名选择。

### 6.6 前导空白与引号

本库为保留前导空格/Tab，在存入本地化值时会按规则增加一个单引号，在读取时去掉该转义前缀。原文和译文都适用。

例如期望实际值为一个空格开头的 ` after whitespace`，HJSON 多行字符串可按 README 约定写成：

```hjson
NewString:
  '''
  ' after whitespace
  the second line
  '''
```

不要不加判断地为所有单引号开头的值再加引号。实际编码规则只处理前导空格、Tab，以及单引号后接空格、Tab 或单引号的情况。隔离检查验证了这些函数对若干字符串的编码/解码往返，但没有把该检查当作完整 HJSON 解析器测试。

### 6.7 常见问题排查

| 现象 | 优先检查 |
| --- | --- |
| 完全没有生成条目 | 内部模组名、调用阶段、注册开关、类型/方法过滤、源码导出条件 |
| 某个方法全部不生效 | `.1.OldString` 是否存在；方法键是否漂移；是否选错重载 |
| 后半段翻译消失 | 数字组或 `NewString_n` 是否中断 |
| 日志安静但翻译未命中 | 目前部分错误日志被 DEBUG 条件省略；先在调用侧记录定位和预期数量 |
| 翻译后找不到资源或存档异常 | 是否误译了资源路径、字典键、存档键、Mod.Call 命令等 |
| 改译文不更新 | 是否采用动态模式；tML 是否刷新数据；显示端是否缓存了旧字符串 |
| 第一次生成后不再生效 | 是否使用 `LocalizeMethodByRoot` 并发生 `_2` 键漂移 |
| 中英文切换后仍显示旧值 | 固定替换、初始化缓存、UI 未重建，或目标自己缓存最终文本 |

## 7. 建议扩展的能力及设计边界

本节都是设计建议，**当前库没有实现这些配置字段或新增 API**。

### 7.1 外部配置控制扫描和挂钩

建议分开三种模式，而不是只有一个 `Enabled`：

| 建议模式 | 扫描 | 导出 | 挂显示文本补丁 | 用途 |
| --- | --- | --- | --- | --- |
| Disabled | 否 | 否 | 否 | 明确停用 |
| ScanOnly | 是 | 是 | 否 | 审核可翻译候选，不影响目标执行 |
| ApplyTranslations | 按已验证清单定位，必要时扫描 | 否 | 仅对确认规则挂钩 | 发布模式 |

配置文件可以包含：目标模组内部名、最低/最高已测试版本、包含/排除类型、方法签名、文本用途、是否允许低置信候选、日志级别等。避免默认对所有含字符串的方法挂钩；“含字符串”还包括路径、存档、网络协议和内部命令。

配置应在扫描前校验，拼写错误和未知字段要报告。运行中切换启停涉及卸载或重新应用补丁，不能只改一个布尔值就承诺完整热生效；首版可以明确要求重载模组，再逐步增加安全的补丁差异更新。

### 7.2 静态数据和初始化缓存支持

建议以显式适配器描述需要刷新的数据，而不是无差别反射写入所有 string 字段：

| 数据形态 | 建议方案 |
| --- | --- |
| 每次显示时生成文本 | 保留动态 IL 取词或优先调用公开本地化 API |
| 可替换的字段/属性 | 记录原值和本地化键，通过明确 setter 刷新 |
| `string[]` 或可变列表 | 针对指定槽位更新，检查长度与对象生命周期 |
| 字典 value | 仅更新已确认是显示文本的 value |
| 字典 key、存档/协议标识 | 不作为通用翻译目标 |
| static readonly、不可变集合、已生成委托/缓存 | 优先选择目标公开接口、重建对象或修改消费点；不要假设现代 .NET 允许任意反射改写 |

刷新时机包括初次加载、语言变更、翻译值更新，以及目标数据重建之后。应保存原值、适配器拥有者和恢复策略，并检测其他模组是否已改过该值，避免卸载时覆盖别人的后续修改。

实际接入何种 tML 语言刷新通知，要按支持版本验证；所有 UI 或游戏对象写入必须在合适线程执行。

### 7.3 汉化合集的条件覆盖

当前源码没有按已启用模组集合计算优先级的规则系统，也没有普通 HJSON 加载层的条件隔离器。`ForceAllWeakReferences` 的开发导出绕过开关不能解决“B 未启用时，B 对 A 的翻译仍然加载”的问题。

可以为一条规则增加明确条件：`RequiredMods`、`ForbiddenMods`、目标版本、语言、显式优先级和规则来源。建议处理顺序是先判断条件，再对生效规则解决冲突，最后输出最终规则及来源。

不应依赖文件名字母序或偶然加载次序作为正式优先级。同一优先级的冲突应输出诊断，不能静默选一个。

还必须区分两条加载路径：

- 本库自己的硬编码映射可以在挂钩前筛选和合并。
- 放在 tML 普通 `Localization` 路径中的对其他模组键的覆盖，会按 tML 自身机制被读取；仅给本库规则增加条件，不能阻止这些文件的普通加载。要真正隔离，需要调整数据组织或在已验证的本地化加载边界接入条件提供器。

## 8. 给开发者的建议

### 8.1 给库维护者

推荐按下面顺序演进，每一步都可以独立提交和回归：

| 顺序 | 工作包 | 完成标准 |
| --- | --- | --- |
| 1 | 修复 F01/F02/F03/F04；补规则校验 | 原有翻译键有兼容策略；本报告的复现输入都能通过修复后的断言 |
| 2 | 补齐诊断、逐方法隔离、统一 Hook 归属与释放 | Release 可定位失败；加载失败后可清理；重复加载不叠加补丁 |
| 3 | 拆分只读扫描器、规则解析器、补丁执行器 | 导出可独立运行且不改目标执行行为 |
| 4 | 稳定签名清单、版本验证与缓存 | 目标升级后可明确报告失效，不误挂到其他方法 |
| 5 | 静态缓存适配与规则热更新 | 译文值变更、规则结构变更各自有明确刷新语义 |
| 6 | 外部配置与条件覆盖 | 关闭目标不安装补丁；缺失条件模组不应用覆盖；冲突可追踪 |

建议内部职责划分为：

- 扫描器：只读取程序集/IL，输出候选及来源上下文。
- 规则仓库：解析 HJSON 与配置，校验、合并并生成不可变规则快照。
- 补丁执行器：根据规则安装/重建补丁，维护每次应用的局部状态。
- 兼容适配层：集中处理 tML 私有成员及版本探测。
- 缓存适配器：在明确生命周期内更新并恢复目标数据。
- 诊断层：记录方法、规则、命中数量、来源、失败原因与耗时。

这些职责可以先作为内部类型组织，无需立即拆成多个程序集或引入大型框架。

文档应至少包含一个可构建的独立示例、重载/构造函数/弱依赖示例、HJSON 编号规则、静态缓存限制、发布前检查和 tML 版本支持表。README 当前的 `LocalizeAll(selfMod, targetMod, ...)` 容易被理解成传 Mod 对象，建议改成明确的字符串变量名。

### 8.2 给汉化包开发者

1. 能使用目标模组已有本地化键或公开 API 时，优先使用原生路径；IL 替换用于没有公开入口的显示文本。
2. 把首次自动导出结果当作候选清单，逐项确认是否面向玩家显示。
3. 尽量限制到需要的类型和方法；对 UI 高频绘制代码，先确认动态取词频率和缓存行为。
4. 保持 `OldString` 稳定，只翻译对应语言下的 `NewString`；英文回退也要维护。
5. 对目标模组更新记录签名和命中数量变化，不要仅凭“成功加载”判断汉化正常。
6. 可选目标在执行前判断是否加载；涉及目标类型的直接引用还要满足 JIT/类型加载约束。
7. 不在 `AI`、`Update`、`PreDraw` 等每帧路径反复调用这些挂钩 API，它们是加载期补丁入口。
8. 同一方法不要在多个入口重复调用本地化；合集需要统一登记规则归属，避免补丁互相覆盖。
9. 发布前关闭扫描注册与进度辅助功能，验证目标缺失、语言切换、多人/专服和连续重载等实际场景。
10. 分发时明确采用前置模组还是携带 DLL，并保持编译和运行时库版本一致，保留许可声明。

### 8.3 有价值的后续测试矩阵

| 类别 | 关键用例 |
| --- | --- |
| 方法定位 | 重载、同短名不同命名空间参数、嵌套类型、构造函数、静态构造函数、附属程序集 |
| IL 安全 | 无方法体、开放泛型、实例调用、void、newobj、分支、异常块、其他 IL 钩子增减 |
| 规则校验 | 编号缺口、重复 OldString、缺失 NewString、空字符串、前导空白、占位符变化 |
| 热更新 | 已绑定译文值、语言切换、新增/删除规则、改变出现顺序、目标缓存文本 |
| 生命周期 | 目标不存在、加载中异常、连续重载、卸载恢复、专服无加载 UI |
| 条件覆盖 | A 单开、A+B、B 关闭、相同优先级冲突、目标版本不符 |
| 性能 | 扫描候选规模、Hook 数、导出时间、动态查询热点、重载后内存趋势 |

## 9. 本次验证与结论边界

### 9.1 实际完成的检查

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| 正式源码、README、构建配置静态审查 | 完成 | 8 个正式 C# 文件均已阅读 |
| 独立 C# 编译 | 通过，退出码 0 | 直接调用本机 Roslyn，引用当前 tML 与 .NET 8 参考程序集，输出全部在 Temp |
| 编译诊断 | 185 个 CS8604、1 个 CS1701 警告 | 前者主要来自 `Type.FullName` 可空性；后者为本次引用集合下的程序集版本统一警告 |
| 方法键冲突 | 已复现 | 不同命名空间下的同短名参数得到同一键 |
| Root 键漂移 | 已复现 | 已有键导致选择 `_2` |
| 出现顺序状态复用 | 已复现 | 第二轮消费落入 fallback |
| void 调用与实例接收者识别 | 两项均已复现 | 使用 Cecil 构造 IL，调用从源码提取的实际分析函数 |
| 直接静态调用识别、空白转义往返 | 两项正常对照通过 | 不代表覆盖所有 IL 或 HJSON 语法 |

小型检查共 7 项：5 项问题复现、2 项正常对照，进程退出码为 0。这里的“通过”表示断言成功确认当前行为，不表示工程已修复。

### 9.2 没有做的验证

- 没有启动 Terraria/tModLoader，没有安装或启用目标模组。
- 没有执行正常模组打包和发布，也没有运行工程原目录的 `dotnet build/restore`。
- 没有测量真实帧率、加载耗时、内存泄漏或多钩子组合表现。
- 没有验证所有 tML 私有反射成员在当前运行时的匹配，也没有验证客户端与专服生命周期。
- 没有实际导出或改写正式 Localization 文件。

因此独立编译通过只能证明本次引用环境下源码可以编译；不能替代模组打包、运行集成或跨版本兼容验证。隔离复现使用从当前源码提取的函数和少量模拟依赖，证据范围见对应条目。

### 9.3 Temp 内的交付与辅助文件

| 文件 | 用途 |
| --- | --- |
| `ProjectAnalysis.zh-CN.md` | 本报告 |
| `analyze_verify.py` | 本次读取、独立编译、源码片段检查和范围核对脚本 |
| `compile_result.json` | 独立编译结果与原始诊断 |
| `probe_result.json` | 7 项检查的结果和证据 |
| `probes_compile_result.json` | 检查程序自身的编译结果 |
| `source_probes.cs.txt` | 从源码提取的检查代码，使用 `.txt` 结尾避免被主项目默认收集为 C# 源文件 |
| `library_compile.rsp`、`probes_compile.rsp` | 两次独立编译的明确参数 |
| `baseline_hashes.json`、`scope_verification.json` | Temp 外可读取文件的基线与完成时核对结果 |
| `AnalyzedLibrary.dll`、检查程序及其运行依赖 | 隔离验证产物，不是建议发布的版本 |

`analyze_verify.py` 使用当前环境的绝对依赖路径以及当前版本的源码行区间，供复核本次分析使用，不是跨版本通用测试框架。脚本只直接运行 `Temp` 中的检查 DLL，不启动游戏、不通过工程 targets 打包。

现有 `.gitignore` 和 `build.txt` 都没有专门排除 `Temp`。本次按要求没有修改这两个文件；后续正式打包前，应自行决定移走临时分析产物，或在另一次授权修改中加入排除规则，避免将临时报告、检查依赖等带入发行包。

本次完成时的修改范围核对结果保存在 `scope_verification.json`。核对以 SHA-256 比较基线与最终内容，排除本次允许写入的 `Temp` 和 Git 元数据目录；覆盖工程源码、配置、已有构建文件以及可读取的 IDE 文件。
