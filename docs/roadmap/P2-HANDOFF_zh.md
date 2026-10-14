# P2 监督者检查点

第43轮为 🚧 进行中。provenance cwd 修复组件为 ✅ 已完成：同一旧 DLL 从仓库根返回997条候选记录，从 capture 子目录返回0条；ls-files 同样受 cwd 限制，因此只修改 ls-tree 不足。验证现在先解析 Git 顶层目录，再统一执行全部命令与路径解析，保持4096文件上限及 tracked/untracked 输入检查。旧四格回归实际执行4/4，其中1通过、3失败；新鲜严格 Release 构建通过全部4/4，包括调用目录外的已跟踪改动和未跟踪编译输入。[修复证据](../evidence/p2/provenance-cwd-repair-v1.json)保留两份原件。真实仓库仍有脏状态，未作为干净候选验收。P1仍有六个开放门禁，P2保持8/101，所有阶段闭环标志仍为false。

[English](P2-HANDOFF.md) | 简体中文 | [P2 契约](P2_zh.md)

## 当前监督入口 — 2026-10-14

🚧 进行中：唯一新监督者为会话
`01a13917-a6b4-78e0-bd31-44600dd87b79`，绑定唯一 ACTIVE 的每30分钟
heartbeat `rustsharp-p1-p2-30`。已删除的旧 `rustsharp-p2` 不恢复。
持久派单与验收检查点为
`artifacts/p1-supervision/resume-root-v50/resumption.json`。

第38轮新鲜Windows formatter集成在限定组件范围内为 ✅ 已完成：严格solution/driver构建零警告/零错误，完整formatter59/59、契约18/18、真实Windows CLI8/8及原生信号1/1通过。[新鲜集成证据](../evidence/p2/formatter-fresh-native-integration-v1.json)绑定精确工作树源码/DLL/PDB/driver哈希、实际pending995、自然130、原生Join/close及outer3→0；三个精确进程身份已独立确认不存在。旧outer骨架proof字段仍为false，不用它们替代已核哈希child证据。此前provenance拒绝仍未定位，尽管实际编译GitAsync返回997项且匹配原始raw哈希；未提高cap。新clean-SHA CI/Linux、完整取消/containment及完整59/91叶项合同仍开放，P2保持8/101。

第38轮精确f463ac8/run38515250985/attempt1原件证据组件为 ✅ 已完成：8个job全部成功；14份原始ZIP的digest/字节数全部匹配GitHub，监督者独立重核2076/2076份留存文件（Drop300、source/backend1776）。两原生host各执行1215/1215个harness案例、32差异、24平台和60基线案例；source19/39PE、backend24个唯一原生格、Drop28=26个rustc匹配+2项固定差异、AOT28和callable7分别保留范围。[当前原件证据](../evidence/p1/ci-f463-current-native-v1.json)确认目标double-panic abort在当前Linux为body/owner/bad，Windows为body/owner。原件parent-exit-only containment、未批准语言差异及metadata/witness深核仍开放。P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第36轮精确f463ac8/run38512774036/attempt1文档CI原件对账为 ✅ 已完成：两份原始ZIP digest与大小均匹配GitHub；各原生宿主991条源码前后一致、6份DLL/PDB实物哈希一致、8个真实workspace子进程及20项API全通过，共56/56。[CI原件证据](../evidence/p2/doc-ci-f463-originals-v1.json)将真实job/OS/attempt API溯源与原binding字段分开保留；CI未运行publication21，未上传临时fixture/页面原件，parent-only/完整containment/运行FD缺口仍保留。历史afb0988的Linux NativeV5原件现确认28=26+2，目标rustc/CoreCLR/AOT均实际body/owner/bad并abort；不提升为当前SHA验收，也不批准legacy-v4差异。当前f463完整P1手动CI已派发run38515250985，仍在运行。Formatter v10已出现真实CTRL、pending995、原生Join/close和自然130且子进程案例通过，但外层读取正在退出的已知driver时仍失败，不验收完整案例/叶子。检查点v40；P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第35轮稳定发布身份限定组件为 ✅ 已完成：本机SDK10.0.402严格全构建零警告/零错误，原生Windows与Ubuntu各通过publication21/21、命令API20/20、真实workspace子进程8/8；98/98已执行，失败/跳过/未运行均为0。[精确组件证据](../evidence/p2/documentation-stableidentity-v1.json)绑定10项源码、6份DLL/PDB、全部有序报告和16个子进程身份，并保留此前Linux20/21失败。Linux采用持续持有O_PATH描述符与原生稳定身份；替换oracle通过真实搬移原件和新建空目录证明替换，不操作时间戳。完整叶子、同进程运行FD泄漏证明、固定SDK400的新精确SHA CI及原始containment仍分列。Formatter v7源码补齐PID身份交接缺口，待监督者严格构建/原生验收。P1目标Drop案例不在d2源码/后端矩阵中；NativeV2实现已存在，须先对账历史v5原始证据再决定新执行。检查点v39；P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第34轮仍为 🚧 进行中。精确PID63500资源查询组件为 ✅ 已完成：12:02:22独立原生OpenProcess返回87，与单PID查询不存在一致；历史UNKNOWN与Job5→0分别保留，不编造退出时间/退出码，不执行kill。[限定范围的监督复核](../evidence/p1/conhost63500-root-review-v1.json)保留两份原始收据。生产稳定身份源码已集成但未提交：本机新观察到SDK10.0.402，严格全构建零警告/零错误，Windows publication21/API20/workspace8全部通过。真实Linux publication已执行全部21项，20通过/1失败：旧替换目录测试前置条件比较.NET创建时间，在产品负向断言前失败。Linux API20/workspace8为NOT_RUN；不验收、不提交生产源码。头文件首次超时/自动变量错误及无效patch均保留。Formatter v5证明HWND归driver，而其自有job有独立conhost；v6改从真实job核身份仍仅源码。P1 NativeV2已有Linux析构清理分支，精确profile/案例执行仍未核。检查点v38；P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第33轮仍为 🚧 进行中。Linux目录身份诊断组件为 ✅ 已完成：直接statx最小程序通过严格零警告/零错误构建，真实Ubuntu /tmp三次采样3/3；设备号/inode/类型/原生出生时间保持固定，创建子目录后.NET CreationTimeUtc随ctime变化。[原生机制证据](../evidence/p2/linux-directory-identity-native-v1.json)保留精确源码/二进制/头文件/原件传输哈希及短stat竞态失败。这支持RSTD2001假说，但生产稳定身份修复与精确CI8+20回归仍为NOT_RUN。新WSL capture通过严格构建及精确21字节printf，仅属于监督设施。真实CLI和driver严格构建在保留超时/还原/AOT失败后通过，但实际formatter案例因无法确立独立私有conhost PID，在CLI启动前失败；CTRL/pending995/自然130/完整59/91仍未证明。检查点v37；P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第32轮仍为 🚧 进行中。精确 d2ee24f/run38494398708/attempt1 的留存文件组件为 ✅ 已完成：1776个唯一留存清单文件（每个RID源码785、后端103）全部与物理哈希记录对齐，缺失/不一致/未核均为0。监督者独立关联完整分母，重核四份原始报告与此前未核的两个第39个wrapper PE。[限定范围的监督复核](../evidence/p1/d2-retained-manifest-root-review-v1.json)保留手工MIR/源码区别、外平台后端单元格和未获批准的Linux第三项Drop差异；不证明后续HEAD或关闭P1。171b1be/efc2dce/8e5383e已推送。Linux身份最小程序在保留IL2026/IL3050和CA1852失败后，通过零警告/零错误构建；两次WSL启动失败已保留，真实Linux执行仍为NOT_RUN。始终加引号并非已证实的WSL原因。稳定Linux身份补丁仍未应用，等待实际statx头和复现。Formatter driver的进程/源码/线程出生字段检查已冻结，但生产原子probe/构建/原生信号验收仍为NOT_RUN。检查点v36；P1六门禁仍开放，P2保持8/101，两个完整闭环标志仍为false。

第31轮仍 🚧 进行中。新capture严格构建及已知NUL／非UTF8二进制微测通过，原生Job2→0；首轮quota1失败及全部旧analyzer失败均保留。提交171b1be保存精确源码字节并对账7个原始ZIP digest。真实d2ee24f／run38494398708／attempt1现联合24个唯一native backend格，各host12 passed／12 foreign-blocked保留；两RID source各19/19，95项独立物理hash（5ZIP+14backendPE+76sourcePE）全部一致。每RID第39个source PE仅有原生CI验证记录，本批未独立重hash；source／manual-MIR类型及未批准Linux第三Drop差异保留，不证明后续HEAD／完整P1。真实45f9 docs run38498734029失败：Windows8+20通过，Linux workspace6/8，两项RSTD2001 staging-ancestor失败，API20为NOT_RUN。两RID各977源码记录前后一致、6份DLL／PDB hash吻合。CreationTimeUtc身份假说待真实Linux复现，未削弱任何身份guard。Probe原子发布补丁已保存但未应用／运行。检查点v35；P1六门禁开放、P2保持8/101、两项full-closure仍false。

第30轮仍 🚧 进行中。待推送983b765／45f9e22已于10:00推送并触发documentation native CI，当前run／结果尚未读回。Windows native-open候选以已有BasicAsync黄金字面量通过2/2实际基础CLI检查；此前空函数预期是监督者oracle错误，其1/2失败已保留，不能据此验收取消／CTRL／完整59／91。实际CLI driver、probe源码及精确patch已保存，生产尚未应用，ready原子发布／conhost／嵌套Job及完整native单例仍需复核。新原始字节capture helper已有实物，但首次严格构建失败10个analyzer；未关闭任何analyzer，原始archive仍0、native24格仍未对账。当前v34检查点保留全部失败及资源缺口。P1门禁仍开放，P2保持8/101，fullP1Closure／fullP2Closure仍false，唯一heartbeat保持ACTIVE。

第29轮仍 🚧 进行中。精确d2ee24f的expanded run38494398708 attempt1回报success，但jobs接口返回HTTP500，尚未对账任何原始archive／native24格，P1门禁仍开放。未鉴权API403及拒绝的exit0／无body helper尝试均保留。Windows open owner9A06D4DA／FormatterCommandFD46F6B0通过新严格0警告／错误构建，进程21→0回收；实际CLI driver／pending995／CTRL／59／91均NOT_RUN，basic验证被09:41截止拒绝且无运行回执，native源码仍未提交。documentation Windows／Linux8+20 workflow及严格source／case／SDK／DLL／PDB binding驱动经parser／源码审查提交于983b765；推送被09:48截止拒绝，因此真实CI仍NOT_RUN。parent-exit-only工具不证明完整子树containment。本地检查点为v33，P2保持8/101、fullP1Closure／fullP2Closure仍false，唯一heartbeat保持ACTIVE。

第28轮workspace CLI组件在 `d2ee24f` 的限定范围内 ✅ 已完成：全部8个真实child案例、20个command-API回归及17个保留实际CLI案例通过，旧5页hash全部一致。[精确组件证明](../evidence/p2/documentation-workspace-cli-v1.json)保留首轮cap16失败及完整回收后有界17重跑。默认virtual拒绝、dependency／feature诊断、existing-target保留、byte budget和真实child timeout130均不代替CTRL+C／阶段验收。P2-08.03及完整12/91仍开放。

推送后已读回精确远端 `d2ee24f44e850bab14a02670b6da7e07a8fd67c1`。手动P1 expanded run38494398708现以此SHA在原生Windows／Linux执行；09:13时为 🚧 进行中。Roadmap38494102292及Linux AOT38494102314通过，Windows38494102359与platform38494102473仍进行中。此前精确c4的4条workflow成功，但本轮尚未对账其原件。徽章或旧108 profile不关闭P1六门禁。覆盖审查保留6个backend family／24个唯一原生ILVerify／AOT格；各历史本地report为12个local passed加12个foreign blocked。Map52及历史source19／39PE不替代当前执行。Linux unwind-own-drop-body-failure第三项差异仍未获语言批准。

Windows formatter源码open集成仍 🚧 进行中及NOT_READY：新native owner／Windows opening seam已冻结，Unix／其余读取逻辑不变。short-name alias／正常错误兼容仍需审查，真实CLI driver未保存，build／59／91／nativeCLI／CTRL signal检查全部NOT_RUN。未重复已验收4e17072 standalone基础组件。拟独立documentation Windows／Linux8+20 CI workflow未创建：两次worker写窗均在保存前截止，既有P1 workflow／product未改。worker实现停点保留于v32。P1六门禁仍开放，P2保持8/101，fullP1Closure／fullP2Closure仍false，唯一heartbeat保持ACTIVE。

第27轮真实FirstChance诊断在 `e064897` 的限定范围内 ✅ 已完成：Hook／controller严格构建及单次精确instrumented target取得4条body执行前的HostException／原生错误6栈。PID／native birth／原始全串命令／Hook SHA／MVID一致；安装产品078a的精确ManagedEntrance catch路径返回初始0。DETACHED到buffer的OS边仍为推断；原tiny／body／identity／cancel6／retention2／Linux12验收 ⛔ 已阻塞。[真实异常栈](../evidence/p1/firstchance-v5-actual-hostexception.json)保留body失败、flags524300／helper0／caps，以及held2→Job0／环境5项恢复。

