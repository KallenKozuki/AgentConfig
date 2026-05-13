# CLAUDE.md

---
globs: "**/*.cs"
description: "Unity C# 编码规范"
---

## 命名规范
- 公共字段/属性：PascalCase（如 `PlayerHealth`）
- 私有字段：camelCase（如 `moveSpeed`）
- 方法名：PascalCase

## 方法命名
- 事件监听回调：`On{EventName}`，忽略末尾的`Args`
- UI响应方法：`On{UIElementName}{EventName}`，可以适当简化 UIElementName，例如关闭按钮：`OnCloseButtonClick`，`OnCloseClick`都可以

## 变量规范
- 尽量避免使用 `var`，写明具体类型
- 使用 `new()` 简写代替重复类型（如 `Vector3 pos = new(1, 2, 3)`）

## Unity 生命周期顺序
在一个继承MonoBehaviour的类中，方法按此顺序排列（无需求的方法可省略）：
Awake → OnEnable → Start → Update → FixedUpdate → LateUpdate → OnDisable → OnDestroy


## 性能规范
- MonoBehaviour中：
1. Update 中避免使用 GetComponent，在 Awake/Start 中缓存
2. 严禁在 Update 中 new 集合类，创建一个缓存对象，Update 中先清空缓存对象，再添加元素
- 频繁创建/销毁的对象使用对象池
- 频繁使用的集合类（不限于 MonoBehaviour），创建一个缓存对象，使用时清空再使用

## 架构模式
- 编辑器工具脚本放在 Editor 文件夹下

## UI框架
- UI界面采用MVC结构，一个界面有三个对应的脚本：UIView，UICtrl，UIModel
- UIView：界面对象，由自动化脚本生成，绑定UI对象
- UICtrl：界面逻辑，遵守如下规范：
1. 严禁定义任何类内非方法成员
2. 需要在类的最后写两个region，分别是“UI交互”和“事件响应”，分别写UI的绑定回调方法和事件监听回调方法
- UIModel：界面数据模型，存放界面数据和仅和数据有关的逻辑，如界面数据转换，数据处理等

## 代码注释
- 公共方法，复杂逻辑，公共字段/属性，必须有 XML 注释（重载的可以不写）
- 私有方法，私有字段，注释直接用 `// `
- 用 `// TODO:` 和 `// FIXME:` 标记待办事项
