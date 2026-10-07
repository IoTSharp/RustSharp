# P1 剩余证据 I/O 截止检查

[English](p1-remaining-io-clock-v1.md) | 简体中文

## 修复与验收

本修复为 ✅ 已完成：剩余证据 CLI 在每次读写前后、EOF 和哈希完成后、刷新后
及发布证明前检查单调十秒预算。取消回调延迟不能让已经超时的 I/O 获得接受。
保留既有联动取消、字节及读取次数边界、父级链接检查、独占临时对象归属和禁止
覆盖的发布规则。

候选 `a434f5bbdf74e3e2b81c368f9bf12a1fd36c20af`、tree
`be596561f77400d47ce97493c4796cc290107140` 绑定 703 个源码输入。
SDK 10.0.401 Release 零警告、零错误。一项极小试跑、全部十项映射控制和九项
实际 Windows CLI 控制均通过。NativeV5 执行全部 28 个生成用例：26 个精确
rustc 匹配及两个继承的冻结差异，失败、阻塞和跳过均为零。Drop 物理证据验证也
通过。这些差异未获得新的阶段批准。

三项编译后检查器控制通过，包括实际时钟已经超时而 token 尚未取消的情形。
该工具调用编译后的私有检查器，未在实际 I/O 期间注入计时器延迟。正向 harness
CLI 控制使用明确保留的 `e773869314e5d7ebb51f035820bc75bcfcc1fdc5`
回归输入；它不是当前候选上的完整 harness 执行。

[验收归档](evidence/p1/remaining-io-clock-v1-archive.json) 绑定报告、程序集和
资源观察。原始报告保留于 `artifacts/p1-supervision/remaining-io-clock-root-v2/`；
生成输入及程序保留于 `artifacts/p1-drop/remaining-io-clock-root-v2/`。
保留 `remaining-io-clock-root-v1/` 中初次 CA1068 构建失败。

## 剩余阶段证据

🚧 进行中：实际 I/O 计时器延迟注入、独立后代进程约束、同一推送 SHA 的
Windows/Linux NativeV5 ILVerify/AOT 回执及 joint 门禁。注册数 1203 是清单，
不是完整执行。六个 P1 阶段门禁均保持开放；本修复不关闭任何 P2 叶项。