普通文件同步open基础组件在 `4e17072` 的限定范围内 ✅ 已完成：真实兼容readonly BATCH hold、thread I/O pending、break signal、CancelSynchronousIo、原生995、native thread exit／Join及source／file／event／storage／thread／privateJob1→0回收全部1/1通过。[真实open证明](../evidence/p2/native-open-cancel-foundation-v2.json)不证明生产CLI／CTRL+C或完整59/91；未重复已验收e07d099。

Workspace CLI仍 🚧 进行中：显式 `rsc doc <Cargo.toml> --workspace` 保留默认virtual-workspace拒绝。新8个child案例及当前受影响20／17回归均NOT_RUN：修复CA2016／IL3000后，新完整solution构建0警告／错误，但08:48:10完成时已过新运行截止。候选源码／共享注册／help保留未提交。下一轮先核v31的精确3源码hash，再执行完整8／20／17分母后验收。root基础设施cap3构建拒绝已回收；修复solution使用有界32个累计进程，实际21→0，原fixture caps不变。第一次构建失败的原始logger文件被第二次构建覆盖；其owned回执／tool输出仍保留，已记录归档缺口。其他原件及analyzer失败日志均保留。

推送后已读回远端 `c4bc58a55e45bd3ba41bf29be51a1866b84a479c`。Roadmap run38490831640通过；08:33检查时platform38490831728、Windows38490831707及Linux AOT38490831681仍进行中。新e064897／4e17072仅在本地，不声称新HEAD的CI闭环。P1六门禁仍开放，P2保持8/101，fullP1Closure／fullP2Closure仍false，唯一heartbeat保持ACTIVE。

第26轮workspace documentation API组件在 `5b8d699` 的限定范围内 ✅ 已完成：
新构建0警告／错误，全部7个真实Cargo／member／source／atomic文件系统案例及
受影响的21个publication、20个command函数、7个depth和12个source-page案例通过。
已修package-relative href校验，旧CLI的unsupported-workspace语义不变。
[真实workspace证明](../evidence/p2/documentation-workspace-v1.json)保留CLI workspace选择、
dependencies／features／multi-target／cross-package symbols及完整12/91范围缺口。
精确050ad0f的4条CI workflow成功，原archive7 digest及aggregate input6 hash／SHA
在 `e2e74a1` 对账，见[当前冻结CI证明](../evidence/p1/ci-050ad0f-original-reconcile.json)。
此profile的108案例不能关闭P1六门禁，也不能证明新未推送源码。

FirstChance v3严格controller构建因226个RSP token中的10处陈旧intermediate lease
❌ 失败，真实原件及失败cleanup证明均保留。v4两次严格构建／SARIF0通过，真实
target输出了初始化journal：managed CommandLine指向pwsh.dll，而精确OS／request
命令指向pwsh.exe，因此full-command guard在观察异常前拒绝。环境恢复现已通过，
body／init／exception验收仍为false。
[实际取证及guard](../evidence/p1/firstchance-v4-command-guard.json)保持原flags／helper／
caps不变。v5原生命令绑定源码已保存，但PowerShell alias冲突及随后截止检查拒绝
导致未完整冻结，状态 ⛔ 已阻塞，禁止运行。同步open cancel概念也仍NOT_READY，
attribute／权限／lease／driver／join缺口尚无native证明。P1六门禁仍开放，P2保持
8/101，唯一heartbeat保持ACTIVE。

第25轮真实普通文件kernel-IO基础组件在 `e07d099` 的限定范围内 ✅ 已完成：
单helper真实oplock请求返回STATUS_PENDING，取消后原生事件signaled且状态为
STATUS_CANCELLED，存储／file／event句柄关闭，fileID／hash／lease及独立子进程
回收复核通过。13-byte source作为已验证交付原件保留。
[真实基础证明](../evidence/p2/kernel-io-foundation-v1.json)保留CA1068构建失败，未声称
CLI pending／open／read取消或59/91验收。修复后documentation DLL的全部17个
真实CLI案例及5个页hash在 `050ad0f` 通过；
[真实子进程证明](../evidence/p2/documentation-depth-cli17-v1.json)保留精确source／DLL绑定。

FirstChance v2 Hook／controller严格构建SARIF0通过，但instrumented target仍无
body／init／event文件，环境恢复回执为false；该process-local环境随自有host退出。
[实际失败取证](../evidence/p1/firstchance-v2-no-init-failure.json)保留完整held2／Job0
回收。v3初始化journal／null恢复源码仅为partial，冻结被07:45:30截止检查拒绝，
Observe cwd guard还需补回，状态 ⛔ 已阻塞，禁止运行。Workspace实现／link补丁／
7个测试draft仅保存于artifact，build／runtime全部NOT_RUN。远端050ad0f已独立
读回；检查点时Roadmap CI成功，其余3条仍进行中。CI9d108仅为历史冻结证据。
P1六门禁及P2的8/101计数均不变，唯一heartbeat保持ACTIVE。

第24轮documentation深度组件在真实3/7失败后限定范围 ✅ 已完成：glob alias
现在保留各自canonical item，带标签链接独立解析destination／title并HTML编码、
诊断不安全scheme。新构建零警告／错误，全部7个depth案例及受影响的12个source-page、
21个publication和20个command函数案例全部通过，未过滤；原失败保留。
[实际修复及回归](../evidence/p2/documentation-depth-v1.json)不声称新的17个子进程／
remote-SHA或完整documentation叶项门禁通过。

精确9d43072的4条CI workflow成功。7份原始archive均匹配GitHub digest，6项
aggregate input hash／SHA已对账；每个原生平台12个platform、16个differential和
26个regression案例通过，合计108。6/6 aggregate含0条validation record，仅覆盖
冻结profiles，不能关闭全部P1六门禁。
[原始CI对账](../evidence/p1/ci-9d43072-original-reconcile.json)保留原报告及所有更大
范围缺口。FirstChance hook v2目前只有2个core源文件，controller／MVID环境／
lease／RSP／manifest绑定 ⛔ 已阻塞，build／native全部NOT_RUN，真实target cause
仍UNKNOWN。Kernel-IO v3 driver已保存，但build／native仍NOT_RUN，report写失败
后的closure状态还需修，未将helper pending请求算作CLI取消。P2保持8/101，
P1六门禁仍开放，唯一heartbeat保持ACTIVE。

第23轮单包documentation CLI组件在限定范围内 ✅ 已完成：新solution构建
零警告／错误，全部20个真实command函数案例通过，另行冻结的17个真实CLI子进程
案例均匹配退出码及发布／原件文件效果。5个已发布页hash与真实API／渲染字节一致；
构建前已LF规范化，source／DLL／PDB绑定均保留。
[实际实现及证据](../evidence/p2/documentation-cli-v1.json)将3个callback取消／deadline
案例与子进程signal证明明确分开。Workspace／dependency／feature／multi-target
选择、更新及原12个完整documentation scenario／tooling91仍开放，P2保持8/101。

第23轮P1启动调查找到与本机PowerShell源码精确匹配的早退出机制：managed entry
遇特定控制台HostException错误6／1236返回初始0；成功formatter使用NO_WINDOW，
失败诊断使用DETACHED。这给出了body缺失／双流空的合理解释路径，但尚未取得
目标exception stack，真实runtime cause／caller仍为UNKNOWN，P1六门禁仍开放。
[源码机制及限制](../evidence/p1/powershell-body-source-mechanism-v1.json)保留原flags／
helper0／caps／全部分母。普通文件oplock草稿为 ⛔ 已阻塞／NOT_READY：lease／FileID／
完整父归属／native driver及超时回收均不完整，build／native全部NOT_RUN，未声称
CLI pending-IO／cancel已证明。已验收真实CTRL+C组件继续保留；唯一heartbeat保持ACTIVE。

第22轮文档首次发布组件在限定范围内 ✅ 已完成：报告写入器改用显式
Utf8JsonWriter，无反射序列化或警告抑制。新独立solution构建零警告／错误；
全部21个真实Windows文件系统案例执行并通过，failed／not-run均为0。Root独立
复核全部21个目录、中途reparse替换后的外部原件保护、精确source／DLL／PDB
hash及22个进程身份，均无自有遗留。
[实现及真实证据](../evidence/p2/documentation-publication-v1.json)仅覆盖向不存在目标
首次整批发布。此前两次真实构建失败继续保留。本次构建前已LF规范化，当前源码
hash保持不变。可移植检查无法关闭所有并发filesystem TOCTOU窗口。
CLI／workspace、已有目标更新及完整documentation profile仍开放，因此P2保持
8/101，P1六门禁仍开放；唯一heartbeat保持ACTIVE。

第22轮新formatter opt-in控制组件在真实Windows限定范围内 ✅ 已完成：同一新
CLI及Helper24／Launcher21先无信号、后CTRL+C；实际注册handler的ready回执
绑定nonce／PID／birth／source lease。GenerateConsoleCtrlEvent返回1、event0／group0，
CLI未被native termination而自然exit130。两次完整6角色图、事件对账、源码保护及
Job0／句柄清理均通过。Candidate36f699仅为显式private source snapshot；编译
metadata仍为43859c4，dirty worktree match保持false。Git规范换行不代表新的
source／PDB／remote-SHA执行门禁。
[真实控制证据](../evidence/p2/formatter-v26-real-control.json)保留Capture25静态调用
失败及精确raw build／snapshot／DLL／PDB。Pending kernel IO、完整stdio／upstream
祖先、原default-handler案例、formatter59／tooling91及完整叶项验收仍开放。
P1 host-command v3 held-terminal transitions在78cdc29的限定范围内 ✅ 已完成，
但body ❌ 失败：pwsh exit0、双流空、body文件缺失。
[真实terminal／body证明](../evidence/p1/host-command-body-v3-terminal-failure.json)
保留v2 image-query失败；原flags／helper0／caps／全部分母不变。下一单包
documentation CLI artifact仅为partial，tests／build全部NOT_RUN／NOT_ACCEPTED。
P1六门禁及P2的8/101计数均保持不变。

第21轮Helper24严格构建在 `01c56bd` 的限定范围内 ✅ 已完成：显式private访问
修复Helper23真实CS8796失败；精确compiler response、全部3个原生身份／退出、
所有级别SARIF诊断0及Job0／句柄关闭均通过。
[实际构建及保留的Helper23失败](../evidence/p2/formatter-v24-helper-strict-build.json)
绑定新DLL `634F255A22F92D6A4146134AC7B0A14BD6F493E85071DEC84B808EE539CCBF96`。
Launcher21／Helper24已包装并绑定真实source10；新product／Git绑定及同一二进制
无信号／CTRL+C仍为NOT_RUN，未验收完整叶项。独立PowerShell `-Command`诊断严格
构建通过，但原生tiny在 `bca4c2c` ❌ 失败：退出中的子进程NT command-query返回
`0xC000010A`，没有body／result回执证明执行。完整2个held身份／退出及Job0清理
保留于[实际失败](../evidence/p1/host-command-body-diagnostic-v1-failure.json)。
原fixture、flags、helper0及全部分母不变。

Documentation publication仍 🚧 进行中：真实mid-stage reparse祖先缺口已修，
保留原20并新增1个真实多源案例，固定21。首次新solution构建因2项CA1859失败，
私有签名修复已保存；第二次构建因runner CA1869／IL2026／IL3050 ❌ 失败，未抑制。
全部21案例NOT_RUN，05:44:05 runner修复截止检查在任何修改前拒绝迟写。
新publisher／tests／runner／共享test入口均未验收、未提交。
[两次真实构建失败](../evidence/p2/documentation-publication-round21-build-failure.json)
保留原件及精确source hash。可移植祖先检查无法关闭所有并发filesystem TOCTOU窗口；
已有目标更新及CLI／workspace集成仍开放。独立复核6份command回执、39个精确进程
身份，均无遗留。P1六门禁仍开放，P2保持8/101，唯一heartbeat保持ACTIVE。

第20轮comparisonv3启动诊断 ✅ 已完成：3个JSON DTO字段已修且原生ABI不变，实际严格Build／SARIF0及完整2个held进程身份／退出。真实body ❌ 失败：pwsh exit0、双流空、无子marker、NaturalRootRuntimeExecuted0。[实际诊断／失败](../evidence/p1/detached-comparison-v3-startup-failure.json)保留于30cf996，原NO_WINDOW／helpers0／cap2及全部分母不变。Helper22已匹配独立审查的精确213-token RSP且nativecompiler3已清理，但因10项SARIF note ❌ 失败；[失败原件](../evidence/p2/formatter-v22-helper-notes-failure.json)保留于36c5bb6，未执行新CTRL+C runtime。Helper23 API／finally修复已保存冻结，build／runtime NOT_RUN。Documentation initial-publish源码及测试已保存；审查发现目录身份及未知staging提交缺口，新源码已修，保留原18并新增2个真实案例、固定20。发布build／tests全部NOT_RUN／NOT_ACCEPTED，未stage任何新候选产品；已有目标更新／CLI／完整叶项仍开放。P1六门禁开放、P2仍8/101、唯一heartbeat保持ACTIVE。追加交接写入超时被拒且未修改，resume-root-v24最终原件／hash检查点绑定这些新候选。

