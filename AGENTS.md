# Repository agent instructions / 仓库智能体约束

These rules apply to every agent working in this repository.

以下规则适用于在本仓库中工作的每个智能体。

## Bilingual project documents / 双语项目文档

- `README.md` is the English project front page and `README_zh.md` is its
  Chinese counterpart. `ROADMAP.md` is the English roadmap and
  `ROADMAP_zh.md` is its Chinese counterpart.
- Any change to one document in a pair must update the other document in the
  same task and change set. Neither language may be left stale or treated as a
  shortened secondary summary.
- Paired documents must communicate the same meaning and keep equivalent
  headings, facts, versions, commands, code blocks, paths, links, milestones,
  task IDs, status markers, and acceptance criteria. Wording may be idiomatic
  in each language.
- Keep a visible language switch at the top of every paired document, with
  working relative links in both directions.
- Before declaring a documentation task complete, compare both language
  versions. Check at minimum their heading hierarchy, tables, task IDs, status
  markers, code fences, commands, and link targets. Correct every unintended
  mismatch in the same change.
- Do not mark work complete when either member of a required language pair is
  missing, outdated, or semantically inconsistent.

- `README.md` 是英文项目门面，`README_zh.md` 是对应的中文版本；
  `ROADMAP.md` 是英文路线图，`ROADMAP_zh.md` 是对应的中文版本。
- 修改任一成对文档时，必须在同一任务、同一变更集中同步更新另一语言版本。任何一种
  语言都不得滞后，也不得被当作缩略的次要摘要。
- 成对文档必须表达相同含义，并保持等价的标题、事实、版本、命令、代码块、路径、
  链接、里程碑、任务 ID、状态标记和验收标准；具体措辞可以符合各自语言习惯。
- 每份成对文档顶部都必须保留醒目的语言切换入口，并确保两个方向的相对链接有效。
- 宣布文档任务完成前，必须比较两种语言版本；至少核对标题层级、表格、任务 ID、
  状态标记、代码围栏、命令和链接目标，并在同一变更中修正所有非预期差异。
- 只要任何必需的语言版本缺失、过期或语义不一致，就不得把任务标记为已完成。

## Task status markers / 任务状态标记

Use these emoji markers whenever task completion status is shown, including in
roadmaps, task lists, progress reports, and final completion summaries:

在路线图、任务列表、进度报告和最终完成摘要等所有展示任务完成状态的位置，统一使用
以下 emoji 标记：

| Marker | English | 中文 |
| --- | --- | --- |
| ✅ | Complete | 已完成 |
| 🚧 | In progress | 进行中 |
| ⏳ | Planned | 计划中 |
| ⛔ | Blocked | 已阻塞 |
| ❌ | Failed or cancelled | 失败或已取消 |

- Put the emoji before the localized status text, for example `✅ Complete`
  and `✅ 已完成`.
- Use the same emoji for the same task in both language versions. A status
  change must update both documents together.
- Do not substitute Markdown checkboxes or plain status words for these markers
  when reporting task completion status.
- Preserve literal status values in code, commands, machine-readable data, and
  quoted tool output. When prose presents such a value as task status, add the
  matching emoji without altering the literal value.

- emoji 必须放在本地化状态文字之前，例如 `✅ Complete` 和 `✅ 已完成`。
- 同一任务在两种语言版本中必须使用相同 emoji；状态变化时必须同步更新两份文档。
- 展示任务完成状态时，不得使用 Markdown 复选框或无 emoji 的纯文字状态替代这些
  标记。
- 代码、命令、机器可读数据和引用的工具输出中的状态原值必须保持不变；正文把这类值
  作为任务状态展示时，应添加匹配的 emoji，但不得改写原值。