第19轮源码页组件在commit1c7d24f的限定范围内 ✅ 已完成：真实新solution构建0警告／错误、原12扩充案例全通过。原Expected7断言保留，使用parser真实Visibility.Span，另核actualfn11；初始CA2249及11/12失败均保留。证据披露执行后仅CRLF→LF源码规范化及精确source／PDB绑定限制，未声称当前SHA阶段门禁。CLI／atomic／workspace及完整叶项仍开放。Launcher21 strict3／SARIF0在55ea12f ✅ 已完成；Helper因精确RSP不匹配 ❌ 失败，原生退出证明secondary失败保留、Job3已回收。新CTRL+C为NOT_RUN。Comparisonv2因3个nuint DTO不能JSON序列化而NOT_READY，v3写入被拒、未构建候选。P1六门禁仍开放，P2仍8/101、唯一heartbeat保持ACTIVE。

第18轮实际回执恢复：Observer18严格构建 ✅ 已完成（3个原生身份／退出、
SARIF0／Job0），commit9002906。Launcher20虽exit0仍因22项SARIF note而
❌ 失败，Helper／CTRL runtime NOT_RUN，commitd2f3b6d。独立DETACHED tiny
❌ 失败：outer2／inner1、双流空、子marker缺失及短子身份回执不完整，commit93c7cbc。
原NO_WINDOW失败及cap2／inner1／helper0不变。源页代码／扩充原12案例已冻结，
但仍NOT_READY，04:17进程创建前guard拒绝后build／tests NOT_RUN。上轮最终
台账／docs写入被拒且未修改；真实raw回执及这些精确commit优先于过期worker-running
字段。第19轮开始实际验证，P1六门禁及P2的8/101叶项计数不变。

第17轮documentation基础组件在限定范围内 ✅ 已完成：独立新输出的SDK10.0.401
全solution Build0警告／错误、源码前后hash不变，实际12／12个seed均通过，
failed／not-run0、未过滤。case报告耗时82.252毫秒；native outer0.334秒，
Job1→0、句柄关闭。[基础实现及实际证明](../evidence/p2/documentation-foundation-v1.json)
已提交于 `51367dd3bb53d0f0f21ba218f6f4a6c63524dfd4`。12个完整scenario的深度
仍开放：source pages／真实anchors、CLI／workspace、完整Markdown／reexports
及预算边界。P2仍8/101叶项 ✅ 已完成。保留不明owner输出Junction，使用新
OutDir绕开，未删除；首次真实IL3000／CA1861编译失败记录保留。

Formatter v18源码及observer严格包装已冻结；IL3000未抑制而已修复，产品
solution已编译，但必需的严格Launcher／Helper双组件驱动仍缺失。03:43窗口
写入于03:43:13被拒，未执行新formatter无信号／CTRL+C；包装、source／Git快照
绑定、kernel I/O及完整叶项验收仍开放。精确版本PowerShell调查保留4份官方源码／
网络回执及具体初始化路径，但console allocation调用方仍 `UNKNOWN`；未改
flags／helper／fixture，未生成新native候选。研究超时4.692秒已记录，最终
packet冻结被03:37 guard拒绝。P1六门禁仍开放。下一doc source-page patch
已派入独立artifact目录，03:47之后无新runtime。

第16轮拒绝溢出的原生诊断组件 ✅ 已完成：v28严格Build／SARIF0，全部3个
真实held身份／image／hash／directparent、命令历史及有效原生退出证明，诊断
0.086秒／error0、Job0，独立核22个PID均无遗留。原tiny仍 ❌ 失败，cap2
对应actual3；innerroot1／helpers0及全部原分母不变。实际console allocation
调用方仍为 `UNKNOWN`。[原拒绝诊断](../evidence/p1/containment-v28-overflow-diagnostic.json)
已提交于 `613c0ff08fc5176822bbde9e2bd97bd79c580af6`。

第16轮documentation v2包含实际model／renderer修复和12个真实parser／
sourceMap／model／render测试seed，已在当前4096上限内注册。整个candidate
build ❌ 失败：Semantics输出目录是指向缺失临时目标的Junction，依赖复制
MSB3026／3027／3021达到10次重试。现有不明owner链接保留，下轮使用独立
新build输出。[产品构建失败证据](../evidence/p2/tooling-round16-build-failure.json)
保留真实native Job9清理及失败命令回执。未执行新doc案例，tests／runtime仍
`NOT_RUN`，CLI／source pages／Markdown／reexports／完整预算边界仍开放。
测试于03:14:45在明确延长至03:16的窗口落盘；旧03:10／03:12 guard拒绝均保留。

Formatter v18已实现兼容Run及opt-in PID／nonce／handler-ready探针；root接入
Program dispatch及CLI AllowUnsafeBlocks。完整helper／observer manifest／build／
新candidate包装和正确新cwd literal仍为 `NOT_READY`，未授权新formatter runtime。
03:14包装写入于03:14:42被拒，未迟写。全部产品／共享入口改动仍未验收、未提交。
P1六门禁仍开放；P2仍8/101叶项 ✅ 已完成。

第15轮开始 `P2-08.03` 产品实现。冻结tooling91范围（49个corpus加42个scenario）
保留全部12个documentation scenario。真实syntax文档属性及原UTF-16源码映射
均可用；新增model／renderer草稿 🚧 进行中且为 `NOT_READY`，build／runtime／
tests均 `NOT_RUN`，未接共享CLI或测试注册。当前local-link分类、source-href安全／
span编码、预算诊断及完整输出检查仍需修；trait／reexport／associated items／
fields／variants尚不完整。后续修复命令被02:42硬code guard在任何编辑前拒绝。
未验收任何doc案例或P2叶项，未审产品文件保持未提交。续接缺口：
`artifacts/p2-supervision/documentation-v1/IMPLEMENTATION-HANDOFF.json`。

第15轮原native v27严格编译组件 ✅ 已完成：SDK10.0.401、编译器exit0、
SARIF0及精确冻结源码／runtime绑定。实际original native tiny ❌ 失败：
outer成员上限2，却在admission前观测dotnet50932 → pwsh65320 →
conhost69104、total3。无body receipt；inner root1／helpers0与原分母
 tiny1／identity1／cancel6／retention2／Linux12均不变。finally记录
Active／Assigned／Listed0、cleanup error0；root独立核8个精确PID均无遗留。
额外helper的held identity／image／hash／history证明及明确scope决议仍开放，
未提高上限。[严格编译与native tiny绑定证据](../evidence/p1/containment-v27-build-tiny.json)
已提交于 `1bc7717bfd1be7f03eab2060ae3e853a2579d41d`。

第15轮formatter工作 🚧 进行中且 `NOT_READY`：可选callback位于真实托管
CTRL+C handler订阅之后，但Program接线、精确PID／nonce lease校验、私有console
readiness handshake、新helper、新candidate build及有意义tests均缺失。
启动时序仅为假设。两参数Run API兼容性修复被02:42 clock guard拒绝；
未测源码改动于02:36:32写入，超过02:36内部截止。未重新解释signal失败；
保留未提交的partial源码和v16失败。P1六门禁均开放，P2仍8/101叶项 ✅ 已完成。

第14轮不可变 REPORT4 v3 限定原报告对账组件经真实tiny／full后 ✅ 已完成：
4份报告、52条作用域记录、78份源码PE及14份后端PE绑定、595项检查／0问题，
3个报告／格／替换负控被拒绝。v2 的24个空哈希失败保留；v3 按精确路径将
静态inventory的 `sha256` 绑定到独立已验收旧证明的 `freshSha256`，未重哈希
大型payload。source原生执行flag及原报告run／attempt仍为 `UNKNOWN`；
每后端12个外平台blocked及完整进程／语义／当前SHA缺口继续开放。

原native v25编译保留精确编译器身份／退出及Job清理，但11项SARIF note使
严格阶段 ❌ 失败，无binding或original tiny；v26修复仍不完整，为 `NOT_READY`。
后续真实链是original native tiny1后接cancellation6／retention2及Linux／joint12；
通过实际生成程序核源码alias／call ABI与imported-panic行为；再接同候选九输入
joint及12个负控。具体案例与固定分母保留于
`artifacts/p1-supervision/report4-reconcile-v3/NEXT-RUNTIME-CHAINS.md`。

Formatter v16 严格构建为SARIF0；同一新程序集的无信号控制在21.761秒通过，
六个role身份及借用资源回收已绑定。真实CTRL+C针对130契约 ❌ 失败：Event0／
Group0发送成功、RawReturn1／LastError0，但真实CLI自然退出3221225786
（0xC000013A），不是130；helper／controller／driver均exit1。TerminationReturn为
null、Wait0、拥有资源回收为true；完整身份／event／cleanup证据不能把运行失败
计为通过。stdio／系统祖先、kernel I/O、59／91案例及全部叶项／门禁继续开放。
Native source26仍为部分 `NOT_READY`，未新build或runtime；v16全部trial、lease
与失败均保留。

第13轮 `P1-GATE.03.REPORT4-v1` 冻结四份原始 source／backend 报告、52条
作用域记录、每 RID 39份源码 PE，以及每报告24后端格，包含12外平台 blocked。
v1 要求所有报告都有true nativeExecution字段的假设 ❌ 失败：source schema2
没有该字段，仅backend schema1明确声明。不可变 v2 保留source的
`UNKNOWN_NOT_IN_SOURCE_SCHEMA`，原报告run／attempt为 `UNKNOWN`，通过已哈希
绑定的production-receipt CI字段确定运行身份。新header／hash／输出对账只核
数据，在此仍待root验收；完整语义、联合格witness闭环、完整后代及当前SHA
验收继续开放。本轮未执行候选程序或重跑已验收审计。

第12轮 `P1-GATE.04.MAP52-v1` 结构映射组件 ✅ 已完成：root tiny／full 验证
52条不同作用域记录（19／19个源码包及7／7个后端 witness），缺项／重复／
错误 group-RID 负控被拒绝。这是26种唯一 group／ID 用途，不是统一52案例
CLI 或完整 P1 语料。每份后端报告保留24格：12本机及12外平台 blocked。
报告哈希非空不证明物理字节或执行；全部门禁仍开放。

复用 v22 构建的真实 managed3 控制在
`cb1d751c37aa56fda8cc39b2cdef84064bc57240` 中 ✅ 已完成，包括 PID／nonce／
hash、有效退出时间／wait、pending started1／completed1、nested cleanup全0；
CIM 核实四个记录 PID 均已退出。原 native 控制仍为 `NOT_RUN`。
Formatter v4 编译器／响应／退出证据保留，但严格阶段因5项 SARIF note
（CA1050、CA1822×3、CA1869）❌ 失败。新不可变 v14 和部分 native-cancellation
v23 在此尚无已验收运行结果。

Formatter v14 随后仅通过严格构建范围：warning／error均0、SARIF0、三个
编译器原生退出及cleanup0。Prepare 在原生 formatter 启动前 ❌ 失败：预载
baseline `Launcher` 与原 port 克隆的程序集同名但身份不同，Add-Type 拒绝
第二次加载。18份已复制文件保留，未形成manifest／config；formatterActualCLI0，
无信号为 `NOT_RUN`，01:15后不重试。CTRL+C、kernel I/O及59／91案例仍开放；
native23 仅有11份部分 typed源码，`NOT_READY` 且无运行。P1六门及P2的8／101
保持不变。后续须用全新 PowerShell 和不可变新版本并保留本失败。

第11轮由 `P1-GATE.02.LINUXDROP-v1` 负责保留原件对账，与继续进行的
containment 和 formatter worker 并行。NativeV5 候选
`afb0988bb74e698a685adde93bacdd2b82b8c932` 已记录 Linux
`unwind-own-drop-body-failure` 的 rustc／CoreCLR／AOT 输出均为
`body\nowner\nbad\n`，双重 panic abort、raw exit134。source／PE／runtime／
oracle／AOT／snapshot 六个物理哈希一致。三个相关 emitter／runtime／runner
Git blob 还与 `64021fb6b6ed65587538839c6aba592112717f92` 源码相同；
提交身份仍不同，源码相同不授予当前 SHA 的构建／运行／CI 或门禁验收。
测试变异负控中的相反轨迹是污染，不是新观测到的执行。历史 legacy
`p1-drop-closure-v4` 与 NativeV5 的28案例分开保留：后者为26项完全一致加
相同2项正常清理差异。本轮未做产品修复，也未重复已验收执行；原始 MIR／LIR
中间件及完整语义／joint／containment 证据仍缺失。剩余52案例范围与全部门禁
保持不变。证据：`artifacts/p1-supervision/linux-drop-repair-v1/`。

v22 编译器／原生退出构建组件经 root 单次真实构建验收后 ✅ 已完成：summary／
SARIF 诊断均0，三个原生编译器身份绑定并有有效退出 FILETIME，Job Total3 后
Active／Assigned／Listed0，全部句柄关闭、nested cleanup 无错误；监督者 CIM
核实四个记录 PID 全部退出。仅观察到控制台历史0x4，ForceV1 转换仍未证明。
00:44 启动 guard 在启动前拒绝 managed3（0 child／0 body）；`tiny`、
`pre-cancel`、`pending-cancel` 仍为 `NOT_RUN`，下一轮复用已验收 v22 构建。
v21 源码审查拒绝及 formatter v2／v3 `NOT_READY` 均保留；完整 containment
和全部门禁保持开放。

第10轮已派出三名 worker 负责互斥冻结范围：`P1-GATE.03.CISEM-v1`
（source 1572文件，每RID 19 packages/39 PE/785 retained；backend 234文件，
24 matrix cells/每RID 103 retained）、`P1-GATE.02.CONTAIN-v8`
（v7的11份冻结源码；tiny1、cancellation6、retention2及Linux/joint12），
以及 `P2-08.02.CTRL-v2`（真实Windows CTRL+C；59/91案例分母不变）。
监督者串行管理共享构建输出、最终验收、索引与每项验收后的独立提交。
worker完成声明仍待独立源码、运行证据及资源复核。

六个P1门禁与15个P2父项仍全部开放。最新路线图检查保持P1的85实施叶项
加6门禁叶项、P2的94实施叶项加7门禁叶项；P2仍为8/101叶项 ✅ 已完成。
本入口不声明当前切片、父项或阶段已通过验收。实际测试、失败、缺项、
提交及资源记录以持久检查点为准。

v29 真实保留字节审计通过10052项检查、0个问题，保留1806文件／144机器头／52案例及92 PE/PDB；绑定708候选Git blob及14个后端PDB文档。完整签名语义与全部阶段门禁仍开放，首次零长度blob失败及archive启动器资源缺项均保留，仍须监督者独立验收。

上一轮保留 formatter v12 两次解析失败：❌ 失败，未形成有效候选。
V11 声明审计草稿 ❌ 失败：源码冻结超过 worker 截止时间，解析报缺右大括号，
第三个真实字段字节负控尚未编写。源码 SHA256 为
`860E5624B2B9318C187530E48211853829049DD4EC00AD384C240CE760CA12B7`；
tiny 与 full78 仍为 `NOT_RUN`。固定 source 范围保持78 PE／88 value types／
76字段／56非零记录加22零记录。Containment v18 冻结 manifest SHA256 为
`528315543534DFFC4BCF9159672D895CB6301403AC457148EA5F617ED65AE9C3`；
受控构建 ❌ 失败：MSBuild 报0 warning／0 error，但 SARIF 保留5个 note 诊断
（CA1822×3、CA1862×1、CA1513×1），未满足冻结的零诊断要求。脚本 exit1，
未生成 `BUILD-BINDING`；managed `tiny`、`pre-cancel` 及 `pending-cancel` controls 仍为
`NOT_RUN`。已绑定三个 MSBuild／csc／conhost 身份及父链；最终私有 Job 为
Total3、Active／Assigned／Listed0，全部句柄关闭且无清理错误，监督者 CIM
回读未发现记录 PID 存活。不降低标准或删除诊断；新不可变源码须修复全部5项
诊断后再单次全新构建。结果保留于当前检查点。
这些草稿和失败记录未关闭任何 P1 门禁或 P2 叶项；六个 P1 门禁全部保持开放，
P2 仍为8/101叶项 ✅ 已完成。

第10轮 V12 tiny 在比较任何 PE 前 ❌ 失败：把 worker V10 报告哈希绑定到了
root 报告路径。两份报告原件保持完整；root 独立核实 emitter 文本相同，
均通过9项检查。不可变 V13 仅修改已核 root 哈希、版本与 packet。
限定声明组件经 root 验收后 ✅ 已完成：真实 tiny1 及3个合法单字节
TypeFlags／BaseType／FieldSignature 克隆被拒绝，原件哈希保持不变；
full78 PE／88 value types／76字段／56非零加22零记录通过1090项检查，
0 issues／0 pending。这是候选 `afb0988bb74e698a685adde93bacdd2b82b8c932`、
run `37710305713` attempt1 的证据。V13 源码冻结超过00:13截止28.3307573秒，
时间纪律失败单独保留。完整 ABI、递归字段作用域、泛型、backend24及全部门禁
仍开放。Containment v19 在原5个 note 修复后保留 CA2219 失败；v20 构建
在 held-Job 阶段 ❌ 失败，未生成 `BUILD-BINDING`，managed3 controls 为
`NOT_RUN`。Formatter v13 严格 builder 为 `NOT_READY`，缺响应文件、三个
编译器身份和真实诊断；11份冻结文件保留，build／runtime 为 `NOT_RUN`。
实际截止超时事实及监督者详细资源记录保留于当前检查点。

v20 因 console 命令变化期间的精确 held 身份变化而拒绝；完整身份层为 false。
保留的 csc／console 退出时间无效，process cleanup 保留 NT 命令查询错误
`0xC000010A`。私有 Job 回收达到 Total3、Active／Assigned／Listed0，root CIM
回读未发现记录 PID 存活；这些事实不构成完整身份或退出验收。监督者在00:16:51
启动 v20，超过自己00:16的停止新操作边界；该违约记录保留。
V13 限定声明组件已单独提交为 `1cd77ef`。
原 v15 formatter 无信号组件在 `099cad7` 中 ✅ 已完成。三个独立的新 PowerShell
进程分别捕获已核持久原生 host、准备不可变原 source10／Launcher 包、运行原
SOURCE-FROZEN 入口。实际 CLI1、driver exit0，以及六个精确角色的 held 身份／
映像／命令／父链／退出与 Job 事件已对账；Total6 最终 Active／Assigned／Listed0，
借用引用与原生句柄关闭，嵌套清理错误0，独立记录 PID 回读无存活。CLI 如实绑定
历史 `aee97ccdc2509fb64693631979fa4e93c3a31578`。没有发送信号；stdio 完整性与
上游系统祖先完整性为 false，真实 CTRL+C、内核 I/O、59／91语料、叶项及全部阶段
门禁仍开放。REPORT4 v2 与原生 v25 已冻结但为 `NOT_RUN`，01:45 guard 将新的试验
推迟；全部失败源码与部分 trial 保留。
## 历史检查点 — 2026-10-08 12:18

Windows 私有控制台夹具组件在提交 `fb90c4c` 中 ✅ 已完成：全新
SDK10.0.401 csc 编译零诊断，唯一真实无信号 tiny 通过。controller/helper/
真实 formatter CLI 自然退出0；严格 inner3/outer4 包含实际 conhost，归零并
关闭全部句柄，无清理/发布错误。root 核实五个记录运行 PID 全部不存在，
拥有的 fixture 已移除。保留 detached raw console0/error6、allocated1 和
final1；live 成员2记录为数组，未另存 live raw LastError 回执。便携验收：
`docs/evidence/p2/formatter-private-console-port-v1.json`。真实生产绑定为
`aee97ccdc2509fb64693631979fa4e93c3a31578` / tree
`4fd94a69ca4659fac8fc9be5e4283caf450df6d9`，不能作为当前 HEAD 验收。
本项不是仓库 Release/analyzer 检查。实际 CTRL+C、挂起 kernel 文件 I/O、
stdio 完整性及上游系统祖先仍未闭环；P2-08.02 仍 🚧 进行中，59/91 分母
不变。v8 失败与 v9 独立成功组件的原件均保留。

CI37710305713/候选 `afb0988` 缺少的四个 source/backend ZIP 现均完整保留
并记录传输哈希。v24 对账小回执和精确候选 manifest；expanded 实际
SDK10.0.400 与 production10.0.401 保持区分。v25 保留1572个 source 文件
（各 RID785、19包/39 PE 绑定）；v26 保留234个 backend 文件（各 RID103个
绑定保留对象）。物理哈希匹配，完整 verifier/header/语义与正式 joint 验收
仍未闭环。未执行下载程序。v7 containment adapter/driver/tiny 源码已冻结，
未编译/运行；身份失败时启动计数仅统计已验证 root。

P2 保持8/101 ✅ 已完成叶项；六个 P1 门禁及全部 P2 父项/阶段仍开放。
子智能体已停止新增工作并保存冻结检查点。root 核查记录的下载/编译/运行/
worker 进程身份；本轮收尾未杀进程，未删除原件或用户文件。续接入口：
`artifacts/p1-supervision/resume-root-v4/resumption.json`。用户最新要求本轮
收尾后终止 `rustsharp-p2`，即使 P1/P2 仍未闭环；不得自动恢复定时监督。
## 历史检查点 — 2026-10-08 11:44

同SHA Drop物理原件组件为 ✅ 已完成，绑定CI run
[37710305713](https://github.com/IoTSharp/RustSharp/actions/runs/37710305713)，
候选 `afb0988bb74e698a685adde93bacdd2b82b8c932`、tree
`387d2ae7af9bfbd1a963dff481de3968fb7c24ae`。Windows原3250项失败报告保留：
复用3242项通过检查及401哈希，余下八项过程记录已逐字段/UTC ticks核对通过。
Linux通过3261项检查及373物理哈希。每个平台完整保留28生成案例、
30 PE/ILVerify10.0.11、28 AOT、两个callable交付物/七案例及36阶段；
保留606个payload成员。原额外Linux unwind案例此次真实rustc/CoreCLR/AOT输出
及结果已一致，未批准新语义差异。便携归档：
`docs/evidence/p1/drop-original-bytes-afb0988.json`。

原生GUI夹具提交 `90e376e` 已推送并独立核实与远程master一致。其四个独占构建
GUID目录（共31对象）已回收；root复核均不存在，未终止进程，保留原源码/DLL/PE。
历史compiler父链/捕获缺口仍明确保留。Formatter v8修正的18文件嵌套manifest及
两轮SDK编译通过独立字节/元数据审计。唯一无信号tiny为 ⛔ 已阻塞：严格inner计数
发现helper、新建conhost及挂起CLI（三成员），outer Job记录四成员。
CLI未恢复执行且未发信号。两个held Jobs均归零；root另核全部五个记录PID不存在后
精准回收带lease夹具。原失败报告不可变。v9已冻结十个源码/契约文件，纳入console-host完整身份及计数；
仍需root源码审计、编译及真实运行。真实CTRL+C及内核待完成I/O取消仍开放；P2-08.02仍为 🚧 进行中。

双方expanded及production-receipt小ZIP均已持有，绑定同一CI SHA；首次Linux
receipt超时/partial与唯一完整重试同时保留。v23启动独立冻结审计范围。
source/backend payload、完整后代隔离、正式joint12、legacy/policy权限及
candidate-gate验收仍开放。P2仍为8/101个 ✅ 已完成叶项；全部六个P1门禁及所有
P2父项/阶段仍开放。从 `artifacts/p1-supervision/resume-root-v3/resumption.json`
接续；心跳保持ACTIVE。

## 历史检查点 — 2026-10-08 11:20

Windows隔离v6原生GUI夹具在其组件范围为 ✅ 已完成：真实tiny1及全部八项固定
生命周期控制通过，严格1/2/3成员计数，并独立证明未分配挂起root负控制退出。
夹具无CLR/CRT/console imports。归档：`docs/evidence/p1/windows-gui-lifecycle-v6.json`；
已跟踪源码：`tests/fixtures/processes/WindowsGuiLifecycle.c`。Compiler Jobs归零；
历史compiler身份/捕获限制及独占构建目录待回收仍明确保留。原取消6、保留2、
Linux委派及正式joint12仍开放。

双方大型Drop ZIP现已完整下载，按准确HTTP206 Range重组。Windows317文件/401哈希
核对3242/3250项；八项ISO时间精度差异仍待独立tick对账。Linux ZIP仍未解包。
未执行任何下载程序。Formatter v6唯一tiny在Helper/CLI执行前失败，因为内置编译器
使用随机私有程序集名。v7改用两轮固定SDK编译器，私有AssemblyDef/Ref闭包正确，
冻结17文件包；runtime0，CTRL+C及待完成异步I/O证据仍开放。P2仍为8/101个 ✅ 已完成
叶项；没有关闭P1门禁、P2父项或阶段。从
`artifacts/p1-supervision/resume-root-v2/resumption.json`接续；心跳保持ACTIVE。

## 历史检查点 — 2026-10-08 10:53

CI run [37710305713](https://github.com/IoTSharp/RustSharp/actions/runs/37710305713)
准确绑定已推送 `afb0988bb74e698a685adde93bacdd2b82b8c932`，全部八个作业
均为 ✅ 已完成。root下载四份原始ZIP：Windows/Linux Drop receipts、production-gate
及suite-gate。四份传输哈希全部匹配，安全解包371个成员。审计450项核对无差异：
双方receipt各有36/36成功阶段及30份原始verifier报告，suite15/15及production24个
闭合单元。Windows Drop报告计数为
固定28、rustc26加2项差异、原始PE ILVerify30、AOT28及callable2/7；receipt明确
保留fullP1Closure=false。双方大型Drop字节包仍未下载，因此报告执行不能充当物理
PE/AOT验收。新SHA与全部历史run独立留存；最新审计位于
`artifacts/p1-supervision/ci-followup-v20/`。

Containment v5冻结8份源码/DLL/原生PE。唯一真实tiny1通过；lifecycle仅执行1/8
即 ❌ 失败：真实额外conhost使JobTotalProcesses2且成员审计不完整。持有Job最终
active0及成功audit disposal不能使该案例通过。不得重试或改变分母。v6仅设计：采用
原生GUI生命周期夹具，并为重复句柄释放加入admission barrier。root独立观察全部
11个已记录build/runtime身份不存在；范围限于记录身份，不证明历史compiler完整
后代树。v4两个独占构建root已删除共26对象，未触碰已复用PID52820或保留交付物。
v5两个构建root独立回收42对象、剩余root0、错误0，未终止进程且交付字节全部保留。

Windows取消v4独立审查匹配32/32包/输入哈希并识别源码缺陷。新v5包以警告视为
错误编译；最初挂起compiler参数绑定失败完整保留，随后唯一真实Helper编译退出0、
诊断0。持有compiler Jobs归零；runtime helper/CLI/signal/tiny计数均为0。
原子发布清理仍可能覆盖主要filesystem失败，任何运行trial前必须继续硬化。
P2-08.02仍为 🚧 进行中。

P2仍为8/101个 ✅ 已完成叶项；六个P1门禁及全部P2父项/阶段仍开放。准确下载、
子智能体哈希、归属核验及下一委派保存于
`artifacts/p1-supervision/resume-root-v1/resumption.json`。保留唯一ACTIVE半小时
心跳及用户 `tmp/`；下一步对账缺失物理字节、完成原生隔离和取消审查，再继续依赖
允许事项。不得重跑已验收snapshot-budget构建，也不因本次纯文档检查点再调度CI。

## 历史检查点 — 2026-10-08 08:55

逐项验收提交：`5139902` 从第一条真实Git命令启动现有snapshot验证时钟，保留
共享110秒/24命令预算及账本。候选 `aee97ccdc2509fb64693631979fa4e93c3a31578`、
tree `4fd94a69ca4659fac8fc9be5e4283caf450df6d9`、708输入、新鲜SDK10.0.401
Release0/0、Windows/Ubuntu tiny1+固定6及构建绑定tiny1+固定9均通过。root观察
60个已记录Windows PID和17个Ubuntu验证Git PID均已不存在；这是已记录身份
证据，不是完整后代隔离证明。见 `docs/p1-snapshot-budget-v1.md` 及其成对翻译/归档。

CI v18独立绑定 `d115737`/run37707754165：最终 ❌ 失败。双方expanded及
production-native作业、suite-gate成功；双方Drop作业在原生执行前失败，
production-gate拒绝缺失Drop字节。Windows/Linux新鲜构建0/0、源码707/707通过，
但构建分别消耗122.418/115.750秒，验证时钟此前在导入辅助脚本时已启动。双方
receipt仅保留2/36成功stage，generated/AOT/callable报告为null，original-PE
bindings为零。实际Drop/ILVerify/AOT/callable计数均为0。原始前置证据对账95项
绑定/62个哈希，不能闭环原生验收。对新推送SHA的时钟修复唯一调度一次，独立
核对新原始字节。

旧 `a911113`/run37705178320 production receipt原件现对账118项绑定/114个
哈希，无不一致：transport2/2，aggregate原始报告5/9。仍缺Windows/Linux
source-package及backend四份报告和完整payload字节。5/9由同一SHA的aggregate
build及双方native build/snapshot组成，不得加入新 `d115737` 的前置证据。
旧4d33334/run37702329159 production原件仍独立待核。准确清单及缺失哈希保留在
`artifacts/p1-supervision/ci-followup-v18/`。

Containment v4修复审计错误留存、不可变身份与可变命令观察的区分、持有root的
最终等待及publication错误留存。两次托管构建0/0和固定MSVC原生GUI夹具构建
通过；唯一tiny1前冻结8份源码、DLL和PE。真实tiny以准确JobTotalProcesses1、
完整成员审计、root退出0和持有作业最终active0通过；lifecycle8实际执行为0。
root审查草案变更，本轮不授权生命周期运行。新增可执行驱动器必须重新冻结
程序集并执行tiny；句柄关闭失败路径仍需审查。Linux独占可写cgroup delegation
仍为 ⛔ 已阻塞。

Windows取消v4针对真实aee97cc候选、新鲜报告和CLI2B84FDA冻结已编译审查包。
两轮警告视为错误的编译通过；controller/helper/CLI/signal实际运行计数均为0。
新运行模型区分活跃任务归属与不完整上游系统祖先，仅允许未来事件作用于已证明
的新私有控制台。唯一无信号tiny前，root须审查准确源码/包及新鲜活跃主机身份；
真实CTRL+C和异步I/O取消仍开放。P2-08.02仍为 🚧 进行中。

P2仍为8/101个 ✅ 已完成叶项；全部六个P1门禁及所有P2父项/阶段均开放。
Git/推送/唯一CI状态、子智能体manifest和回收范围保留在
`artifacts/p1-supervision/snapshot-budget-root-v1/resumption.json`。接续现有三个
独占任务，保留失败证据/用户tmp及唯一ACTIVE半小时心跳。

## 历史检查点 — 2026-10-08 08:25

逐项验收提交：`959f463` 添加必需的原生 Windows/Ubuntu `drop-native` 作业，上传
原始生成程序/验证器字节，并在 production 聚合前对账双方物理传输包。候选
`04a1a15f05f28f05471fe48816544600d0c5038f`、tree
`3a6946d9077ca956c6c9feb24bca66eb83510586`、707 输入、Release0/0、
workflow tiny1+utility8及六份 PowerShell AST 检查通过。这些 utility 检查实际执行的
native 程序、ILVerify/AOT 用例和正式 joint 控制均为零。主监督者观察九个已记录
启动身份消失；清理核验范围为已记录 root。正式 joint12、旧 API attestation、策略
authority及可信完整后代隔离仍开放。见 `docs/p1-drop-workflow-v1.md` 及其成对
翻译/归档。

CI v17 独立绑定 `a911113`/run37705178320，不作为新工作流证据。双方 expanded、
production-native作业及suite-gate均为 ✅ 已完成。下载原件对账60次物理哈希；每个
expanded平台有1203个唯一有序通过用例、formatter59及707条独立Git源码blob。
suite15/15及双方publication8报告通过。production gate JSON与原始matrix stdout
对24个单元格一致，但其27成员ZIP缺少两份native receipt及它们的八份输入报告。
因此九份aggregate输入字节和两份transport receipt仍需独立物理对账。依据v17
最终清单取得准确native-receipt artifact ID；run最终结论仍为独立pending。
旧4d33334/run37702329159的suite原件现对账12passed/2failed/1blocked；其production
原件仍为独立pending。

Windows取消v3以警告视为错误编译双方helper。root冻结真实04a1a15/CLI17E836并
执行唯一无信号Controller trial。上游父链在无法访问的系统祖先处停止，因此在
helper创建前阻塞：helper0/CLI0/signal0，未创建夹具，Controller退出1。分别保留
原始草稿与root运行manifest；草稿的零执行记录属于历史时点。独占v4设计必须解决
归属边界，不得声称不可访问父链完整，也不得向现有应用控制台发信号。Windows
CTRL+C和实际异步I/O中断仍开放；P2-08.02为 🚧 进行中。

隔离v3两次构建0/0，唯一tiny为 ❌ 失败。持有Job的证据识别本次额外conhost成员，
观察到其命令行变化，TotalProcesses2违反固定单成员契约。最终持有Job的
ActiveProcesses0和cleanup errors0不使tiny通过。MemberAuditFailures遗漏及
RootExited观察竞态须在新版本修复；lifecycle8仍为0。不得回填v2的未知成员身份。
Ubuntu可写独占cgroup委派仍为 ⛔ 已阻塞。

P2仍为8/101个 ✅ 已完成叶项；P1六门禁及P2全部父项/阶段仍开放。最新Git/push/
唯一CI状态及资源复核保存在
`artifacts/p1-supervision/drop-workflow-root-v1/resumption.json`。新推送工作流SHA只
调度一次并对账，完成正式joint和完整隔离接线，继续依赖允许事项。保留用户tmp、
全部失败证据及唯一ACTIVE三十分钟心跳。

## 历史检查点 — 2026-10-08 07:55

逐项验收提交：`f213c3a` 接入原始字节 Drop 传输组件。候选
`c950e0164719bdcf6309e944c2e0078d2ee5eafe`、tree
`08ec300868804497bc985f547bd8679f88e8f5cf`、707 输入、Release0/0、
Windows/Ubuntu 各 tiny1+utility12、两个无认证上下文入口拒绝及四份 AST 检查通过。
主监督者确认13个已记录启动进程身份消失，四个独占沙箱/链接已移除。真实 native/
ILVerify/AOT/正式 joint 控制仍为0；工作流接入及可信完整后代隔离仍开放。
见 `docs/p1-drop-transport-v1.md` 及其成对翻译/归档。

`894be1b` 修复真实 Linux CI 发现的 formatter Remaining 两次时钟采样竞态。候选
`0004e750d80900d2881d7ea680ce9a1b26228aec`、tree
`8c0e49054af4bff4da25c3a880ea19450744e68e`、707 输入、Release0/0、
Windows/Ubuntu 各 tiny1+formatter59 通过；既有预算案例现含32次有界单 tick 断言。
Ubuntu 使用 Windows 构建的可移植程序集，不是原生 CI 构建证据。主监督者确认70个
Windows 和62个 Ubuntu 已记录身份消失。Windows MIR or-pattern 诊断1通过，但
不据此解决其独立旧 CI 故障。P2-08.02 仍为 🚧 进行中；P2 仍为8/101 ✅ 已完成
叶项。P1 六门禁及 P2 全部父项/阶段仍开放。见 `docs/p2-formatter-budget-v1.md`
及其成对翻译/归档。

CI v15 绑定旧4d33334/run37702329159，最终 ❌ 失败。两个 expanded 平台均执行
1203/1203、1202通过/1失败，未执行为零。Linux formatter58/59 因已修复的 parser
预算竞态失败；Windows formatter59/59 通过，MIR binding or-patterns 在6.0166秒
后触发类型分析限制。独立审计核对32次物理哈希、每平台440个映射 ID、703条源码 blob
与883条 Git 清单；14个本地启动身份消失。aggregate 原件仍为独立待验收证据，v16
接续其精确 artifact ID。不得把 production-native 作业成功提升为物理矩阵证明。

Windows 取消 v2 构建通过，但 tiny 就绪无法附加挂起 CLI 的控制台（Win32=6）；
CLI runtime0/signal0，六个已知 PID 消失，夹具移除。隔离 v2 构建0/0；唯一 tiny
root 退出0且持有 Job 的 ActiveProcesses 达0，但 TotalProcesses2 违反单 root
契约，第二个身份未知。lifecycle8 仍为0。Ubuntu 没有可写的独占 cgroup 委派。
保留两份阻塞草稿及失败，不放宽标准。主监督者依据其精确 manifest 和资源记录接续。

最新 Git/push/唯一 CI 状态保存在
`artifacts/p2-supervision/formatter-budget-root-v1/resumption.json`。下一步完成安全
取消/完整隔离，接入必需 NativeV5/transport/joint 门禁，调查 Windows MIR 限制，
对账新鲜同 SHA CI。保留用户 tmp 和唯一 ACTIVE 三十分钟心跳。

## 历史检查点 — 2026-10-08 07:25

分别验收提交：`ad813af` 修复Linux隐藏回执沙箱清理，Windows/Ubuntu各通过
tiny1及fixed3，四个沙箱已移除。`6d3d428` 添加剩余证据单调I/O接受检查：候选
`a434f5bbdf74e3e2b81c368f9bf12a1fd36c20af`、703输入、Release0/0、mapping10、
compiled-guard3及真实Windows CLI9通过；NativeV5生成执行28项，26精确匹配/
两个继承差异。正向harness输入明确是旧回归证据。root观察123个PID不存在。
实际I/O计时器延迟注入和独立后代证明仍开放。

`4945807` 修复真实同步源码增长发现的formatter字节限制异常。候选
`eb0598f39c3797e29b3c9d0332a179a03aeac9f1`、tree
`ee500132a72b60022f2c689ac74dc2038d67cdbc`、703输入、Release0/0、fixed59及
Windows CLI8通过。Linux tiny1，以及增长/变化/SIGINT/托管调度4项和独立外部
FIFO-worker1项通过。root确认18个Windows launcher PID和六个Linux CLI PID/夹具
不存在。保留原未命中事件及实际退出134失败。Windows定向CTRL+C和实际异步I/O
中断仍开放，P2-08.02为 🚧 进行中。P2仍为8/101个 ✅ 已完成叶项；六个P1门禁
和全部P2父项/阶段均保持开放。

CI v14绑定旧264416f/run37699582503，最终 ❌ 失败。Windows实际1203/1203、
formatter59/59及source703；Linux expanded在诊断隐藏沙箱清理失败后实际执行0，
其断言虽然3/3通过。两native源码19/39及本地backend12通过；suite的9passed/
3failed/3blocked阻止production聚合，没有物理24矩阵。v14独立对账Linux43哈希/
16报告及Windows53哈希/19报告，使用152/250文件；20个已记录本地launcher身份
均已退出。保留run-log获取失败及审计器修正blocked形状前的失败。

`joint-transport-hardening-v1/` 的transport加固草稿通过utility12、五AST及两patch
检查；真实native/ILVerify/AOT/joint12仍为0。不声称共享workflow已经接入。
下一步审阅transport3及强制joint接线，完成可信后代约束和安全Windows取消控制，
再执行同SHA原生门禁。Windows取消/后代约束草稿独占，仍需root验收。
最新Git/推送/唯一CI状态保存在
`artifacts/p2-supervision/formatter-limit-root-v1/resumption.json`。
保留用户tmp、旧失败及唯一ACTIVE半小时heartbeat。

## 历史检查点 — 2026-10-08 06:59

本轮已验收提交：`9265057` 回执诊断（trial1/fixed3及publication8）；`64b3046`
有界NativeV5 producer接入（utility1/8，真实native执行0）；`e0fb26b` formatter接入。
最终formatter候选 `103d9a5b952cf852468dab511b244e67193895ae`、tree
`ebe90aaf0b0c379a6973125edca30572773ba171` 绑定703输入，新鲜SDK401 Release
零警告/零错误，trial1、formatter59及既有tooling18通过。Windows/Ubuntu真实CLI各8项
通过且自有夹具已移除，包括独占ext4夹具的真实Unix可执行权限。P2-08.02为 🚧 进行中，
实际CLI取消/截止/源码变化控制仍开放。P2仍为8/101个 ✅ 已完成叶项，不关闭父项或
阶段。注册1203与完整执行分开记录。保留
`artifacts/p2-supervision/formatter-root-v5/linux-delivery.json` 记录的独占Ubuntu
原始证明及其复制/哈希证明。最新Git/CI/接续状态保存在该目录 `resumption.json`。

CI v13分别对账旧e90及新33b153a，run37696451049六个job通过，Windows/Linux
1144/1144、694输入及物理24单元矩阵。这些旧SHA证明不能关闭本轮新代码。
NativeV5工作流/transport30/joint12接线及C#最终I/O时钟patch仍待接入。
读取 `joint-native-readiness-v1/HANDOFF.md` 及部分producer记录。root补完worker
失败的最终PID/沙箱检查，未发现任务身份残留。CI审计原90文件上限越界已如实保留；
另行授权的250文件最终阶段记录206个独立文件（保守上界232）。17个已记录CI启动
进程身份均已退出。

Formatter v1–v3构建失败及v4路径前缀失败均保留。root修正analyzer类型/取消参数顺序，
以96个静态AST类型根保留独立公开属性oracle，并规范Windows corpus前缀且不放宽
目录检查。worker审阅早于最终v5，不将其当独立最终验收。下一步推送已验收代码，
在最终SHA仅调度一次并对账CI，完成真实formatter取消/竞争控制及NativeV5/joint门禁。
保留用户tmp及唯一ACTIVE半小时heartbeat；六个P1阶段门禁均保持开放。

## 历史检查点 — 2026-10-08 06:28

验证器引用留存修复为 ✅ 已完成。最终候选
`b404f0dd62cb0368159c319e67ba98e6542eb2c4`、tree
`11523f2ca44103bcb96e86ddc53a1b53b9eb6328` 绑定694输入；新鲜SDK401
Release零警告/零错误，trial1、reference4及legacy-source12通过。
真实Windows源码包producer通过19/19、原始PE ILVerify39、CoreCLR19及Native
AOT19；严格物理验证通过。root核对100个已记录进程身份不存在、20个自有临时目录
不存在。参见 `docs/p1-reference-retention-v1.md`、成对翻译及归档。
worker最后审阅调用解析失败，root补完其余检查。计时器延迟注入及独立后代进程
约束仍开放。P2仍为8/101个 ✅ 已完成叶项，六个P1阶段门禁均保持开放。

CI follow-up v12针对既有e90e9fe的run37693029849对账74个hash/27报告、Linux1140
及693输入、mapped440/pending24和两平台production回执。其预算内未取得Windows
expanded及两个aggregate。接续该run，不重复调度、不用于替代本次新修复证明。
推送本次已验收修复并调度一次新SHA绑定run。NativeV5回执/transport/joint集成仍
等待真实验收。

回执诊断控制冻结于 `receipt-diagnostic-controls-v1/`：标准patch适用性检查及
trial1/fixed3工具控制通过；真实生产源码接入及验收仍开放。Formatter v2的
review-manifest保留准确替换hash和49+10用例，runtime0、leafClosed=false。
上次只读变量错误后的标准patch生成及最终资源交接正在接续。保留旧失败、用户tmp及
唯一ACTIVE半小时heartbeat。下一步独立接入/验收/提交回执诊断，然后接续joint/native
门禁及formatter。

## 历史检查点 — 2026-10-08 06:08

此前05:54检查点遗漏了中文翻译。两种语言现包含相同检查点及本更正记录；不改变
任务状态或验收分母。提交 `e90e9fe83b201be882c76c0e5c9f20be3b779b66` 已推送；
唯一 CI 为 run `37693029849`、attempt1。接续该运行，派发或创建后续任务前先读
`artifacts/p1-supervision/process-wrapper-root-v1/resumption.json`。
`reference-retention-fix-v1/` 的引用留存及回执诊断草稿 runtime0，等待验收。
P2 仍为8/101个 ✅ 已完成叶项，六个 P1 阶段门禁均保持开放。

## 历史检查点 — 2026-10-08 05:54

P2 仍为8/101个 ✅ 已完成叶项。六个 P1 阶段门禁均保持开放。root已接入进程
包装器取消/错误保留修复；最终 Windows PowerShell 工具验收为 tiny1、fixed6、
failure-tiny1、failure2及既有 publication8。候选
`2e38438404e112b6afe3ae39d635cc5d670f55cc` 绑定693个输入。
参见 `docs/p1-process-wrapper-v1.md`、成对翻译及验收归档。
CleanupComplete 仍仅证明父进程退出，未声明后代闭环。保留worker最后here-string
调用失败及root标量数组试跑失败；它们均不属于成功证据。root负责最终guard/dispose
修改及独立故障控制。资源审阅和准确Git结果保留于
`artifacts/p1-supervision/process-wrapper-root-v1/`。

前一远程候选 `e6ee4e6140d5776c376af59bc94ed0766faf806a`、tree
`8e303078c582e8548cc9f74ecca9fc6ad400fa99`、run `37689531739` 的Windows
production-native失败：源码包17/19，scalar-positional-copy及
reference-shared-mutable-origin因verifier引用留存10s阻塞。
CI follow-up v11对账28个hash/13报告、两个1140项harness、691个源码输入、真实
mapped440/pending24及suite15。四个原始PE哈希保留。源码报告匹配回执哈希
AA1503D77CF86F6BEC840950AF032E31C1D815A1BC6DA02B6003B50BA0C28F2C；
回执中的bytes-changed消息没有已证实的制品不匹配。Windows backend验证12个单元格；
完整24/native闭环仍开放。Actions报告回收孤儿vctip PID3328；不得混淆本地下载进程
不存在与CI子树闭环。保留v11原件和失败审阅。

下一步：安全修复10s验证器引用留存及失败validator的误导诊断；接续已有run且不混用
候选证明。NativeV5回执/joint transport集成仍开放。Formatter v2部分副本/CLI控制
保存在 `formatter-read-fix-v2/`，runtime0、固定59/父91，注册计数脚本错误后尚缺
patch/最终hash审阅。完整稿审阅及新鲜源码绑定59项验收前，不接入原7文件/注册、
formatter-review-v1 guards和v2副本。保留用户tmp和唯一ACTIVE半小时heartbeat。
不声明阶段完成。

## 历史检查点 — 2026-10-08 05:22

P2-06.02 最终候选 `29a8edaa6d63f1b7601159dedbe04decbeef989a`，tree
`f4709c042009cf8da47a837dbfdb0014519f05ce`，新鲜 SDK401 Release 零警告/零错误，
tiny1、binding14/14 及继承 contract8/8 均通过。P2 达到 8/101 个 ✅ 已完成叶项；
P2-06 和 P2 仍为 🚧 进行中。运行导入调用、普通导出、真实 NuGet 执行及双平台
AOT 仍属于后续叶项。当前注册数 1140 与完整执行分别记录。归档：
`docs/evidence/p2/P2-06.02*`；最终物理证明及保留的独立夹具：
`artifacts/p2-supervision/interop-v4/`。十个验收进程台账均已退出。
此前 v1/v2 构建失败及 v3 读取加固前通过记录仍保留。

生产 CLI 修复已提交并推送为 `f99907b`；expanded run `37686188538` 六个 job
均通过。CI follow-up v9 对账 44 个 hash、两个 1126 例 harness、真实映射
440/pending24 及物理矩阵 24。该 SHA 与新互操作
叶项独立，不能证明其整体候选门禁。P1 门禁仍保持开放。

Native receipt v2 冻结于 `artifacts/p1-supervision/native-receipt-v2/`：语法、补丁
适用性及 8/8 隔离工具控制通过；未声明真实 ILVerify/AOT 运行。保留 28/30/28/2/7
及 36 个阶段。root 下一步：审查/接入原始 PE 捕获报告绑定及正式 joint transport
packet，解决共享进程取消/后代清理缺口，再执行真实 native jobs 并逐项对账同 SHA
门禁。工具测试、合成元数据及旧 SHA 报告不能替代这些义务。
root 已完成 P2-06.02 最终源码/构建/worker 交叉验证：691 个精确编译输入、七个
程序集、23 个真实 worker 身份及三个引用 hash。最后的独立报告未在期限前发布；
保留该未完成审阅和成功的 root 交叉检查，不把它改称独立最终报告。共享进程审阅
方案为 `artifacts/p1-supervision/process-cancellation-review-v1/REVIEW-PLAN.md`；
其检查点写入失败，root 已保存接续记录。未声明运行时进程 containment 证明。
保留用户 `tmp/`、
交付物和唯一 ACTIVE 半小时 heartbeat。

## 历史检查点 — 2026-10-08 04:58

P2 仍为 7/101 个 ✅ 已完成叶项；P1-GATE.01–.03 仍为 🚧 进行中，
.04–.06 仍为 ⏳ 计划中。远程候选 `723ab72178911b24ecdf6b6a31d9f94b646fcce1`
的 expanded run `37682369775` 六个 job 均通过：Windows/Linux 完整 harness
各 1126/1126，suite 聚合 15/15，物理 production 矩阵 24/24。
CI follow-up v8 对账 37 个 hash，并核验十六个子/父进程身份已退出。
此前 Linux 聚合失败已修复；仍保持 `fullP1Closure=false`。

root 已实现真实完整 harness 覆盖和 NativeV5 CLI 入口、有界捕获读取及不覆盖
已有证明的写入。冻结候选 `13e77bde34e642974f7eec0ea842b108d0e40447`，tree
`a69da77076490b8b6089dfb4897b1fd210d5e1ba`，新鲜 SDK401 Release 零警告/零错误，
映射控制 10/10、真实 CLI 控制 9/9 均通过。当前 NativeV5 真实运行保留全部 28 例：
26 个精确匹配加两个继承的冻结差异，无失败/阻塞/跳过，物理 PE 验证通过。
harness CLI 正向控制读取旧候选 `e773869314e5d7ebb51f035820bc75bcfcc1fdc5`
真实 1126 例报告，明确标注为回归输入。它不能证明新 producer 的完整 harness
或正式门禁。归档：`docs/evidence/p1/remaining-cli-v1-*`；物理原件：
`artifacts/p1-supervision/remaining-cli-v3/`、`artifacts/p1-drop/remaining-cli-v3/`。
任何门禁关闭前，新鲜同 SHA CI 必须执行新增覆盖入口。

下一步：对账该 CI，再完成正式 joint transport/gate packet、真实拒绝控制、
NativeV5 ILVerify/AOT 回执和双语完成记录。joint/native receipt 准备稿仍未验收。
Interop scope 修复已冻结于 `artifacts/p1-supervision/interop-scope-fix-v1/`；
runtime0，叶 14/父 36 例，仍需新鲜 14+8 验收。保留 formatter/documentation
草稿、原有 `tmp/`、旧 junction/target 和诊断文件。唯一半小时 heartbeat 保持
ACTIVE；root 继续独占共享源码、构建、集成、双语文档、stage 和逐任务 commit。

## 历史检查点 — 2026-10-08 04:29

P2 现有 7/101 个 ✅ 已完成叶项。`d813a1e` 关闭 P2-04.05：最终候选
`064f5c54600b5a6b0c7feaafd564b2cdf2fb8421`，新鲜 SDK401 Release 零警告/零错误，
锁 10/10 及 cfg12/features11/manifests38/legacy3+5+2 均通过。
归档为 `docs/evidence/p2/P2-04.05*`；物理证明在 `artifacts/p2-supervision/lock-v3/`。
此前 v2 证据早于 case map 的 LF 规范化；v3 对最终字节重新执行全部必需场景。
1126 个注册项与实际执行分开。父项、阶段及 P1 门禁均未关闭。

远程 `e9c2cb2` 的 expanded run `37678958396` 双 native 与双 expanded 作业均通过，
但 suite15 为 13 通过/1 失败/1 阻塞。Linux 聚合误读 Windows 可执行文件 basename，
production 因而受上游阻塞。`7ea2412` 规范路径分隔符，保留全部 SDK/方案/构建检查。
实际生产谓词控制在 Windows 与 Ubuntu PowerShell 各通过 10/10；本地命令检查并非
新整个候选门禁。证据在 `artifacts/p1-supervision/suite-path-fix-v1/`。
下一轮须对账新同 SHA 运行。CI v6 保留八次下载、66 个哈希、64 个原生指定匹配与
二十个已退出所属进程，位于 `artifacts/p1-supervision/ci-followup-v6/`。

接续 `prepared-p1-joint-gate-v1/` 内 `p1_remaining_gates`：修正入口替代第一草稿；
仅语法通过，runtime0；联合运输/强制候选接线与十二项拒绝控制仍开放。
不得复制旧无界入口。`documentation_p2` 正在 `lock-final-review-v1/` 完成 v3 锁的
只读资源审核。root 独占真实源码/构建/共享入口/stage/commit。保全冻结 formatter/
documentation/interop 草稿、用户原有 `tmp/`、旧改名 junction/target 及诊断制品。
唯一三十分钟 heartbeat 保持 ACTIVE；继续下一依赖允许任务。

## 历史检查点 — 2026-10-08 03:58

本节决定接续；后续章节是历史检查点。`20c77a8` 完成 P2-04.04 后，P2 为
6/101 ✅ 已完成叶子。最终候选为 `b75bdf5d68bb8a3a50ee4aaaca9500bb1da6fa4a`：
全新 SDK 10.0.401 Release 零警告/错误，12/12 cfg、11/11 feature、38/38 清单
及 3+5+2 旧版案例。归档为 `docs/evidence/p2/P2-04.04*`；最终物理源码与执行
保存在 `artifacts/p2-supervision/cfg-v5/`。71 条隔离执行记录均退出。cfg-v1/v2
初步失败在不缩范围的情况下修复；v3 证据早于 Program 行尾修正，v4 正确拒绝
陈旧候选，v5 重新绑定最终字节并重跑。保留全部来源。P2 父项/阶段和 P1 门禁
均未闭环。唯一 30 分钟定时监督仍为 ACTIVE。

真实远程候选 `bb1c062906f7bd581f98d45f8b14791c2595215e` 已通过路线图
`37675252213`、Windows P0 `37675252075`、Linux Native AOT `37675252162`
和平台 `37675252198`。Expanded 运行 `37675251918` 的两个 production-native
和 expanded Linux ✅ 已完成；expanded Windows ❌ 失败：1,104 个测试全部执行，
1,102 通过，恰好两个 generated Drop oracle 测试未能在有界 PATH 查找中找到 rustc。
不是超时，也不是语义 trace 差异。失败 job 为 `112976974188`；下载的 harness、
stderr 和失败摘要位于 `artifacts/p1-supervision/ci-followup-v4/`。双平台回执的
SHA/tree/run/attempt 与 64 个选定物理哈希匹配；这种选定审核不等于全部保留字节
或聚合门禁闭环。Backend 回执使用 `legacy-v1`，六夹具矩阵不能替代 NativeV5 Drop。

Root 已把验证过的 Windows P0 oracle 安装块接到 expanded/production-native 两处：
有界安装、Get-Command rustc/dotnet 路径、真实 +1.98.0 版本检查和 GITHUB_ENV。
全新本机候选 `ed2ad9b0b3ab4e95aed98c60565a709cc956a7a9` 通过 SDK401 零警告/错误
构建；两个真实 Drop 测试均通过且进程完全清理。修复提交 `705efc3` 记录 CI 修复。证据在
`artifacts/p1-supervision/expanded-oracle-fix-v1/`。所属启动器已退出；推送已接受提交并调度一套同 SHA CI。不得拼接早先失败/成功候选证据。

已冻结未构建草稿包括 `prepared-cfg-v1/`（已集成）、`prepared-lock-v1/`
（P2-04.05，15 文件/10 案例）、`prepared-interop-binding-v1/`（P2-06.02，
11 文件/14 案例）和 `prepared-formatter-v1/`（P2-08.02，59 案例）。Formatter
只读审核准备了 `formatter-review-v1/formatter-command-guards.patch`，修复实际
读取字节上限、Unix 权限、所属临时文件创建和清理。此外须在重解析前拒绝超过
冻结 1M 输入字符上限的格式化输出，否则第二次格式化可能拒绝第一次的输出。
先接入原实现再应用审核修复，完成语义构建、固定执行和真实 CLI 字节非修改验收
后才宣称完成。Root 是唯一集成/构建/共享注册负责人，每次提交一个已验收任务。
全部草稿工作者报告没有待回收所属资源。

活跃工作者 `p1_remaining_gates` 和 `documentation_p2` 仅预留
`prepared-p1-remaining-gates-v1/` 和 `prepared-documentation-v1/`。前者准备缺失的
真实 coverage/NativeV5 CLI 与保留 CI 证据接线；后者在父项 91 中冻结 P2-08.03 的
12 个文档场景。先接续现有工作者再替换。保留用户 `tmp/`、已移动旧 junction 及
目标、被拒绝清理 ZIP 日志、全部快照和诊断产物。

## 当前检查点 — 2026-10-08

接续以本节为准，后续章节保留历史检查点。P2 有 5/101 个 ✅ 已完成叶子：
P2-04.01、P2-04.02、P2-04.03、P2-06.01 和 P2-08.01。P2 父项与阶段均未闭环。
P1-GATE.01–.03 保持 🚧 进行中，.04–.06 保持 ⏳ 计划中。

上一检查点后验收的提交为 `ef8bb36`（版本化测试容量）、`5358e9b`（实物新鲜构建
绑定）、`7de0d94`（38/38 严格清单）、`5b1ab7f`（8/8 显式 Drop 策略传播）、
`6725b04`（12/12 源码包验证器控制）、`93e60b2`（8/8 精确后端验证器控制）、
`41c3f88`（Windows 后端证明）、`3495da6`（Windows 源码包证明）和 `ed0efde`
（11/11 feature 解析）。当前测试容量为 schema 2／4,096 项注册，最新 Release
清册有 1,104 项实际注册；冻结的 P1 分母保持不变。

Windows 生产候选 `93e60b2e3881bb13e5a836271739aaae944e13af`、tree
`adcf297f7780b2ada00e5f5f745c501f714a926c` 通过全部 19 个源码包、39 个原始 PE
和本机 12 个精确后端单元。生产验证器重新读取保留实物，返回本机门禁为真。归档位于
`docs/evidence/p1/source-package-native-win-v1*` 与 `backend-native-win-v1*`。
100 个源码包子进程和 30 个后端子进程均退出，所属临时目录已不存在；另外 12 个
Linux 后端单元和同 SHA 原生 CI 仍待收口。

Feature 候选 `8a250d1bd1baf58bb75242e6d85172bed7fc01e4` 通过 SDK 10.0.401
的新鲜零 warning／error 构建、11 项 feature、38 项清单和 5+3+2 项旧包检查。
证据为 `docs/evidence/p2/P2-04.03*`。解析器执行全图 1,024 条原始边上限；loader
的每包元数据上限仍为独立阶段规则。两个包 512+512 条成功，600+600 条拒绝。
所有验收构建／测试启动器与隔离进程均已回收。初步失败保留于
`artifacts/p2-supervision/features-v1/` 与 `features-v2/`，验收证据在 `features-v3/`。

CI 实现提交为 `a4e62bd`（独立原生 job 与实物聚合）、`7ed3c3d`（Windows oracle
显式路径）、`6510e6a`（Linux runtime sidecar 与有界诊断）、`5c2900d`（旧报告
候选 SHA 绑定）。最终候选修复在 `4dc082b43d45583ba094ca1de9ea6add4dc3aae6`
通过新鲜零 warning／error 构建和 6/6 差分控制。四项本地 CI 拒绝控制通过，但不是
原生 CI 验收。推送这些可审阅提交，在同一个远程 SHA 调度 `p1-expanded.yml`、
`p1-platform.yml`、`linux-native-aot.yml`，并核查自动 Windows P0 运行；精确 run ID、
状态和来源存入 `artifacts/p1-supervision/`。生产门禁对旧七报告／15 检查 suite gate、
双原生平台的 19 包／39 PE 回执及实物 24 单元后端矩阵取合取。完整测试、NativeV5
Drop、精确覆盖和全部 P1 门禁仍需联合核对，不能由本机切片推导阶段闭环。

当前 `cargo_p2` 仅预留 `artifacts/p1-supervision/prepared-cfg-v1/`，准备 P2-04.04
草稿。冻结并审阅全部固定 cfg 场景后再集成，必须包括源码选择和条件依赖；仅有
predicate helper 不能关闭叶子。`tooling_p2` 已冻结并交付 `prepared-native-ci-v1/`
和 `prepared-legacy-ci-v1/` 的 CI 草稿。root 负责真实源码集成、构建、共享入口、
验证及逐任务提交；空闲 worker 可接续依赖允许的 P2-06.02／P2-08.02，独占不同文件。

旧 `tests/RustSharp.Tests/bin/Release/net10.0` Junction 已可逆移动到
`net10.0.preexisting-link-20261008`；其 2026-09-24 目标及全部独有内容仍保留。
canonical 输出现为普通的新鲜构建目录。不得删除旧目标或放宽重定向路径验证；
恢复来源记录为 `artifacts/p1-supervision/native-trials-v1/preexisting-junction-recovery.json`。
保留原有用户 `tmp/`、清理被拒绝的 ZIP 日志、候选 refs 和全部审阅产物。唯一
`rustsharp-p2` heartbeat 保持每 30 分钟启用，先接续既有 worker，再创建替补。

最新 CI 后续：`5646c98` 归档两份历史源码包报告，并同步替换双语路线图中指向
忽略产物目录的链接。Roadmap CI 在 `5646c982418df7740a609d4f8232b2d091e175f6`
通过（run `37674077686`）；Linux Native AOT CI 在
`c4e0962460afc4abe32dd1fd0406e34eb59dc79b` 通过（run `37673374351`）。这是单个
workflow 的结果，不代表阶段闭环。原生套件运行分别为 `c4e0962` 的 `37673475249`
和 `5646c98` 的 `37674078250`。精确快照和下载诊断产物位于
`artifacts/p1-supervision/remote-ci-v1/` 与 `remote-ci-v2/`。重复手动调度的平台／
Linux 运行 `37673480822`、`37673486650` 已取消，保留其来源记录。

两项真实 Linux 故障已修复，仍待新原生 CI：`6f83d50` 在短命 Git 进程退出前记录
验证器启动时钟，消除空 `Process.StartTime` 竞态，且不放宽候选核验；真实源码
验证与 12 次短命 Git 启动通过。`8aa7d63` 在 Linux 重写离线缺失报告夹具后恢复
模拟 Windows 身份，保留失败上游／阻塞报告语义，八项发布控制全部通过。失败原生
任务的生产回执和源码报告保留于 `remote-ci-v1/download-receipt/` 与
`download-source/`，夹具错误在 `download-suite-preflight/`。所有诊断／下载／验证
启动器均退出并完成清理。推送修复候选，在同 SHA 调度一次新原生套件；不能把旧
失败运行或夹具模型作为原生闭环证明。

`prepared-cfg-v1/` 现有 11 个冻结审阅文件及进度清单：12 个固定场景，语法／补丁
检查通过，语义构建／运行／闭环仍待完成。`interop_p2` 和 `tooling_p2` 分别预留
`prepared-interop-binding-v1/`（P2-06.02：不变 36 项契约内的 14 个叶子场景）和
`prepared-formatter-v1/`（P2-08.02：49 项语料与 10 项 formatter 场景）。两者均为
草稿，root 需审阅、集成、构建、执行和提交。

## 授权与接续

用户授权监督子智能体实现全部 15 个 P2 父任务、94 个实施叶子和 7 个门禁叶子，
随后将 P1 全部未完成的实现、集成和退出门禁要求一并纳入。每验收一个任务就单独
提交一次本地代码。`rustsharp-p2` heartbeat 每 30 分钟接续本会话，名称为
RustSharp P1/P2 子智能体监督与逐任务提交。派单前核对当前文件、Git 历史和存活
智能体；接续现有派单，避免重复。监督者负责共享注册入口、双语状态、串行构建、
验收和提交。在代码可供本地审查且检查通过后，完成 P1/P2 门禁明确要求的已推送
SHA／CI 验证。本授权不包含产品发布、部署、付费安装或向外部发送消息。

每次 heartbeat 的工作预算为 25 分钟，最多三个工作子智能体。每次委派都重复机器级
PowerShell 7、有界循环／搜索／命令、进程身份与清理、工具发现、临时路径归属以及
禁止 Graphify 的规则。保留原有未跟踪的 `tmp/`。

## 前置项审计

P2 为 🚧 进行中。P1-GATE.01–.03 为 🚧 进行中，.04–.06 仍为 ⏳ 计划中；
不声明 P1/P2 阶段闭环。
源码提交 `0c5d80f623ab263682ed36c47f3e9182e23374f9` 的公开检查均为 ❌ 失败：

| 检查 | 证据 |
| --- | --- |
| Windows P0 | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927965> |
| P1 平台门禁 | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927863> |
| Linux Native AOT | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927846> |

P1 平台作业的 native 子项通过，但完整退出门禁失败。Linux 作业的原始类型差分
14/14 通过；后续 ILVerify 因引用集遗漏 `RustSharp.Runtime` 失败。
P1-GATE.01 需要绑定候选提交的语义覆盖；.02 需要
解决尚未批准的 Linux 展开输出差异；.03 需要同一 SHA 的源码包执行；.04 需要远程
套件证据；.05 需要同一远程 SHA 的全部必需工作流通过；.06 需要目前缺失的双语完成
报告。历史 P1-10 的 15/15 结果明确记录 `fullP1Closure: false`。保留这些验收要求。
P2-04.01、P2-06.01 和 P2-08.01 依赖已验收的 P1 父项，可独立推进。
core/alloc/std 任务链等待 P1-GATE。

## 当前所有权与后续工作

| 叶子 | 状态 | 所有者与预留文件 |
| --- | --- | --- |
| P2-04.01 | ✅ 已完成 | `cargo_p2`：`CargoContract*.cs`、`P2CargoContractTests.cs`、`p2-cargo-v1-manifest.json`；10/10 隔离验证器测试和 78 条冻结记录。 |
| P2-06.01 | ✅ 已完成 | `prerequisite_audit`：`DotNetInteropContract*.cs`、`P2InteropContractTests.cs`、`p2-dotnet-interop-v1-manifest.json`、双语 ADR 0010。 |
| P2-08.01 | ✅ 已完成 | `tooling_p2`：`ToolingContract*.cs`、`P2ToolingContractTests.cs`、`p2-tooling-v1-manifest.json`。 |

每个合同通过后，单独提交对应叶子的文件与证据／状态更新。先将工作智能体转到
Linux 原始类型一致性失败、未批准的生成 Drop 展开差异，以及 P1 覆盖／源码包证据。
随后预留互不重叠的文件，分派 P2-04.02（清单加载器）、P2-06.02（绑定）和
P2-08.02（无损格式化）。
清单中的后续用例清册不证明实现用例已经执行。继承的测试运行器限制注册数为 1024；
新增 P2 目标时，不得悄悄修改冻结的 P1 分母或突破此上限。

## 本地验证与资源台账

PowerShell 为 `C:\Program Files\PowerShell\7\pwsh.exe`，版本 7.6.6。
本机 .NET SDK 为 10.0.401，驱动路径为
`C:\Program Files\dotnet\sdk\10.0.401\dotnet.dll`；仓库保留 10.0.400 固定版本。
现有显式 SDK 驱动方式允许本地验证，无需修改固定版本。一秒有界 SDK 试运行通过，
串行 Release 基线构建以零警告／零错误通过。PID 35176 和 93044 均已退出，
`CleanupComplete: true`；元数据和日志保留于 `artifacts/p2-supervision/`。
本任务没有创建临时源码树。

`eng/Invoke-P2ContractEvidence.ps1` 为隔离的过滤合同验证记录固定分母、清单哈希、
源码候选、工具／RID、有界进程台账、清理和失败原因。报告区分验证器测试与冻结的
后续实现分母，并保留 `fullP2Closure: false`。源码候选必须匹配整个工作树；
先等待工作智能体冻结文件，再生成候选快照。报告与哈希来源证明保留于
`artifacts/p2-contracts/`。

首批合同通过 36/36 定向测试，以及额外的 10/10 + 8/8 + 18/18 隔离、绑定源码的
验证。验证候选为 `41e14c1587d4366007fa108b0f5f6ba023da6c77`
（641 个编译器／测试／工具输入），保留于
`refs/codex/p1-10-candidates/41e14c1587d4366007fa108b0f5f6ba023da6c77`。
可迁移的叶子报告与记录原始／归档哈希的 LF 规范化运行器记录归档于 `docs/evidence/p2/`。
Release 构建零警告／零错误；当前注册数为 1005（继承的 969 项 P1 注册与 36 项 P2
注册）。全部构建／测试子进程已退出，快照助手已删除其专属索引。

已下载、校验哈希并以只读方式检查原始 Linux ILVerify 制品，未执行其中内容。
选出的报告／来源证明保留于 `artifacts/p1-supervision/cargo-ci-download/`。
自动审批拒绝了组合 ZIP 提取／删除，仅返回 `blocked by policy`；安全只读提取成功。
40,140,758 字节的 `archive.stdout.log` 仍保留于该目录；不得绕过拒绝或声称 ZIP 已删除。
三个下载／网络进程 PID 52444、23432 和 42364 均已退出。

## P1 整改派单

已验收的 P2 提交为 `3aa7c00`（P2-04.01）、`867a62a`（P2-06.01）和
`2075ebe`（P2-08.01）。这些清单是设计清册；后续实现叶子仍未关闭。
三个工作智能体已明确重新分派到 P1：

| 工作 | 状态 | 预留所有权 |
| --- | --- | --- |
| ILVerify 运行时引用 | ✅ 已完成 | `cargo_p2`：`eng/Invoke-ILVerify.ps1`、`eng/Test-ILVerifyRuntimeReference.ps1`；10/10 项真实生成 PE 检查，包含冲突的重复 AssemblyRef。此修复不关闭 P1-GATE.03。 |
| native Drop/展开实现 | ✅ 已完成 | `prerequisite_audit`：显式 NativeV2 API／PE 元数据及嵌套异常保留；8/8 项 Windows 定向检查和 28 项 v5 用例（26 项匹配、继承的两项正常清理差异）。Linux／平台／CLI／包验收仍待完成。 |
| 精确 P1 需求／证据映射实现 | ✅ 已完成 | `tooling_p2`：10/10 项隔离映射／拒绝检查；40 个需求、160 条清册记录、完整实际源码注册和六个精确缺口，覆盖 24 个后端／RID 单元。生产门禁／完整测试集成仍待完成。 |

保留旧 v1 Drop 和 v4 运行器入口。Native-v2 必须是具名、可观察的生产选择，保留父／子
异常链并通过真实生成程序检查；不授权输出字符串特判或批准新差异。门禁映射必须使用
冻结的可执行、仅检查及拒绝分类与真实证据，不能用 40/160 清册或通用平台样本替代
缺失覆盖。REQ034 和 REQ040 是聚合／平台边界，不得引入前置依赖环。
映射验收并单独提交后，将 `tooling_p2` 转到十九项源码包运行器的同 SHA 来源绑定和
严格验证器。root 负责共享门禁／报告／CI／CLI 注册及串行验证。

## 2026-10-08 验证检查点

运行时引用修复在 Windows x64、ILVerify 10.0.11 上通过一项极小试运行和全部十项
固定检查。报告归档于 `docs/evidence/p1/ilverify-runtime-reference.json`；它没有候选
SHA，属于修复验证，不是同 SHA 平台／门禁证据。独占夹具目录已删除，全部十二次
子进程调用均已退出，启动进程 PID 47404 已退出且 `CleanupComplete: true`。
日志保留于 `artifacts/p1-supervision/ilverify-runtime-10*`。

修正两处 CA1068 问题后，第三次严格 Release 构建以零警告／零错误通过。极小
NativeV2 源码到 PE 程序也通过。NativeV2 和覆盖映射仍待完整定向验证及分别提交。
共享运行器已有 1023 项注册，原上限为 1024；后续 P2 注册需要明确的容量／版本决策。

最终源码候选 `4bfe06016586775baa4df0385397fb1298d4b944` 通过零警告／零错误
Release 构建、8/8 项 NativeV2 进程隔离检查和 2/2 项旧 v4 rustc 检查。两套完整
Drop 差分均保留全部 28 项：Windows 上 26 项精确匹配及既有的两项正常清理差异。
v5 报告验证器接受两种根路径分隔符形式，拒绝同名前缀兄弟目录，并验证实际 PE
策略元数据。此前失败的检查保留于 `artifacts/p1-supervision/`，没有用作闭环证据。
最终过滤测试报告归档于 `docs/evidence/p1/native-v2.harness.json` 和
`docs/evidence/p1/drop-v4-compatibility.harness.json`。它们验证此修复；仍需要
1023 项完整测试及原生 Linux／ILVerify／AOT 门禁。全部验证启动进程已退出且清理完整。
保留候选 ref 和报告引用的生成 Drop 制品。

映射的 10/10 项检查在同一源码候选上通过。解析器现在读取独立 C# 注册数组关闭行，
不再误匹配 Rust 字符串中的 `];`；拒绝重复源文件，并把 REQ028/029 直接绑定到最终
八项 NativeV2 检查。过滤测试报告及原始／归档哈希分别归档于
`docs/evidence/p1/gate-mapping.harness.json` 和
`docs/evidence/p1/gate-mapping-archive.json`。这是验证器验证，不是实际覆盖闭环：
六个具名缺口／24 个单元仍需要双 RID 的真实 ILVerify／AOT 执行。生产门禁必须把
此映射与新鲜完整测试注册清册检查联合使用。

已验收的修复提交：`30f728b`（运行时引用）和 `f42bd7e`（显式 NativeV2 实现）。
下一步所有权为：`cargo_p2` 负责 P2-04.02 清单，`prerequisite_audit` 负责 CLI／
编译器／元数据 Drop 策略传播，`tooling_p2` 负责十九项源码包的来源绑定／严格验证器。
root 负责共享注册容量、调用方集成、CI／门禁聚合、双语状态、串行验证／提交。
仅在新改动／失败影响已验收检查时重跑这些检查。

## 第49轮：原始 host/runtimeconfig 审查

限定审查组件 ✅ 已完成：f463ac8/run38515250985/attempt1 的全部14组保留 native-host 源码/项目及14份 runtimeconfig，770/770字段匹配。Root 独立检查每份真实 runtimeconfig、精确 host 文本及项目结构、报告绑定和冻结源码合同。[审查证明](../evidence/p1/f463-host-runtimeconfig-review-v1.json)保留首次失败审计，并明确保留30项真实未知 PE/PDB/手工 MIR 结构组件。没有执行或重编译原始二进制。P1仍 🚧 进行中，六门禁开放；P2保持8/101，P3正式验收保持0/38及0/4门禁。全部阶段闭环标志仍为false。唯一监督会话为 `01a13917-a6b4-78e0-bd31-44600dd87b79`，检查点为 `artifacts/p1-supervision/resume-root-v49/resumption.json`；旧监督会话停止派工。

第49轮 P3-01 规则解析原型组件 ✅ 已完成：保留 CA1859 失败后，本地 SDK10.0.402 严格 Release 构建及固定24/24测试通过。真实 lexer/token tree 产出源码范围、规则臂、ident/literal/tt捕获及嵌套/分隔重复结构，拒绝和取消均有界。[原型证明](../evidence/p3/macros-rule-parser-prototype-v1.json)不声称调用匹配、替换、展开、卫生性、歧义/基数语义、固定SDK CI或82项完整编译器用例。P3仍 🚧 进行中；正式叶子0/38、门禁0/4及前置依赖不变。P1 argv候选与真实三方probe仍NOT_RUN；生产containment模块未提交且未验收。

## 第50轮：Windows 生产 containment 模块

显式 Windows opt-in 组件 ✅ 已完成：新鲜严格库/夹具构建与全部8项控制通过（7项真实运行、1项结构变异）。真实cmd三方对照的标准/旧/候选序列化退出码为0/1/0，8种特殊参数往返精确匹配。[独立root证明](../evidence/p1/windows-production-containment-v2.json)检查全部7份实际报告及20个精确身份均不存在。瞬时子进程和夹具自然退出0；birth/terminal严格位于连续观测2→3之间，Total2/保留1仍按失败关闭/fullfalse处理。stdout+stderr合计实时预算及二进制CapturePrefix兼容通过，进程上限未改；保留第47/48轮失败。库项目为 `eng/RustSharp.ProcessContainment/RustSharp.ProcessContainment.csproj`；调用者须构建并绑定精确DLL哈希及显式lease/caps。Linux containment仍UNKNOWN，历史CI仍parent-exit-only，此组件不关闭任何P1门禁。P1仍 🚧 进行中、六门禁开放；P2保持8/101；P3正式叶子0/38、门禁0/4；全部阶段闭环标志仍false。
