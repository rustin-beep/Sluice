# Sluice Lite · 设计文档 v2

| 项 | 值 |
|---|---|
| 文档版本 | v2.33-lite（v2.33：平行链事故批——vivarium PR #4 实弹三缺陷裁定与修复：**Bug 1（主）push 基点解析错误、重放链吞进 PR**：根因=规划期基点唯一信息源是本地引用，而 REST 不回写 + seal 封 fetch 使其结构性陈旧（闸门⑦只防同名两侧异 sha，防不了两侧同陈旧——本次落点 0130bc8=PR#1 之前的 main）；基点过远 → 远端已有段整体重打，而 GitHub 签名的 squash 提交经 POST /git/commits 重打必丢 gpgsig → SHA 分叉 → 平行链（本地单笔提交变 PR 4 提交/154 文件/mergeable dirty；blob 去重 1356 成功恰掩盖 commit 级未对齐——传输优化与对象图保真正交，只做了前者）。修法=**执行期锚点收窄**：新建分支时 GET 远端默认分支头（FetchDefaultBranchTip 拆分，与 blob 去重同源复用免重复往返），`ResolveNewBranchAnchor(hasDeclaredBase, declaredIsTip, tipIsAncestorOfHead)` 纯函数入锁——tip 是 HEAD 祖先即取为锚（声明基点陈旧/全历史两形态同收窄，远端已有提交复用原 SHA 不进重打循环；收窄 ⊆ 弹窗范围=v2.12 先例；stderr 披露收窄行），tip 非祖先（分叉/独立历史）→ 维持声明（特性分支意图）/全历史兜底；重放通道锚=声明的新基点（语义是替换旧头）不收窄。**Bug 2 REST 不回写本地引用自锁环**：不回写 + 指引 fetch + seal 封 fetch 三点成环（sluice 自推分支的增量/重放通道结构性不可达；§9.6 实弹 124 条均 git 侧 tracking 分支，此盲区未覆盖）——修法=push 成功后 best-effort `git update-ref refs/remotes/origin/<B>`（UpToDate 同步回写；**红线收窄**："Sluice 不写本地 git"改述为"除 refs/remotes/origin/* 回写外不写本地 git"——回写=fetch 的引用等价物，发生在 approved+executed 之后，非写动作，闸门语义不变；**NewSha 对象不在本地库时回写必败，该失败即平行重打哨兵**——分叉的服务端 SHA 从未在本地存在，GitExists 先判定把分叉与普通失败分开）；PushDone refsNote 三态（已回写/分叉哨兵点名用户 fetch/其他原因），审计 result 附 refs: 注记。**Bug 3 Notice 化石语法**：`--remote origin --refspec` 系旧 plan.md PushReq 字段照搬（实际 CLI --repo/--branch），按指引自救必报"缺少 --repo / --branch"直接加剧 Bug 2 死结——已正为 `--repo <owner>/<repo> --branch <分支>`。git 封装注释改述（除 refs/remotes/ 回写外全部只读）。selftest：锚点决策 6 景 + pushdone 三态样例（pushdonewb/pushdonediv）+ 措辞锁（已回写/提交对象不在本地/origin 引用状态句）+ 变异自证（DefaultTip 臂改 Declared → 2 景红 → 还原绿）；v0.7.8；v2.32：审阅修复批——外部审阅 v2.30+v2.31 双提交（1 P1 / 3 P2 / 8 P3）逐条裁定：**P1 幽灵 pending 已修**（入闸后 ErrBad/BadRequest 一族早退不落行，残留 pending 锁死 (op,repo,target) 达 StaleAfter 且 exit 10 的 after_fix 契约当场失效——修法取单点：Audit.NonReadAppends 水位计 + TryEnterGate 入闸时 GateArmed 武装 + Main 的 finally 水位未涨即补 **abandoned** 终态行（result=early_exit:exit_N，覆盖全部 13 处早退）；abandoned 入收割族 StatusAbandoned/10/after_fix；实弹双冒烟：vivarium v3-smoke-p1 空范围推送 → pending（window:nonce）+abandoned 同秒落盘、立即重跑即时放行、MCP 回执收割 StatusAbandoned 全链通）；**P2 rule 7 三源同步已修**（AGENTS.md/§8.4 与 SetupCommand.Snippet 对齐；新增 ReviewFix232Checks ①AGENTS 三源锁——标记块==Snippet 常量这一机械可比面入锁，"不锁散文靠评审"的豁免自此收窄）；**P2 变异洞已修**（S6b 入锁：CLI 行 dedup_blocked 不得置 matchedTerminalSeen——变异 `!="dedup_blocked"` 改恒真曾使 selftest 仍 PASS，正是 E0 要挡的第二扇窗；变异自证：复现变异→S6b 红→还原绿）；**P2 check-then-act 已修**（改**占位-扫描序**：pending 行先 append 后扫描，result 带 8 位 nonce；文件行序即全序，并发双发只有最早占位者开窗；败者 dedup_blocked 自赎（只解析同 req 的 pending——req 非空行相互解析，CLI 败者幽灵与 CLI 胜者同生共死无害）；收割链 depth 限 1——双竞态互相退让（A 赎 B/B 赎 A）时照录字面防环）；P3 逐条：**3.1 push 重放 CLI 关联已修**（Matches 增 branch@ 前缀匹配（@ 界标防 v3/v3-libc 误撞），重放终态行自此能解析裸分支 CLI pending，S10 两景入锁）；**3.2 裁定维持 target 级键**（窗口堆叠防护是本意——同 PR 两条不同评论在窗期排队 ≤60s 是接受代价；DedupBlocked 措辞 同操作→同目标 对齐，措辞入锁）；**3.3 搜索空页脚已修**（SearchFooter 纯函数 + 零命中/页码越界同形特判，旧版渲染"显示第 1–0 条"；入锁）；**3.4 delete_head_* 已修**（入 Success 族 + StatusMergeTail——approved 之后的收尾行不再把已生效的合并收割成"结果未知勿重发"，成败均不动摇合并结果；双样例入锁）；**3.5 已修**（ReadRecentRows 环形缓冲：顺序单遍只留末 2000 行，免全文件物化）；**3.6 已修**（api 通道 + TryReadMeta 静默复读审计行 auth→**auth(fallback)**，与显式 --auth 可区分）；**3.7 已修**（页脚/代理对截断入 ReviewFix232Checks——切点 120 落高代理位回退 119 实现本正确，补 119 位高代理回退 + BMP 边界两景样例）；**3.8 AOT 已验**（审阅沙箱 LNK1181 系环境限制；本批 v0.7.7 正常环境 publish + rename-swap 照常）；正面项入册：G8 issuer 校验实弹挡下 SteamTools MITM（读通道被劫持时按设计拒绝并给准确指引）——审阅者因此无法联网实弹 issue list，非代码缺陷；附带发现留观：push 受保护分支/--force policy tripwire 不落审计行（"拒绝也记"原则漏网，发生面=永禁路径零歧义，低危）；selftest +5 样例 + 14 景去重锁 + v2.32 审阅锁；v0.7.7；v2.31：pending 行与同操作去重批——E0-20261108 实弹（rustin-beep/vivarium v3-libc，audit 4210-4223 行）三缺陷：①未决请求台账零痕迹——sluice_status 的"尚无终态"无法区分"窗口在等"与"回执不存在"，agent 盲等后误诊"终态回执永不落盘"（审计复核证伪：两回执均有终态行且现查即得——3d3608be approved/executed、14fb4b0f http_failed/not_sent，所谓"永久 pending"是轮询止于终态落盘之前的误判）；②同操作未决重发无闸门强制——agent 两连发 + CLI 一发，三窗并发堆叠、用户为一次逻辑操作 6 连点（弹窗疲劳实弹首例，review.md 头号风险落地）；③http_failed 收割与同步路径指引相反（status 落 StatusOther/21/wait_user"勿重发" vs live 20/retry_ok"可重试"——回执 14fb4b0f 实弹）。处置：**请求入闸即落 pending 行**（DedupGate.MarkPending；TryEnterGate 单源=TryPreflight 尾部 + push/pr_comment/pr_create/pr_merge/branch_delete/pr_reopen/issue_close/workflow_dispatch 八内联 verb；pending 自此对 status 与去重全程可见，LastFor 增返 Ts）；**同 (op,repo,target) 未决去重**（FindPendingCore 纯核心七景入 selftest 锁：同 req 终态收割 / CLI 行按同目标终态关联 / dedup_blocked 不解析任何 pending / 孤儿超龄放行——死窗不永久占坑；键=target 审计形态，push 用裸分支名）；命中→不开新窗：落 dedup_blocked 行（result="pending:{原req|cli}"），MCP 前门走 23+DedupBlocked 并入原回执（回执以 ReceiptOpen/Close 定界随文，McpServer.ExtractReceipt 提进 structuredContent——子进程快路径退出码 23 自此合法；server 超时路径与 dedup 快路径统一以消息定界为准，SpawnCli 不再回填自产 GUID），CLI 前门走 5+DedupBlockedCli（无回执不挂 23——不造永查无终态的回执）；RunStatus 收割三新增：pending 行 fresh→StatusPending、超龄→StatusOrphan 孤儿点名（"永久 pending"体验根治：回执要么等来终态、要么到点被点名孤儿——查证后可重发，孤儿不阻塞新请求）；dedup_blocked→链式跳原回执终态（inner==receipt 防环，cli 回放终态语义）；http_failed→StatusHttpFailed 入收割族（20/retry_ok 同 live，网络层描述自此随审计行 result="not_sent:{describe}"——10 处写路径 catch 全改，DedupInnerOf/NotSentDetailOf 纯函数入锁；pr_merge delete_head 兜底行同步附 ex.Message）；selftest +4 样例（statusorphan/statushttpfailed/dedupblocked/dedupblockedcli）+ v2.31 锁节；AGENTS 片段 rule 7 措辞更新（重发会被去重并入原回执，不再另开新窗）；遗留：bad_request 族（non_ff/has_merge/base_missing 等）收割仍照录字面未重建 live 消息（重建需 plan 语境，发生面=慢执行+慢决策交集，低频）；v2.30：api 读通道匿名回退批——v2.24 TryReadMeta 模式推广到整个读通道（RunApi 与 issue list 共用单源 ApiGetWithAnonFallback）：匿名撞 **401/403/404/429** 且 vault 为静默格式（SLCV2，同 `--auth` 零交互姿态——读无破坏力不弹批准窗）→ 以 token 复读一次，**复读结果即最终结果**（分类/审计同 `--auth`——双失败时"token 无权/私有库"归因诚实，不再给"加 --auth"这条已内部试过的死指令）；Hello vault 不复读（门控前多一道生物事件=弹窗疲劳，v2.24 同纪律）；vault 异常退回匿名结果不阻塞（同 TryReadMeta）；匿名尝试照常落读审计（回退时 audit 两行：anon + auth）+ stderr 一行过程提示。背景（2026-11-05 实弹）：匿名配额 60/h 耗尽后 agent 读操作全数 403 卡死（15:08–15:15 十余分钟数十条读把窗口烧穿），同刻 `--auth` 200 且配额几乎全新（used 2/5000）——「登录了自己的账号」≠请求带认证，api 默认匿名是 v2.2 的 V1 时代低摩擦默认（带 token 读=一次指纹），SLCV2 下零交互的原始摩擦已消失；**实测 fine-grained PAT 对白名单外公开库全可读**（Metadata 强制权限覆盖 repos/issues/actions 端点——moonbitlang/moon 的 issues/2254 与 actions/runs 均 200），复读翻盘面远大于预期，双失败只剩"私有库不在授权列表"与"真不存在"（既有 404 两可措辞已覆盖）；actions 端点对匿名收紧的 403（非配额，v2.7 已注）同批受益。触发面纯函数 **AnonReadShouldFallback 入 selftest 锁**（401/403/404/429 触发；2xx/3xx/422/5xx/0/-1 不触发——扩大触发面=每次对应失败多一次 token 请求与审计行）。`--auth` 语义不变（首请求即带凭据，省一次匿名浪费）；匿名窗健康时直通匿名成功零 token 消耗（issue list 实弹 12635B anon）。实弹验收：Vitro actions 匿名 403→复读 200（audit 两行 281B anon + 13071B auth）、moonbitlang 同过、issue list 窗口恢复后匿名直通；v0.7.6；v2.29：issue list 读侧清单面——agent 实弹反馈「issue 只能逐个查看太费劲、不能按标签或关键词搜索」：api 通道（§9 v2.2）本可承载（?labels= 过滤、search/issues?q= 端点），可用性不在——repos/issues 原生 JSON ≈4KB/条（默认页 30 条 >100KB，输出被截断后 agent 退化成逐条 issues/N 拉取）、端点混入 PR（issues API 对 PR 编号同样应答，issue_close 预检早知此坑）、搜索需 URL 编码知识（空格 %20/CJK %XX——agent 绕开的直接原因）。修法 = 同一读通道之上的紧凑渲染 + 参数化过滤，不新增通道不进写面：①新命令 `sluice issue list --repo R [--state open|closed|all] [--labels a,b] [--search 关键词] [--limit 1–100] [--page N] [--with-prs] [--auth]`——清单模式 GET repos/{r}/issues（state/labels/per_page/page/sort 全参显式，不依赖端点缺省——缺省漂移即行为漂移）；--search 走 search/issues（q=repo:{r}+type:issue[+state][+label:]… 整体 percent-encode，调用方零编码知识；不传 sort——best-match 即搜索本意，清单端点才是 created 序）；输出一行一条 #N [state·reason][PR] 标题≤120 截断（不切代理对）{标签}——比原生 JSON 缩约 50 倍；②端点混 PR 处置：默认隐去但计数披露（「另有 N 个 PR 已隐去，--with-prs 显示」——隐去≠不存在），--with-prs 显示带 [PR] 标；③--search 与 --state all 互斥 fail-closed（搜索端点 state 限定符只有 open/closed，all 会静默缩窄成缺省 open——v2.8「不可辨即说破」纪律）；④清单端点无总数：满员提示可能还有下一页、不足即末页，不猜总数；搜索端点 total_count 透传；⑤读通道单源收敛：RunApi 内联的 --auth 凭据获取与状态分类两段提出 TryAcquireReadToken/ClassifyApiReadResult（行为不变），issue list 共用——限流嗅探（v2.10）/403 拆分/匿名 after_fix/3xx 不跟随（v2.7）/审计行一表两用，审计 op 恒 api_read（读通道单源，op 参数只进终态文案）；⑥面一致：ReadOps 登记 issue_list（不进 CLI 写动词表/MCP Actions）；查询拼装（IssueListPath/IssueSearchPath）与渲染（RenderIssueList）纯函数入 selftest 锁（标签逐名编码/搜索 q 结构/含空格标签引号短语/PR 隐去计数/截断 120/total_count 透传）；⑦MCP ToolDescription 读指引（issue list 提示补进「只读查询不要用本工具」句——抱怨的 agent 正是从这里找路）、README 命令表、Usage、AGENTS.md 片段规则 6（SetupCommand.Snippet/§8.4/本地 AGENTS.md 三源同步）补面。pr list 不做（本批）——同型诉求实弹出现时按同模板加；v0.7.5；v2.28：ImageChanged 双哨兵洞一行修——同日评审 P3：启动时 ProcessPath 即不可解析 ⇒ StartupStamp=(-1,MinValue)，此后每次 StampOf() 同为 (-1,MinValue)，`startup != now` 恒 false ⇒ **永不判过期**——v2.26 的注释与 selftest 都声称"文件消失（-1 哨兵）同判过期 fail-closed"，但锁只盖了（真实值,-1）方向，(-1,-1) 破了声称的不变量（恰好是最该 fail-closed 的形态：映像信息完全不可知却继续服务写门）。修法：`startup.Size < 0 || startup != now` 一行（startup 侧哨兵前置短路，now 侧 -1 由不等比较天然覆盖）+ (-1,-1) 入锁；v0.7.4；v2.27：收割语义统一批——74439f9 四提交评审处置（1 P2 + 2 P3 代码面 + 1 P3 文档面）：①**P2 收割路径 isError 两族分叉复发**——StatusFamily（v2.25）只映射 denied/timeout/approved·up_to_date 四族，**consent_failed 与 rate_limited 都是批准后才落审计的 decision**（恰落在 pending 收割窗），收割成 exit 21/isError=true/"勿重发交回用户"，与 live 同 decision 的 6·wait_user·"等用户口头允许再重跑" / 22·wait_until·"按窗口等待后重跑"指引相反——落哪边取决于人是否 15s 内点完，**非确定性契约**；v2.24 立的"用户决策族不得标错误"纪律没照到收割面。修法：StatusFamily 补 consent_failed→6 / rate_limited→22（cooldown 无生产审计行且未接线，不映射——映射而无渲染分支只会造出新分叉，接线时同批进）；RunStatus 单源复用 ConsentFailed / WriteRateLimited·PushRateLimited（push 单列——"分支未动、已传 blob 幂等"事实句不谎称零副作用）；rate_limited 审计 result 自本版存 RateLimitWhat 结论（"http_403"→"http_403:{what}"，push 沿用 not_executed:{what}；≤v0.7.2 旧行收割回退通用等待句）；LastFor 增返 repo；**收割↔live 反向锁入 selftest**（每 decision：StatusFamily·RetryOf ≡ live 样例 exit·retry——live 样例即基准，两表再漂移当场红）+ RateLimitWhatOf/HttpStOf 解析锁②**P3 status 动词参数误报"未知命令"**——原模式 `["status", var receipt]` 只匹配恰两元素，`sluice status`（缺 receipt）/多参落 BadVerb："未知命令 status"+动词清单不含 status+提示重新 publish——已知动词的参数错误被引去重新发布；修法：`["status", ..]` 单列 arity 收口走 ErrBad 用法句（exit 10 after_fix，实弹验收：缺参/多参/坏 receipt 三形态全走用法句），BadVerb 动词清单补 status/notice/doctor③**P3 up_to_date 收割输出自相矛盾**——"已获批准并执行。审计结果：not_executed"（agent 读到"执行了/没执行"二义）；单列 StatusUpToDate（"已获批准；远端已在目标提交，无需执行"，exit 0/—，入 selftest 样例表）④**P3 明文窗口基线重校（文档面）**——v2.24 TryReadMeta 静默回退起，hello:false 姿态在**批准窗之前**也可能解 vault（匿名 403/404 → token 复读一次；IsDpapiVault 闸，hello:true 不受影响；同用户本可直解 vault 文件，L1 让步在册，无新增暴露）——"明文仅在批准→响应窗口"的旧审计基线（review.md B 组）作废，§5 基线改写为"首次解密（可能是批准前预读回退）→ 进程退出"，"无常驻明文持有者"不变⑤§8.3 exit 表补 v2.25 起缺失的 23 行（pending/非终态/receipt 收割）；v0.7.3；v2.26：僵尸自曝与收割入带批——2026-11-04 晚 v2.25 部署**当晚复发**（用户报告"CI 触发指令被拒时输出的指令 agent 读取不到"）：根因不是 v2.25 机制失效，是 **rename-swap 部署的系统性缺口**——18:27 v0.7.1 落盘后，18:15–18:16 启动的四个 v2.24 旧 server 仍在服务既有会话（存活会话句柄随旧名文件继续跑旧代码；v2.18 只记录了"锁文件→rename 腾位"，没记录"旧 server 继续按旧语义丢终态"这层语义债），其 SpawnCli 无限阻塞 → 宿主 ~30s 弃等 → 19:04:47 dispatch 拒绝理由再度沉没；审计 req 字段有无为证（同刻新旧并存：19:00 push 带 req / 19:03 push、19:04 dispatch 不带）。修法两件：①**过期自检守卫**——server 启动快照自身映像 (大小, 修改时间UTC)，每次 github_write 调用前复核，磁盘映像已换 ⇒ 显式回 ServerStale（exit 1/wait_user/isError=true，"转告用户重启会话"）拒绝服务——把"静默丢终态"变成"响亮可行动的失败"；sluice_status 豁免此闸（audit 格式跨版本稳定，僵尸 server 上的在途 receipt 也值得收割——fail-open 只让给只读收割，写门永远 fail-closed）；ImageChanged 纯函数入 selftest 锁（未变放行 / 大小变 / 时间变 / 文件消失 -1 哨兵全判过期）。②**收割入带 sluice_status 工具**——v2.25 的 pending→`sluice status` 收割是提示层动作（agent 须切 Bash 抄命令，服从概率性，且实测全部工作区 AGENTS.md 均未装片段）；v2.26 增第二个类型化工具 `sluice_status(receipt)`：spawn CLI `status <receipt>` 单源复用 §8 消息与 StatusFamily 映射，server 零收割逻辑（spawn-child 纪律不变，无窗口毫秒级恒快路径）；github_write 的 action 值域不动（16 写 op 面一致锁不受扰——status 是独立工具非 action，"api 读 v1 不进 MCP"红线不破：status 读本地审计非 GitHub API）；PendingGate 文案与 github_write 工具描述改指工具优先（CLI 等价命令保留）；AGENTS.md 片段规则 7 同步；tools/list 双工具。当日处置：四个僵尸 server kill（其一自退）、v0.7.1 存活 server 无守卫——部署 v0.7.2 后它们同样过期不自曝，会话自然重启前仍可能丢终态（如实告知，不静默）；v0.7.2；v2.25：pending 收割批——2026-11-04 实弹第三缺陷（用户报告"agent 根本获取不到我填入的信息、CI 动作一直失败或超时"）：**宿主客户端 ~30s 弃等工具调用 < 人类决策时长 40-90s**（读窗+选预设+打拒绝理由），终态永远落在客户端离场之后——deny_reason 自 v2.24 起格式完好却一世无处投递（当日 05:28/05:54 两次 dispatch 拒绝理由原文全在审计、agent 零接收，"从不记忆"的根源）。修法 = **服务端限时未决即回 pending**：SpawnCli 等待 `Gate.McpPendingSec`（config "mcp_pending_sec"，默认 15s，夹 [5,55]，必须 < 一切已知宿主工具超时）仍未决 → 本请求回 exit 23（wait_until、isError=false、非终态）+ **receipt**（16 位随机，env SLUICE_MCP_REQ 注入子进程，子进程全部审计行随身携带——同 via 纯标签纪律）；**活窗不杀照跑**（§9.7 红线不变），终态落审计；新动词 **`sluice status <receipt>`**（Audit.LastFor 逆序找该 req 最后一条非 api_read 行）收割——denied/timeout **单源复用原决策消息**（拒绝理由原文与"必须遵守"条款原样再现，StatusFamily 纯函数入锁：denied→2/timeout→3/approved·up_to_date→0/其余→21），approved 族 StatusDone 事实句照审计 result，其余 StatusOther 照录决策字面。快路径不变（15s 内落地的批准/拒绝仍原样直返）。**实弹验收**（2026-11-04，#999999 无害目标）：pending 15.1s 返回（<30s 客户端超时）→ status 凭 receipt 收割 not_found 终态 exit 21 ✓；同窗顺带实证 v2.24 匿名 404→auth 404 回退落审计 ✓。新消息四条（PendingGate/StatusPending/StatusDone/StatusOther）入 §8.2 表与 selftest；AGENTS.md 片段 +规则 7（pending 非 failure、status 收割、终态前禁重发）；v0.7.1；v2.24：拒绝可见性与预读降级批——2026-11-03 Vitro #452 实弹用户报告两缺陷（"合并分支时看不到什么合并成什么，显示私有仓库或数据混乱"；"拒绝理由写入后 agent 无法得知为什么拒绝，是直接错误显示"）：①**MCP isError 语义拆分**——用户决策族（denied/timeout/policy/cooldown/consent_failed/rate_limit_wait）isError=false：工具没坏、是闸门说了不；isError=true 会被宿主按"工具出错"渲染（折叠/摘要/归为可重试故障），拒绝指引与自由文本理由随之沉没——实弹时序：14:50:59 与 14:52:59 #452 两次 denied、同一理由两次填写（"452不是没跑完吗？453跑完了吧？"），agent 仍于 14:52 重发 merge（违反 retry=never）；ok 与 isError 解耦（ok=code==0；拒绝=ok:false+isError:false）；**denied/deny_reason 进 structuredContent**（定界符 DenyReasonOpen/Close 与 Messages.Denied 单源，ExtractDenyReason 往返入 selftest 锁——宿主即便摘要化 text，机器可读字段随身携带）②**TryReadMeta 静默 vault 认证回退**——匿名 403/404（配额限流/私有库匿名不可见）且 SLCV2 vault 时以 token 复读一次（同 `api --auth` 零交互姿态：读无破坏力、不弹批准窗，读落审计 auth 标记）；实弹：14:50:15 匿名读 pulls/452 → 403（278B，同库早间匿名读尚 200 = 配额 60/h 耗尽）→ 弹窗退化为"方向：? ← ？"且标题行误报"私有库或网络原因"，同刻 `api --auth` 18168B 200——agent 比批准窗看得还多，批准决策面反而最瞎；pr_merge/pr_reopen/issue_close/branch_delete 四处内联匿名预读收敛 TryReadMeta 单源（v2.11 收敛令的漏网四连），branch_delete 连带关 PR 披露同沾（匿名限流时 PR 关联警示静默丢失恰在最需要的时刻）；Hello vault 不复读——门控前多一道生物事件 = 弹窗疲劳，维持降级+准确归因③**MetaUnreadableWhy 归因单源（入锁）**——403 不得再写"私有"（高频真因是匿名配额耗尽）；404 匿名两可（私有库/不存在不可区分，v2.8 纪律）、带凭据 404 收窄为"确不存在或无权"；-1/0 如实"未发出/超时"；pr_merge/pr_reopen/issue_close 弹窗降级文案全走此源④**pr_merge 指纹弱锚定披露**——元数据不可读时批准指纹混 "?" 且执行前 head 复核不可用，此前静默降级；弹窗警示行说破"未锚定 head 版本、执行前复核不可用，合并对象请自行核对"⑤**selftest 拒绝可见性锁**——isError 两族穷举（7 决策码必 false / 7 技术码必 true）、deny_reason 往返（含无理由→null）、预读归因措辞面三断言；v0.7.0；v2.23：审阅修复批——d0f817c–8c3e770 五提交评审处置（1 P1 / 3 P2 / 5 P3）：①**P1 哨兵 /TR 引号**（d0f817c）：原 `-File {ps1}` 不带引号、注释声称"LOCALAPPDATA 与 exe 路径均校验过不含空格"而实际无任何校验——LOCALAPPDATA 含空格（Windows 用户名含空格即中招，常见形态）时 powershell 把 "C:\Users\John" 当脚本名，任务每 5 分钟静默失败且连 sentinel.log 都不会有（脚本没跑，doctor 只查任务注册 → 全绿假象）；修法 = -File 恒带内层引号 + /TR 内层 `\"` 转义（SentinelTr/SchtasksCreateArgs 纯函数入 selftest 锁），**2026-11-03 实弹**：含空格路径注册 → XML 拆分 Command/Arguments 正确 → /Run 执行成功（marker 落盘）②**P2 doctor 暂停感知**（e247910 先落、8c3e770 未回补——v2.22"doctor 顺手补暂停态显示不做"的决定随评审推翻）：暂停期内 seal 预检显示"暂停中（至 …）"而非误导性"无违规"；notice 摘除是 pause 的预期效果，不再建议 setup --install（照做=把刚摘的 helper 装回去，与暂停语义直接冲突）；新增"过期未愈"显示（哨兵 ≤5 分钟自愈 / 手动 --resume）；暂停期内 notice 在位（直推将被拦，与暂停目的冲突）如实报红③**P2 doctor notice 检查收紧**：`Contains("notice")` → ProcessPath 前缀精确匹配并单源化（NoticeValuePresent，与 InstallNotice 幂等检查共用）——指向已删除旧 exe 路径的残留条目、任何路径含 notice 子串的无关条目不再误判绿（doctor 的职责恰是发现漂移）④**P2 state.json 信任面入册+抬门槛**（§14 新 L11）：state.json 同用户可写 = 检测层可被一行写入未来时间戳静音（哨兵静默 / seal 门放行 / seal 显示暂停中；写操作本身仍需批准窗，token 不因此泄出）——处置 = 哨兵 ps1 与 doctor 交叉核对 audit.jsonl 的 seal_pause 记录（state 值无逐字对应审计记录 → toast 疑似伪造告警；ToStateString 三方逐字一致入 selftest 锁）；完整方案（vault 同源密钥签名）不做入册——audit/签名公钥同为同用户可写，增量有限⑤**P3×5**：InstallNotice 失败消息照实（半装状态：--unset-all 已执行、空串可能已落盘——原"未产生任何变更"与事实不符，fail-closed 方向不变）；暂停截止的 ✓ 输出/审计用批准后实际值（SealPause.Begin 改返实际值；弹窗正文才是预估）+ 跨午夜显示带日期（UntilLabel：同日 HH:mm / 跨日 MM-dd HH:mm，入锁）；InstallSnippet END 搜索限定 BEGIN 之后（ReplaceSnippetBlock 纯函数入锁——AGENTS.md 前文引用标记字符串不再错切）；sentinel.log 超 1MB 滚动 .1 单代（违规持续期间每 5 分钟全量追加无上限会失控）；Git() 花括号格式回归；v0.6.9；v2.22：人类工作流收尾批——§6 最后两个设计项落地，V1.5/小项清账完毕：①**`seal --pause <分钟>` / `seal --resume`**：批准窗门控（V1=Hello/SLCV2=点击，与写动作同待遇——agent 发起同样要过闸）；批准后摘 notice helper + 摘 SSH 屏蔽块（标记块外科摘除），state.json 记 pause_until；**暂停短路单点 = SealCheck.Run() 开头**——九处写门/seal/哨兵零散改全继承；到期自愈双路 = seal no-args 过期检测先 resume + 哨兵 ps1 过期跑 resume+toast（最迟 5 分钟，不靠人记得）；resume 无闸门（恢复防护是安全侧——开门要批准关门不用），resume = 清 state → 重装 notice → seal --fix 清期内新凭据；暂停期哨兵静默②**`sluice unseal`**：批准窗门控；摘 notice/删哨兵任务/摘 SSH 块/清暂停态，vault 不自动删 + 善后清单逐项说破（§6 红线）；③哨兵 ps1 升暂停感知（未到期静默/到期自愈+toast），Show-Toast 提炼双路复用④**实弹**（2026-11-03，无闸路径全测）：活跃暂停+GH_TOKEN 注入 → seal 放行 exit 0；哨兵静默；过期 → 哨兵触发 resume：state 清/notice 回位/credential fill 重新被拦/toast+日志；seal no-arg 自愈同验；pause/unseal 的批准路径与写动词共用 RunLocalGated（结构同形，留待首次真实使用）；⑤doctor 顺手补暂停态显示不做（seal 已显示，doctor 保持六项）；v0.6.8；v2.21：doctor+片段批与遗留清账——V1.5 两项落地 + 四笔遗留入册：①**`sluice doctor`**（V1.5）——六项只读健康汇总：版本戳/vault 形态（V1 Hello vs SLCV2 说破 L1 让步在册）/config 解析/seal 预检（写路径同款轻量检查）/notice helper（含 gh 桥接条目重现检测）/哨兵任务注册；全绿 0 有红 1，只读不修（修复动作各归各位）②**`sluice setup --install-snippet`**（V1.5，§8.4）——AGENTS.md 片段安装器：标记块（BEGIN/END 注释）幂等，重跑=刷新手改会被覆盖，无文件新建/有文件追加/已有块刷新三态实弹；§8.4 正文随之扩全动词面（v2.0 文本还停在 6 个 op 时代）+ 补 github_write 双前门表述 + git 直连将收到 notice 指引；正文与 SetupCommand.Snippet 常量同源同步（散文不进 selftest，靠本条入册纪律）③**遗留清账入册**：actions logs 数据体读取已闭环（2026-11-03 实弹：PAT 补 Actions:Read 后 `api --auth` 跟随 302→CDN 拿到 105KB/81KB/54KB 日志体，审计 http_200 auth 在案——incident-vitro 遗留 #2 关闭，#3 README 指引早已有之）；**L6 销账**（push 纯 REST 定稿后 token 不进任何子进程，"1h installation token"不再需要）；重放通道实弹早已充分（审计大量"重放替换"行——incident 遗留 #1 事实上关闭）；④Sluice 仓库自身装入 AGENTS.md 片段（吃自己狗粮）；v0.6.7；v2.20：哨兵落地批——§6 哨兵从设计走向实现（评审认定"最可能的失效方式 = 三个月后 gh auth login 忘掉"的检测网）：①**`sluice sentinel-install`**——写 `%LOCALAPPDATA%\Sluice\sentinel.ps1`（烙印安装期 exe 绝对路径）+ schtasks 每 5 分钟（交互会话内），**检测逻辑单源 = `sluice seal`**（与写路径 50ms 预检同一套，C# 侧零检测代码，复用映射 §11.5 定案的兑现）②**toast 双路**：BurntToast 模块（装了就用）→ WinRT 原生兜底（PowerShell AUMID `{1AC14E77…}\WindowsPowerShell\v1.0\powershell.exe`，零依赖零安装）③**PS 5.1 三编码坑修在位**：ps1 带 UTF-8 BOM（无 BOM 按 ANSI 解码，中文与 ✗ 全乱）；`[Console]::OutputEncoding=UTF8` 先设（原生输出捕获）；Add-Content 显式 UTF8④**实弹验收**（2026-11-03）：GH_TOKEN 注入 → exit 7 + toast + sentinel.log 正确识别环境变量项；干净环境 → 静默 exit 0；任务注册 Next Run 确认⑤边界如实：检测不是阻止（N4），最迟 5 分钟看见；哨兵跑的是轻量预检非深扫；v0.6.6；v2.19：notice 落地批——§9 消息型 credential helper 从设计走向实现（README 架构图早画了它，文档↔实现漂移清账）：①**`sluice notice`**（git 索凭据 get → stderr 打 §8 指引、非零退出、stdout 永无 `password=`；store/erase 静默成功；每次 get 落审计 op=notice——T3"换路径硬闯"的第一手信号，Vitro 复盘里 push 被拒→找凭据那条链从此有现场记录）②**`sluice setup --install`** 安装器写**作用域键** `credential.https://github.com.helper`：`--unset-all`（多值键普通 set 必败——gh 曾写多条，实弹踩到）→ `--add` 空串（清 github.com 从系统层继承的 GCM，其他 host 不受影响——外科手术式，SSH 屏蔽块同纪律）→ `--add` 本 exe notice；**顺手清除 gh 的 `!gh auth git-credential` 桥接条目**（gh 登录状态下直供 git，§6 侧门面）；安装器路径含空格宁拒不装错（helper 值经 shell 分词）③新消息 Notice 入 §8.2 与 selftest 表（exit 4 / change_approach——"直推 git"这条路永久禁止、点名允许的替代，policy_reject 同族）④**实弹验收**（2026-11-03）：`git credential fill` / `git push --dry-run` 私有库 → 指引文案 + 无凭据 + 快速失败；审计落行 ✓。**两个意外收获**：装前基线探针实证——任何进程跑一次 `git credential fill`，GCM 就在用户屏幕弹 GitHub 登录窗（诱导登录=重新开门，N4 相邻面；空串把它从 github.com 链上摘掉，比"裸错换消息"更硬的收益）；git 在全部 helper 失败后的 askpass/终端回退层无人交互下自灭、有人交互也拿不到有效凭据（环境无存量）——回退层不处理如实记边界⑤setup 尾部指引自"最后一步 seal"改两步（seal --fix + setup --install）；v0.6.5；v2.18：gate 崩溃诊断保真批——internal_gate_crash 六次复发（2026-10-30×2 push / 2026-11-01×4 pr_comment，push+comment 双通道实证崩溃点在 gate 公共路径）全部只留外层 `TypeInitializationException` + 通用占位 Message，InnerException 类型与堆栈被丢弃，两次"留观"皆无法归因——`Gate.Run` catch 改用 `DescribeCrash`：异常链外→内扁平化（" <- " 连接）+ 最内层堆栈前 3 帧（" | " 连接，AOT 帧名含类型与静态构造器名，是唯一定位线索）+ 600 字符截断；同一详情落 stderr（`[Sluice]` 技术前缀——MCP 前门下子进程 stderr 进 text，双前门都可取证）；`DescribeCrash` 纯函数入 selftest 崩溃诊断锁（三层链保真 / 链分隔 / 截断上限）。**处置定性**：偶发环境相关（同环境多数进程正常），不猜根因不写修复——下次复发的审计行自带归因材料，从"留观"升为"待复发即定位"。同批入册：MCP 前门已接线 ZCode 用户域（`~/.zcode/cli/config.json` mcpServers，AOT 发布件绝对路径——§9.7 拓扑落地，via:"mcp" 开始积累，A/B 探针数据自然生长）；**发布件锁约束实测**：常驻 mcp server 与 CLI 是同一 exe 文件，宿主会话存活期间 publish\sluice.exe 被锁（实测 16 个 server 进程锁文件，publish 报 MSB3027）——原地更新正解 = **rename 腾位**（Windows 允许重命名运行中 exe，存活会话的句柄随旧名文件继续服务）后拷入新版，无需杀会话；v0.6.4；v2.17：标签面批——动词面第七缺口（评审实锤：源码 grep 零 label 能力——非参数未暴露而是整条缺失；#409/#410 挂账无标签可挂，PR 侧同理共用 labels 端点）：①**issue_create 增可选 `--labels a,b,c`**——POST /issues 单请求携带（**单请求原子**：消除"issue 建成而标签被拒"的半成品态；POST 非幂等不重试不变量不变）；**未知名 fail-closed 预检**（匿名 GET /labels?per_page=100 分页 ≤5，未知名点名拒——GitHub 对未知名的处置未文档化且路径不一致：add-labels 据报 404、create 带标签据报静默自动建标签随机色＝窗外实体创建；预检不可用（私有库 404/超 500/网络）→ 弹窗警示行，v2.8 纪律）；labels 入指纹与弹窗，**无 --labels 时指纹逐字节不变**（既有审计行可复现）②**新 op `issue_label`**——`sluice issue label --repo R --number N --labels a,b,c`，PATCH /issues/{n} `{"labels":[…]}` 整组替换；**摘除清单披露**（pr_edit 写覆盖披露同族：弹窗列当前标签→新标签→将摘除——set 语义的静默摘牌面必须被看见才执行）；**PR 编号放行+说破**（与 issue_close 拦截相反的定性：挂标签无"错对象"可言，PR 与 issue 共用 issues 端点，拦了即堵死 PR 挂标签）；匿名 404 降级同 v2.8（当前标签不可读 → 摘除清单无法预生成，警示行说破"凡不在新清单者都将被摘除"）；空清单（清空全部）不做、标签实体管理不做（未知名正解=先建后挂）③**LabelsParse/LabelRemovals 纯函数入 selftest 标签锁**（trim/空段拒/重复拒——OrdinalIgnoreCase 对齐 GitHub 标签唯一性大小写不敏感；摘除=当前减新）④**面一致锁四方同步=16 op**（§9 两行 / WriteVerbs / MCP Actions+AllowedFields+schema / selftest 快照）+ **§9.7 action 清单存量漂移顺手清**（v2.11 批A 后停在 8 个——锁管不到散文，暴露锁边界）；MCP labels 数组→逗号拼接走 CLI `--labels` 语义单源；v0.6.3；v2.16：写通道限流嗅探批——**诊断分类谎言族全通道清账**（七提交评审 A/B1/B3 全实锤且 A 实际范围更大）：①**写 op 403/429 限流嗅探**——v2.10 只修了 api 读通道；ExecuteGated 七 op default 臂、pr_merge/pr_reopen/issue_close/branch_delete/workflow_dispatch 内联 403 臂、**GitHubApi 层 comment/pr_create 的 `401 or 403` 混归 Unauthorized**（403 权限拒也落 credential_expired 死方子——v2.5 拆分漏网两 op）、**push 通道**（ref 预查 403→Unauthorized + 序列中段缺 wait 语义——blob POST 密集面恰是 secondary 最高发处）全数接 `RateLimitWhat`（ParseRateLimit 重构：RateLimitText 兼容 raw body 与 ApiResult.Reason 纯文本；429 本身即限流无特征也归档）→ exit 22 wait_until；新消息 WriteRateLimited（"本次操作未执行"）与 PushRateLimited（"分支未动、已传 blob 幂等"——不谎称零副作用）；api 读通道 429 顺手同档（原落"其余非 2xx"）②**comment/pr_create 拒绝分类清账**：Other 原经 InternalPost 谎记 internal（服务端 404/422 不是 sluice 内部错误）——CommentOutcome（403→forbidden/WriteForbidden、404→not_found、422→pr_comment_rejected）；pr_create 403 同批 WriteForbidden、404 专属文案、audit outcome 去 internal ③**pr_close 预检次序**（B1：merged⇒state=closed，先判 closed 会给已合并 PR 开"pr reopen"死方子且 merged 终态检查成死代码；RunReview 同款次序本正确）④**branch_delete/workflow_dispatch 5xx 归 21**（B3：v2.11 改齐 PATCH 族时"留下批评审统一"清账——原落 10 谎称"未执行"；dispatch 5xx 可能已受理，重发=重复 run）⑤**闸门⑦ 扩斜杠名**（B2：feature/x 同吃 DWIM refs/heads/ 优先坑；`NeedsDisambig` 纯函数=非 sha、非 refs//origin 前缀皆入两侧消歧）⑥MCP prerelease **布尔白名单化**（C1：原 `||` 使字符串值必拒、"字符串 true"接受分支不可达；直接改 `&&` 会漏放 object 静默丢参——白名单=真布尔+字符串 true/false）⑦ci.yml 版本正则泛化 `v[0-9]+\.`（B4：防 plan-lite 升 v3 当天正则先红）⑧RunComment 内联 IsRepoName/Sha8 收敛单源（C3）+错别字（C4）。**入锁**：RateLimitWhat 8 断言（非限流 403 必 null / 429 无特征必归档 / Reason 纯文本可嗅探）+ NeedsDisambig 6 断言 + MCP prerelease 3 断言入 selftest；执行期读（blob 去重 3 GET/基点前移/重放复核）不落审计维持现状（C2 留观：push 审计按终态落行是既有纪律，扩面另议）；v0.6.2；v2.15：CI 防线批——**回归防线自动化**：33557dd→000da02 十一落地批的质量保证全是"当场跑一遍 selftest"一次性快照，仓内零 CI（.github/workflows 不存在），回归窗口真实存在；补 `.github/workflows/ci.yml`：push(master)/PR 自动跑 `sluice selftest`（消息表穷举+exit→retry 函数性+面一致+push 逻辑锁，**全离线纯函数**可上 runner——不碰 vault/不弹 gate/不打真 API，豁免"gate 要人点"红线；面一致锁在 checkout 内按 plan-lite.md 文档实文核对），master 另跑 AOT publish+**构建戳校验**（--version 的 sha 必对 HEAD 且干净 checkout 必不带 +dirty——戳对齐从人眼核对转为机械断言，v2.5 滞后 v2.6 那类漏抓从此红脸）；"每批 selftest PASS JIT+AOT"双跑纪律固化进 job 结构；exe 用 find 定位不硬编码 TFM 路径。**边界诚实**：CI 防的是纯函数回归/面漂移/构建断裂/脏戳，防不了 live 行为（vault 迁移/302/真 API）——live dogfood 仍人工，这是红线代价非遗漏；v2.14：push 卡慢与平行链处置批——①**闸门⑦ `--base` 裸名消歧**（git DWIM 裸名只试 refs/heads/ 永不试 refs/remotes/origin/*：worktree 本地分支常年陈旧，`--base master` 静默吃到影子引用，实测落后 17 笔把 2 笔增量放大成 19 笔重放/276 blob 三连坑；同名两侧并存且异 sha ⇒ 规划期 fail-closed 列两侧 sha+日期点名二选一 `--base origin/<N>`（荐）/`refs/heads/<N>`，仅一侧 ⇒ 采用并在弹窗披露基点来源+日期；裸名自此分支命名空间语义，tag 须写 refs/tags/<name>）②**CI 聋哑警示**（被推头树无 .github/workflows 而远端主干树有 → 弹窗警示行——on:push 工作流取被推头所在树，链根早于 CI 引入则结构性零运行=平行链事故终态症状；agent 在 sluice 外裸名 rebase 造链时推门前兜住）③**blob 树集去重**（>25 blob 才启用：远端默认分支头树 recursive 一次拉取命中即跳过，3 GET 换 N POST；逐 blob 探测否决——已存在只省请求体不省往返、缺失反成双往返；truncated/失败 ⇒ 全传降级；跳过数入 PushDone+audit）④**执行期心跳**（每 25 blob + 逐 commit 一行 stderr）；消歧/分区/聋哑判定三纯函数入 selftest 锁；v2.13：发版面批——缺口服方 #5 落地：**`tag_create`**（`sluice tag create --repo R --tag V --at <分支|完整sha>`，POST /git/refs——lightweight tag 直接指 ref 不建 annotated 对象（后续按需）；**create-or-fail 天然防覆盖**，v2.7「update-ref 永不自动重试」不变量原文覆盖；预检匿名读：tag 已存在=确定性拒绝（附既有指向，省一道弹窗）；--at 分支匿名解析失败（私有库 404 不可辨）→ 点名给完整 40 位 sha（本地 git 可得）；指纹=repo+tag+目标 sha）与 **`release_create`**（`sluice release create --repo R --tag V --body-file F [--target <分支|sha>] [--name N] [--prerelease]`，POST /releases——**target_commitish 锚定**：tag 不存在时以批准期解析的 sha 显式传入（--target 缺省取默认分支头解析），杜绝隐式建 tag 的 TOCTOU（pr_merge head-sha 同族）；tag 已存在则挂其上（target 不参与，弹窗披露）；**广播性披露**：弹窗明示"发布即通知所有 watchers 并触发 release 事件 webhook"；同 tag 已有 release → 服务端 422 already_exists 天然护栏（消息给查既有 release 指引）；拒绝消息事实句"未发通知、未触发 webhook"；指纹=repo+tag+锚点+正文。**资产上传不做**（另一端点族+二进制体量，发版正文场景用不上）。两 op 均走 v2.11 共用段（TryPreflight/TryReadMeta/RunGatedOp/ExecuteGated），5xx/超时→21，403/404/422→10 after_fix；面一致锁三方同步（§9 两行 / WriteVerbs / MCP Actions=15）；v2.12：推送序列诊断保真批——2026-10-31 实战缺陷报告三案处置。①**基点在场校验前移**（缺陷一主案：`--base` 给了本地-only 对象时，原执行序列在 blob 全部上传后才 GET commits/{base}，404→Other→internal——白传 blob＋烧批准＋谎称"可能已完成"三连，×3 复现）；前移到执行序列**首步**（任何上传之前，请求数不变——该 GET 原本就有只是来得晚），失败零上传零副作用；新 PushResult.**BaseMissing**→exit 10 after_fix（正解：推基点所在分支或换远端已有祖先）；RunPush 层**匿名预检** plan.Base（v2.8 纪律：404 警示行不硬拒——公开库提前警示省一道批准，私有库匿名不可见由执行期带 token 终判）。②**Other 族诊断保真**（缺陷三）：Other 兜底臂从 Internal(after=true)（无 Reason 槽位、exit 1/wait_user）改为 **PushStepRejected 渲染服务端原话**→10/after_fix；"可能已完成"对整个 Other 族都是假话——update-ref 是唯一写点且未执行，**分支必未动**（已传对象为内容寻址孤儿：无引用、不可见、GC 回收——"副作用已发生"的准确说法是浪费配额）；Internal(true) 从 push 路径绝迹。③**blob sha 不符→20 retry_ok**（内容寻址同内容必同 sha，不符=平台异常窗（Vitro 4×blob-500 同族），分支未动重传安全）；**提交数>200/对象>2000 前置到规划期**（原只在执行期=批准已烧）→10 after_fix 附分批正解，执行期保留为范围收窄后兜底。④**全历史形态警示行**（缺陷二=闸门⑤的无 --base 对偶面：hasTracking=false 静默全历史回溯，远古 submodule 在规划期即炸，fetch 提示只在成功消息尾巴）：弹窗警示行＋submodule/超限文案附 fetch 正解；**有意不 fail-closed**——闸门⑤的理由是销毁侧披露不可构造，此路径 FF 无销毁侧且批准内容⊇执行内容（执行以远端现状收窄），警示行即可不罚批准；v2.11：生命周期对称批——①**五 op 补全对象生命周期**：`issue_create`（立案闭环——issue_close 有销案无立案，备稿曾只能搁 D:\tmp 等网页）、`pr_close` / `issue_reopen`（两对状态机各补半边，错关不再只能网页救）、`pr_edit`（PR 勘误；**写覆盖首次入面：弹窗披露旧值→新值**——旧标题原文 + 旧正文规模/指纹/预览 + 新正文全文，指纹混入新旧两侧，重放通道"丢弃清单"同族纪律）、`pr_review`（§9 v2.0 承诺形状 `sluice review --event E` 落地，销漂移账；**commit_id 锚定"所见即所评"**——批准期解析 head sha 随评审提交，pr_merge head-sha 同族；**APPROVE 自批禁令双说破**——GitHub 服务端 422，agents 与 owner 同账户时 APPROVE 仅对他人 PR，弹窗与拒绝消息各一句）。全部克隆既有模板：v2.8 404 降级、预检 early-exit、`--body-file` 纪律、指纹锚定族直接引用，零新机制（共用段 preflight/预读/闸门/执行收敛为单源函数）。②**面一致锁入 selftest**：§9 op 列 ↔ CLI 写动词表（dispatch 由 `Program.WriteVerbs` 数据表驱动）↔ MCP Actions/AllowedFields 三方对账——"§9 承诺而实现没有"（pr_review 漂移靠人眼评审才抓到）从此 selftest 红脸；plan-lite.md 在运行目录向上 ≤4 级内按文档实文核对，发布件单飞退内置快照。**锁首跑即抓到第三处命名漂移**：§9 op 名 `pr_comment` 而 MCP action 叫 `comment`——§9.7"与 §9 写 op 一一对应"的字面承诺第一天就有例外，MCP action 更名 `pr_comment`（前门落地仅一天无存量依赖；CLI 动词仍为 `sluice comment`）。③**状态翻转 PATCH 族 5xx 归 21**：pr_reopen/issue_close 原归 10/rejected 会在服务端 5xx 时谎称"本次操作未执行"——与新四 op 及 comment/pr_create 的 create 族先例统一归 unknown（v2.7 批 workflow_dispatch/branch_delete 的 5xx 分类保留，留下批评审统一）。④api 行 graphql 措辞"未实现"→"不做"（POST graphql 是无类型万能口——一切 mutation 含破坏性操作都将绕过逐 op 闸门披露，等于把 §9.7 定论否决的 raw 透传请回后门；GET-only 是设计边界非欠账）；v2.10：全流程实战复盘处置批——①**push 重放前置缺口 ReplayUnresolved（§9.6 闸门⑤）**：worktree `git worktree add -b` 新建分支无本地 tracking、REST 首推成功又不回写 `origin/<B>` 引用 → rebase 后带 `--base` 重推时规划期 hasTracking=false 进不了重放 → 静默落 NonFF，开出"加 --base 重跑"这条**已满足的死指令**（2026-10-31 实战绕圈根源）。修法 = 执行期发现 `--base` 已声明 + 远端分支已存在 + 规划期无本地引用 → fail-closed 拒执行（exit 10，after_fix），点名 `git fetch origin <B>` 后原命令重跑。**本地引用是重放的硬前置**：丢弃清单 = `rev-list HEAD..origin/<B>` 需要本地对象，规划期 API 查远端头替代不了销毁侧披露——fetch 是闸门③的价格而非实现事故；push 成功消息必附"本地 origin/<B> 未更新"提示（PushDone 专属模板），掐掉确定性踩坑路径的出生点；`--base` 非祖先错误对 `origin/*` 形态点名 fetch+rebase 正解（实战两次同因：云端主干前进）。②**api 403 限流三分类**：匿名配额耗尽（"API rate limit exceeded for \<ip\>"）原归"资源需要认证（私有或受限）"= 诊断分类 bug（与仓库可见性无关）；带 token 的 secondary/abuse 与 primary 耗尽原归 credential_expired，把"等 N 分钟"开成"重新 setup"死方子——body 嗅探（ParseRateLimit）：匿名 primary→exit 10 after_fix"加 --auth 走独立配额"；secondary→**exit 22 新档 wait_until**（服务端分钟数透传；不开在 20 上——v2.9 exit→retry 函数性下 wait_until 不能与 retry_ok 同码）；首版不取 x-ratelimit-reset 响应头；v2.9：MCP 第三前门——`sluice mcp`（stdio JSON-RPC 常驻子命令）暴露**单一类型化工具 github_write**（action 值域与 §9 写 op 一一对应，一次调用恰一个写 op）；**spawn-child 拓扑**（server 每次 tools/call 以绝对路径 spawn 一次 CLI 子进程——Gate 的 Avalonia lifetime 是"窗口关即退出"的一次性设计不可常驻复用，server 进程由此**永不接触 vault**，MCP 层零行安全/UI 代码；body/inputs 经临时文件桥接 --body-file/--inputs-file 用后即删）；text=子进程 §8 消息逐字（单源），structuredContent={ok,code,retry,url?}+isError=!ok（**executed 砍掉**：exit 0 含 up_to_date/204 受理等非写入终态，不造第二套语义）；**exit→retry 映射函数性入 selftest 锁死**；audit 新增 **via 字段**（cli|mcp，env 注入纯标签）；host 取消**不杀子进程**（杀活窗违禁，审计是真相）；红线：elicitation 永不替代闸门 / CLI 永留 / 无通用透传 / api 读 v1 不进（留观红队）；v2.8：latest-4 评审处置批——①**匿名 404 不再断言"不存在"**（pr_reopen/issue_close 预检的私有库盲点：私有库对匿名 GET 同样 404，硬拒 = 私有库两 op 完全不可用 + "不存在"终态误导 agent 认定编号错而换路重试——降级为弹窗警示行 + Gate 警告，存在性由服务端执行时终判，与 pr_merge/branch_delete 预查纪律对齐；issue_close 另说破类型未验证——关到 PR 编号上就是关 PR）②**audit consent 形态保真**（SLCV2 下批准=桌面点击，consent 记 `click` 不再硬编码 `hello`——形态取**进程启动时 vault 快照**：运行中 V1→V2 迁移不回写，迁移当次批准仍是 Hello 照记 hello；§14 L1 让步的唯一事后区分维度"生物事件还是点击"从此可答）③**SaveDpapi 原子写**（临时文件→回读解密比对→替换，与迁移同纪律并收敛为 SLCV2 唯一写者——setup 覆盖旧 vault 不再写一半即损）④api 302 同域 api.github.com 保留 Authorization、跨域 CDN 仍剥（正确性兜底，现网无同域 302）⑤README PAT 矩阵 pr merge 行补 Contents:RW（merge 要向 base 写 commit，--delete-head 删 ref 同走 Contents）；v2.7：Vitro 复盘处置批——①`pr_reopen` / `issue_close` / `workflow_dispatch` 三 op 入列（**reopen 优先于 dispatch 作为 CI 补救**：reopened 是 pull_request 默认触发 type；dispatch 定性为运维便利而非"REST push 不触发 CI"的修复——该定论被 10-30 上午三次 synchronize 触发实证否定，当晚零 run 是 GitHub 平台故障窗，与同窗 4×blob-500 同源）②api 读通道 **3xx 受控跟随**（≤3 跳，仅 api.github.com / *.githubusercontent.com；DoH 直连同域扩展——logs 端点 302→CDN 签名 URL 从此可读；tarball 端点实测跟随后 200/9.7MB 落 stdout；域外重定向原样返回 + ApiRedirected 指引，不装开放代理）③**幂等对象创建 5xx 有界重试**（blob/tree/commit，2s/8s×3；批准覆盖一次执行，同对象重试不超范围；update-ref 永不自动重试）④**branch delete 弹窗披露连带关 PR**（静态警示行 + 尽力匿名预查 pulls?head= 命中列 PR 号；删 #389 head 时用户批前不知情）⑤**hello:false 正式采用**：SLCV2（DPAPI）静默 vault 不变量入册 §5/§14 L1——批准事件从生物特征降为桌面点击，同用户可静默解密=owner 显式选择的 L1 级让步；回滚须 hello:true+重跑 setup；⑥--version 戳对齐 v2.7（v2.5 滞后清账）；v2.6：push 重放式更新通道——force 不变量从"永禁"改写为"唯一显式通道"（非主干分支 + `--base` 声明新基点 + 弹窗明示旧头→新头与丢弃清单 + 执行前远端头复核 RemoteMoved + audit 记 old→new），PR 分支可原地 rebase 更新、污染归零；裸 `--force` 参数与主干推送仍永禁；v2.5：评审处置批——①submodule 判定看两侧 mode（删除行 `:160000 000000…D` 只看 newmode 会漏判，折回 oldmode 生成非法 tree 条目照样 422）②pr_merge 内容保真复核（head sha 入弹窗与批准指纹，执行前带 token 复核，变了即中止 after_fix——与 push 的 HeadMoved 同族）③--delete-head 删分支成败均落审计（审计链不再断环）④403 语义拆分（pr_merge/branch_delete 的 403=分支保护/无权限 → 专属消息 exit 10，不再误导重新 setup；401 仍 9）⑤ref 路径逐段 percent-encode（`#`/`?`/空格等合法分支名不再截断 URL）⑥构建戳 +dirty 标 ⑦tripwire 大小写不敏感（Main/MASTER 漏网）⑧pr_merge 404 专属消息（不再混入"不可合并"文案）⑨pr_merge 匿名元数据读入读审计（与 api 通道同纪律）⑩--version 打印版本号；v2.4：8-PR 实战修复批——构建戳自报家门、门控 UX 四项（滚动暂停计时/Hello 取消回窗/前台置顶/超时可配+召回）、push 非 ff 改档 after_fix 与 HasMerge 专属消息、api 白名单补 `+,;`、pr_merge 与 branch_delete 入列；v2.3：实现现状对齐——api 读通道消息入 §8.2 表与 exit 映射、api GET 超时归 20、pr 422 与 vault 损坏专用消息；v2.2：评审修订——vault UI 门控、seal 扩容、notice 通道、unknown 语义、人类工作流） |
| 日期 | 2026-11-05 |
| 状态 | 设计定稿；**V0–V1 已实现并实弹运行**（v0.7.6，audit 3100+ 行，CLI 与 MCP 前门并用）——余项：§12 红队演练/MCP A/B 探针、P5 可选探针、Linux 评估（V1.5），§14 留观项（L7 数据已可从 audit 挖掘，受控归因待 A/B） |
| 与 plan.md 关系 | **本文档取代 plan.md 的架构决策**；plan.md 保留作背景与实测依据（§4 实测、§17 验证思路仍有效），不再按其实施 |
| 目标平台 | Windows 10/11 x64（UI 与核心逻辑跨平台，Linux 为填空题，见 §1.5） |
| 语言与 UI | **C#（.NET 10 LTS）+ Avalonia 12**（12.1.4 实测；NativeAOT 定稿） |
| 代码位置 | 任意本地目录，下文以 `<SLUICE>` 代指项目根 |

---

## 1. 哲学与安全性质

原 plan.md 的隐含不变量是"能力壕沟"（token 永不进入 agent 可达形态），同用户条件下守不住，导致一路加码防御 High IL / 内存保护 / TPM。本文档换一条更诚实的不变量：

> **事件闸门**：任何 GitHub 写动作，必然伴随一个人类在场完成的批准事件（看内容 + 点击批准 + Windows Hello）。token 的保密性不是目标；批准事件与写动作的一一对应才是。

推论：

1. **侧门封堵是承重墙**。封堵后（环境里没有可静默取用的写凭据），闸门对"不主动解密 Sluice 本地凭据"的 agent 是绝对的——没东西可绕。对会主动去解密的那类（恶意文档驱动的 T2），防线是 **vault 本身需要 UI 才能解开**（§5：Hello 门控的解密，P7 探针定形式）——解密即批准事件，不存在静默路径。措辞修正（v2.2）：不是无条件的"绝对"，是"绝对 + 一道 UI 门"。
2. **N1 类攻击（同用户注入/读内存）明确不管**。这类攻击等价于"机器上有个黑客"，属重装系统问题，不做防线投入。让步的代价用**细粒度短效 PAT** 封顶：即使 token 被偷，爆炸半径 = 单仓库 × 单权限 × ≤90 天。
3. **agent 不是恶意方，是热心过头的自己人**。防线目标：它"顺手做完事"的手停在批准点上；它误解时，回复信息让它**无法多想**（§8 是本文档的核心章节）。
4. 剩余风险全是 UX 问题（弹窗疲劳、误批），用拒绝理由反馈与收敛去解，不用马奇诺防线去解。

---

## 1.5 语言与运行时（v2.1 变更）

原 plan.md 选 C++23 的理由链——`std::expected` 载体、`secret_buffer` 清零、RAII 纪律——是为"能力壕沟"服务的，事件闸门哲学下该理由链不再成立。语言的安全职责从"保护秘密"退到"保护逻辑正确"（守门工具最致命的 bug 是"该拒时放行"），这恰好是内存安全语言的强项。v2.1 改选：

| 项 | 决策 | 依据 |
|---|---|---|
| 语言 | **C# 13 / .NET 10 LTS** | 内存安全；可空引用 + 模式匹配 + record + switch 表达式让 op 分派与策略判断难写错 |
| UI | **Avalonia 12**（12.1.4 实测通过） | 单对话框开发体验接近 WPF；Linux（X11/Wayland）同一套 XAML；全项目唯一第三方依赖 |
| Hello | CsWinRT 投影 `UserConsentVerifier` | 比 cppwinrt 舒服一个量级；Windows 专属，藏进 consent 叶 |
| vault 保护 | **Hello 签名派生（P7/P7c 实测定案）**：master key = HKDF(HelloSign(挑战))，解锁即按指纹，静默解密路径实测不存在；DPAPI 弃用 |
| push 管道 | `AnonymousPipeServerStream(HandleInheritability.Inheritable)` | 标准库现成，token 不落命令行/磁盘/环境变量 |
| HTTP | `HttpClient` | **证书 issuer 校验必须做（P2 实锤）**：本机 hosts 将 api.github.com 指到 127.0.0.1，TLS 由 Watt 本地代理终结，issuer = `CN=SteamTools Certificate, O=BeyondDimension` 且已受本机信任（响应头 `Server: WattToolkit`）——一切 GitHub HTTPS 流量过本地 MITM，遇非 DigiCert 链拒发 token 并告警 |
| JSON | `System.Text.Json` | BCL 内置 |
| 发布 | **NativeAOT**（探针通过，定稿） | 启动 ~100ms/次；exe 19MB + 原生渲染依赖 ~19MB；R2R 单文件 214MB，弃用 |

**换入**：内存安全；开发速度；C++ 方案里最疼的三块（WinRT / DPAPI / 匿名管道句柄继承）全部降为标准库；Linux 路径——UI + 核心逻辑约 2/3 代码可移植。

**换出（诚实记账）**：(a) token 在托管堆存在多副本且**永不可清零**——N1 已让步，无增量风险，如实记为缺口 L8；(b) "零第三方"降级为"BCL + Avalonia"；(c) 新增运行时探针（§12 V0）。

**平台叶接口**（现在立起来，Linux 移植变填空题而非重构）：`IConsent`（Windows=Hello；Linux 预留 polkit 风格口令/指纹）、`ISeal`（per-OS 侧门清单）、`ITokenStore`（Windows=DPAPI；Linux=0600 文件）。跨平台的是 UI（Avalonia）与核心逻辑（op 分派/policy/消息/审计）——**§8 消息规范一字不改**，语言无关。这也说明本次是"叶决策"而非"架构决策"。

---

## 2. 与 plan.md 的对照

| 原 plan.md 项 | 处置 | 理由 |
|---|---|---|
| sluiced.exe 常驻守护进程 | **砍** | 事件闸门下持守不是目标；CLI 每次调用自足 |
| 命名管道 + ACL + 帧协议 | **砍** | 无 daemon 则无 IPC |
| ImGui + DX11 + 托盘 | **砍** | 批准对话框用原生 Win32；无需 60fps 渲染循环、GPU 常驻、1.4MB 依赖 |
| shim（gh/git/credential helper 伪装） | **砍** | agent 直接推会失败（exit 128）→ 人来处理或走 `sluice push`；lull/PATH 抢占一类问题随之消失 |
| PBKDF2 + AES-GCM vault | **砍**，v2.2 换 Hello 门控的 NCrypt 包封 | 同用户本来防不住（plan §4 自己实测的），假装防得住反而引入键盘钩子自伤洞；评审指出 DPAPI 也是静默可解（同用户普通 API 调用），故 vault 解锁必须过 UI |
| canonicalize + 指纹重算仪式 | 简化 | 批准与执行同进程同瞬间发生，T5 几乎不存在；保留"给用户看的内容短哈希"作 UI 完整性提示 |
| Windows Hello | **保留** | 便宜（已实测可用），且是 T1.5（agent 自动点批准）的解 |
| fine-grained PAT | **保留并强化为硬性要求** | 爆炸半径封顶的唯一手段 |
| 侧门封堵 | **从缺失变为 V0 核心** | 原方案最大流程缺口 |
| audit.jsonl | 保留（简化字段） | 成本 50 行，价值高 |
| policy 硬拒（main/force） | 保留，硬编码（**v2.6 修订**：force 从"永禁"改为"唯一显式重放通道"，见 §9.6——裸 `--force` 参数仍永禁） | 疲劳误批的唯一闸；通道内化到弹窗明示与执行前复核，闸没拆 |
| 拒绝理由反馈 | **新增（用户要求）** | 把"墙"变"协商界面"，批准请求收敛，弹窗越用越少 |
| 递增延迟冷却 | 保留（简化为全局冷却） | 只能延长，做不到真阻止——对，但在封堵前提下"延长"已够 |
| G8（Watt MITM 证书校验） | 保留为 P2 门控加固项 | 15 行，token 不发给第三方代理 |
| C++23 + clang | **v2.1 换为 C#（.NET 10）+ Avalonia** | 见 §1.5：C++ 纪律为"能力壕沟"服务，前提已撤；内存安全语言接手"逻辑正确"职责 |
| nlohmann/json | **砍** | `System.Text.Json`（BCL 内置）；第三方仅 Avalonia（§1.5） |
| High IL / S5 | **砍（无限期推迟）** | 升级路径改为"agent 独立 OS 账户"，那才是真隔离 |

---

## 3. 威胁模型

### 防的

- **T1** 热心过头的 agent 顺手执行写操作（原始事故即此类）。
- **T1.5** agent "自我批准"：理解了"需要用户批准"后，用 SendInput 帮你点掉弹窗。防法：批准必须过 Hello（点得动按钮按不了指纹）+ 对话框丢弃 injected 输入。
- **T2** 内容注入使 agent 临时变恶（恶意 README/issue 诱导）。防法：能力缺席（无凭据）+ 弹窗内容人审 + tripwire 硬拒不可逆操作 + 消息文案点名禁止 device flow。
- **T3** 重试风暴：被拒后换方法再试（push 被拒 → gh api → 找凭据 → device flow）。防法：§8 消息规范（显式 retry 语义 + 禁止清单逐项点名）+ 冷却。

### 明确不管（与 plan.md 一致，但态度更彻底）

- **N1** 同用户注入/读内存：让步。爆炸半径由 PAT 封顶。
- **N2** 浏览器 cookie、**N4** device flow 诱骗：消息文案警告是全部投入。
- **N3** 管理员级攻击：不管。

### 爆炸半径封顶（硬性要求）

vault（token.bin）只接受 **fine-grained PAT**：指定仓库白名单 + `Contents: RW` + `Pull requests: RW` + 最短实用有效期（建议 90 天）。setup 时校验并在 UI 上显示权限要求，用户粘贴不符合要求的 token 时警告但允许（校验只能做"可访问目标仓库"这一层）。

---

## 4. 架构

**单进程、单 exe、用完即退。无 daemon、无 IPC、无服务。**

```
agent / 用户
   │  sluice comment --repo R --number 5 --body-file x.md
   ▼
sluice.exe（本次进程）
   │ ① 解析参数 → ② policy 硬判（main/裸 force → 直接 policy_reject，不弹窗）
   │ ③ 侧门自查（sealcheck，~50ms；违规 → seal_violation）
   │ ④ 冷却检查（state.json；冷却中 → cooldown）
   │ ⑤ 批准对话框（Avalonia）：操作摘要 + [批准] [拒绝] + 拒绝理由区（默认焦点拒绝，60s 倒计时）
   │      ├─ 拒绝/超时/关窗 → 终态消息 → exit
   │      └─ 批准 → ⑥ Windows Hello（生物识别优先）
   │              └─ 通过 → ⑦ DPAPI 解出 token（仅本进程内存，秒级）
   │                        → ⑧ WinHTTP 执行 / push bridge 执行
   │                        → ⑨ 审计一行 JSONL → 结果消息 → exit
   ▼
agent 收到 stdout/stderr + exit code
```

明文 token 存在窗口 = CLI 进程生命期（秒级）。**系统任何时刻都不存在常驻的明文凭据持有者。**
（v2.27 基线重校：v2.24 TryReadMeta 静默回退起，hello:false（静默 vault）姿态在**批准窗之前**也可能解 vault——匿名预读撞 403/404 时以 token 复读一次（读无破坏力、不弹窗、读落审计 auth 标记；IsDpapiVault 闸——hello:true 无此路径）。精确窗口 = 首次解密（可能是批准前预读回退）→ 进程退出，仍 ⊆ CLI 进程生命期；对同用户攻击者无增量（vault 文件本可直解，L1 让步在册），但"明文只在批准后出现"的旧审计基线（review.md B 组）自此作废。）

### 线程与并发

单线程 + 一个 Hello 等待。多个 sluice 进程可并存（各弹各的框），可接受；不做串行锁。

---

## 5. 凭据生命周期

| 阶段 | 形态 | 位置 |
|---|---|---|
| `sluice setup` | 用户粘贴 PAT → 校验可访问白名单仓库 → 随机 master key 加密 PAT → **master key 由 NCrypt 密钥包封（形式由 P7 探针定，见下）** | `%LOCALAPPDATA%\Sluice\token.bin` |
| 每次写操作 | 批准点击 → 解密触发密钥的 UI 门控（**这一次 Hello 同时就是批准因子**：单次生物事件完成解锁与授权，无需独立 UserConsentVerifier 调用） | 进程内存，秒级 |
| push | 经继承的匿名管道交给 cred-print 子进程 → git | git 子进程内存（git 存活期） |
| 读路径（v2.2） | API 读走 `sluice api`（token 不出 sluice 进程）；git fetch/pull 用独立**只读** PAT（在 agent 侧，无写能力，见 §9.5） | — |
| 失效 | HTTP 401/403 → `credential_expired` 终态，提示用户重跑 setup | — |

**vault 保护形式（P7/P7c 已实测定案，2026-10-28）**：
- **形式**：master key = HKDF(HelloKey.RequestSignAsync(vault 挑战))；挑战含 per-vault salt 与用途域分隔，存于 token.bin。
- **证据链**：a) 另一同用户进程可静默打开 Passport KSP 键**句柄**（键名为 SID 限定长路径，须 NCryptEnumKeys 枚举获得，凭据名直接开失败 0x80090027）；b) 但真实私钥运算有 UI 门——`NCRYPT_SILENT_FLAG` 下签名返回 **NTE_SILENT_CONTEXT（0x80090022）**，静默签名不存在；c) RequestSignAsync 同挑战两次签名**逐字节一致**（RSA PKCS1v15 确定性，256B）→ 派生密钥每次解锁可复现。
- **结论**：解锁 = 按指纹；批准因子与解密因子是同一次生物事件；静默解密路径被实测消灭（评审问题 1 落地，§1 措辞的"UI 门"成立）。
- 脆弱性注记：确定性依赖 PKCS1v15；若未来 Windows 更换填充方案，旧 vault 无法解密 → 恢复 = 重跑 setup 重贴 PAT。可接受。
- P7b（NCRYPT_UI_POLICY 软件键）不再需要。
- **静默 vault（SLCV2，config.json `"hello": false`；v2.7 正式采用——2026-10-31 owner 拍板，"试用后再定"落定）**：
  owner 显式降级换免指纹体验：vault = DPAPI（crypt32 直调 `CryptProtectData/UnprotectData`，CurrentUser 作用域，零第三方依赖）包封，无 Hello 参与——**批准事件从生物特征降为桌面点击事件**；api `--auth` 认证读随之零交互。语义让步如实入册（§14 L1）：同用户进程可静默解密（回到 L1 级让步，owner 自选，2026-10-31 三枚指纹连弹后拍板）。InputFilter 注入拦截 / 倒计时 / seal / 审计全保留；旧 V1 vault 在下次批准或认证读时自动迁移（= 最后一次指纹，stderr 有提示，迁移失败不阻塞本次已批准的写）。**回滚 = config 改回 true 且重跑 setup**——SLCV2 下仅改回配置不恢复指纹（LoadApproved 先查 vault 形态再查配置）。**v2.7 迁移实弹修复与加固（2026-10-31 首个迁移实弹暴露 92e902e 两处缺陷）**：①`PeekChallenge` 在 SLCV2 下返回空数组——挑战是 V1/Hello 专属概念，原实现无条件走 V1 布局校验，迁移后一切写动词在 `Gate.Request` 构造期即崩（弹窗都不出现，vault 本身无损——`api --auth` 静默读实测可解）；②迁移改「临时文件写入 → 回读解密比对 → 原子替换」，校验不过保留 V1——覆盖唯一凭据文件的动作必须先验证再替换。

明确不做：内存加密、VirtualLock（可选 10 行加固，做不做不改变性质）。

---

## 6. 侧门封堵与自查（承重墙）

`sluice seal`（人运行，`--fix` 自动处理）+ 每次写操作前 50ms 自查：

| 检查点 | 内容 |
|---|---|
| Windows 凭据管理器 | `git:` / `gh:` 前缀条目 |
| `%APPDATA%\GitHub CLI\hosts.yml` | gh 的明文/记锁 token |
| `~/.git-credentials` | 明文凭据文件 |
| WSL（若安装） | `~/.config/gh/hosts.yml`、`~/.git-credentials` |
| VS Code | globalStorage 中 GitHub session 存在性（只检测报警，清理需在 VS Code 内操作） |
| **SSH（最易旁路，v2.2 补）** | `~/.ssh/` 私钥存在性、`ssh-agent` 服务与已加载密钥、Pageant 进程。`git push git@github.com` 完全绕开 PAT。**不可删除**（SSH key 是多用途凭据）——`seal --fix` 向 `~/.ssh/config` 写入 `Host github.com` 屏蔽块（`PubkeyAuthentication no`，带 marker 注释），外科手术式只断 GitHub-over-SSH |
| **remote URL 内嵌 token（v2.2 补）** | 深扫档遍历常见仓库位置的 `.git/config` 与全局 gitconfig：`ghp_/gho_/github_pat_` 模式、`url.<base>.insteadOf` 重写规则；发现即报警，人工处置 |
| **环境变量（v2.2 补）** | `GH_TOKEN` / `GITHUB_TOKEN`——预检直接检查自身进程环境（agent 调 sluice 时必然继承 agent 的环境，一查一个准）；`seal --fix` 提示从 shell profile / IDE 配置移除 |

本机实测清点（2026-10-28 探针，存在性检查、未读取内容）：凭据管理器 **4 条 GitHub 凭据**——`LegacyGeneric:target=GitHub for Visual Studio - https://jingwei108@github.com/`（VS 集成）、`git:https://github.com`、`git:https://rustin-beep@github.com`（GCM）、`gh:github.com:`（gh keyring）；`%APPDATA%\GitHub CLI\hosts.yml` 存在；`~/.git-credentials` 无；WSL 内两处均无；VS Code `state.vscdb` 存在待查。

- **预检**（每次写调用，≤50ms）：凭据管理器 github 条目计数、gh hosts.yml 存在、`GH_TOKEN`/`GITHUB_TOKEN` 环境变量、`~/.ssh/config` 屏蔽块在位、全局 gitconfig 快查。
- **深扫**（人工 `sluice seal`，可 `--fix`）：仓库 `.git/config` 遍历、WSL、VS Code、`ssh-add -l`、凭据管理器逐条。
- 自查发现残留 → **不弹批准框，直接 `seal_violation` 终态**（消息见 §8），Sluice 拒绝服务直至用户处理。
- **哨兵（v2.2 设计，v2.20 实现落地）**：`sluice sentinel-install` 建 5 分钟间隔计划任务跑预检，违规弹 Windows 通知。作用：封 device flow 的"**当场成功**"时间窗——agent 走 `gh auth login` 拿到新 token 后用 gh 直连，全程不经过 sluice，最迟 5 分钟被看见。是检测不是阻止（N4 本地不可阻止），但把"下次 sluice 调用才发现"缩到"五分钟内"。**v2.20 实现现状（2026-11-03 实弹）**：C# 侧零检测代码——安装器只写 `%LOCALAPPDATA%\Sluice\sentinel.ps1`（烙印安装期 exe 绝对路径）+ schtasks `/SC MINUTE /MO 5`；检测单源 = `sluice seal`（与写路径 50ms 自查同一套预检，非深扫），exit 7 → toast + sentinel.log。toast 双路：BurntToast 模块（装了就用）→ WinRT 原生兜底（PowerShell AUMID，零依赖）。实弹三个编码坑修在位：ps1 必须带 UTF-8 BOM（PS 5.1 对无 BOM 按 ANSI 解码，中文与 ✗ 字面量全乱）；`[Console]::OutputEncoding=UTF8` 先设（子进程输出捕获解码）；Add-Content 显式 UTF8。移除：`schtasks /Delete /TN "Sluice Sentinel" /F`。**v2.23 修复与加固**：/TR 内层引号转义（原 `-File {ps1}` 裸拼——LOCALAPPDATA 含空格时任务每 5 分钟静默失败且无日志，原注释声称的路径校验并不存在；含空格路径实弹验证通过，SentinelTr/SchtasksCreateArgs 入 selftest 锁）；sentinel.log 超 1MB 滚动 .1 单代；**暂停态伪造核对**（§14 L11）——state.json 的 pause_until 无逐字对应的 seal_pause 审计记录 → toast「疑似伪造」+ 日志（检测层看住检测层）。
- **`seal --pause <分钟>`（v2.2 设计，v2.22 实现落地）**：人的显式暂停窗口——过批准窗（agent 也能发起调用，但同样要过闸门，与一切写动作同待遇；V1 vault=Hello、SLCV2=点击），期内恢复直推能力，到期自动恢复封堵。服务于 §9.5 人类工作流。**v2.22 实现现状（2026-11-03 实弹）**：批准后摘 notice helper + 摘 SSH 屏蔽块（只删标记块内行，用户配置不动），state.json 记 pause_until；**暂停短路点在 `SealCheck.Run()` 单点**——九处写动词门、seal、哨兵全部自然继承，零处散改。到期自愈双路：seal no-args 检测过期先跑 resume（人跑 seal 即触发）；哨兵 ps1 检测过期跑 `seal --resume` + toast「暂停窗口结束」（最迟 5 分钟，不靠人记得）。resume 不过闸门（恢复防护是安全侧动作——开门要批准，关门不用）：清 state → 重装 notice → seal --fix 清理期内新登录凭据。暂停期内哨兵静默（不为用户主动引入的凭据报警）；分钟数夹 [1,480]。**v2.23**：暂停截止的 ✓ 输出/审计/state 一律以批准后实际时刻为准（Begin 返实际值——原窗口预估时刻会早于 state.json 实际值）；显示跨午夜带日期（UntilLabel：同日 HH:mm / 跨日 MM-dd HH:mm）；doctor 补暂停感知（v2.22"不做"决定推翻——原 notice 重装建议与暂停语义直接冲突，暂停中/过期未愈/在位冲突三态各有其辞）。
- 卸载路径（v2.22 实现落地）：`sluice unseal`——过批准窗（摘防护是降级动作，与 pause 同待遇），摘 notice helper + 删哨兵任务 + 摘 SSH 屏蔽块 + 清暂停状态；vault/config **不自动删**（含加密 token，删除由人决定），末尾打印善后清单（AGENTS.md 片段手动移除、重引凭据自觉最小化）。红线不变：卸载后环境**不得**静默回到"全权 token 躺在 keyring"状态——清单逐项说破。

---

## 7. 批准交互

Avalonia 窗口（单对话框；Windows 端以 Win32 LL 钩子做 injected 输入过滤）：

```
┌─────────────────────────────────────────────┐
│ Agent 请求执行对外动作                  58s │
│─────────────────────────────────────────────│
│ 操作   pr_comment                           │
│ 仓库   rustin-beep/Vitro   #5               │
│ 正文   LGTM，两处命名建议……（可滚动，全文）│
│ 哈希   9f3c2a1b                             │
│─────────────────────────────────────────────│
│ 拒绝理由：[预设▾]  [自由文本______]          │
│                                             │
│   [ 拒 绝 ]        [ 批准→Hello ]           │
└─────────────────────────────────────────────┘
```

| 规则 | 理由 |
|---|---|
| 默认焦点"拒绝"，Enter/Esc = 拒绝 | 误触安全侧 |
| **正文全文可滚动，长文强制滚到底才亮"批准"** | 原 plan"3 行折叠"破坏"批准你所见"，此处修正 |
| 批准 → Windows Hello | T1.5 防线；用户成本 ≈ 1 秒 |
| 对话框内丢弃 injected 输入（`LLKHF_INJECTED`/`LLMHF_INJECTED`，进程内 LL 钩子过滤，~40 行） | T1.5 第二道；门槛性质，够用 |
| 拒绝理由：预设 + 自由文本 | 预设决定消息的 retry 语义（见 §8），自由文本原样附言 |
| 预设：不批准 / 目标不对 / 内容需修改 / 我自己处理 / 任务重新讨论 | 每档绑定明确的"下一步"文案，防 agent 猜 |
| 窗口被关 = 拒绝（timeout 档） | 无"关闭即同意" |
| **滚动正文 10s 内暂停倒计时**（v2.4，与"输入理由暂停"同构） | 滚到底是批准前置，惩罚阅读时间自相矛盾；LL 钩子挡 injected 鼠标，暂停不可被 agent 利用；10s 上限防误触滚轮无限阻塞 |
| **批准 → Hello 前隐藏批准窗 + Activate**（v2.4，实测修订） | Hello 系统凭据 UI **非 TOPMOST 且 Z 序在调用者窗口之下**——批准窗在前必遮 Hello（置顶版实测：Hello 恰在批准窗下面）；隐藏批准窗让 Hello 无遮挡，Activate 先行保证 sluice 前台（credential UI 定位跟随前台，防左上角/沉底）；取消后窗口带状态行恢复 |
| **Hello 取消/未通过 → 回到未决态**（v2.4，非终态） | 取消 ≠ 拒绝请求：回窗看清内容再决定；安全无损（取消无写动作，再批准仍必须过 Hello）。vault 密钥缺失仍终态（重试无用） |
| **超时秒数可配**：config.json `gate_timeout_sec`（默认 60，夹 [30,600]）；剩 15s 提示音 + 闪任务栏（v2.4） | agent 同用户可改 config——改短只自伤（自动拒是安全侧）、改长不产生写动作，纯 UX 旋钮；召回给暂离用户一次回来机会 |
| **pr_merge 弹窗带 head sha8，执行前复核**（v2.5） | 元数据是 fetch 时刻快照，批准→执行间 head 被推进则合并内容超出用户所见——与 push 的 HeadMoved 同族：sha 变了即中止（after_fix），重跑重新弹窗；批准指纹混入 head sha，审计行锚定"所见的是哪一版"；快照缺失（私有库匿名读失败）不阻断，弹窗已声明"请自行核对" |
| **重放式更新弹窗明示替换内容**（v2.6，§9.6） | 「批准你所见」扩展到销毁侧：窗口顶部明示旧头→新头 + 丢弃清单（rev-list HEAD..origin/<B>，前 3 标题+总数）——feat 分支通常无服务端保护，本地闸门是唯一防线；执行前复核远端头（RemoteMoved） |
| **branch delete 弹窗披露连带关 PR**（v2.7） | 删 PR head 分支 = 连带关闭该 PR，且重建同名分支**不会**恢复已关 PR——2026-10-30 Vitro 实战：删 #389 head 时用户批前不知情。静态警示行常驻弹窗；尽力匿名预查（`pulls?head=O:B&state=open`，读落审计）命中时列 PR 号与标题并红字提示（Warning 位）；预查失败（私有库/网络）不阻塞——静态行仍披露规则 |
| 冷却：10 分钟内 3 次拒绝 → 全局冷却 10 分钟；冷却期内再试不弹窗、每次顺延 +5 分钟（上限 60） | 只能延长，做不到真阻止——封堵前提下"延长"已够；消息带绝对结束时间 |

不承诺：窗口显示完整性（同用户可移动/遮挡，L3）。信任在 Hello，不在画面。

---

## 8. 默认回复信息规范（本文档核心）

### 8.1 设计原则——每条消息要做到"单读终判"

1. **单读闭环**：发生了什么 / 为什么 / 下一步做什么，一条消息内说完。agent 不需要追问、不需要推测。
2. **retry 是字段不是语气**。每条消息携带机器可判的 `retry` 语义，禁止留白让 agent 猜：
   `retry=never`（禁止重试）/ `retry=after_fix`（修正后可重试一次）/ `retry=retry_ok`（可重试同一命令）/ `retry=wait_user`（等用户指示）/ `retry=wait_until`（等绝对时间点）/ `retry=change_approach`（换允许的方式）。
3. **事实前置**：每条终态消息必含"本次操作未执行，GitHub 上没有产生任何变更"——防 agent 假定部分成功（"多想"的最常见形态）。
4. **禁止清单逐项点名**：换命令、换 API、找凭据、发起授权，四类绕过路径一条不落全部写明"不要"。
5. **成功也交代**：给出结果 URL + "操作已生效，无需再用其他命令验证"——省掉 agent 事后复核的一轮工具调用。
6. **前缀二分**：`[Sluice]` = 过程/技术消息；`[Sluice·终态]` = 最终结果。AGENTS.md 按前缀立规（§8.4）。
7. 默认中文；错误码等技术细节只出现在技术类消息中，拒绝类消息零噪声。

### 8.2 全部默认消息（全文，实施时逐字落进 `msg.cpp`）

**成功**（stdout，exit 0）

```
[Sluice] 已完成：pr_comment → rustin-beep/Vitro#5（HTTP 201）
结果：https://github.com/rustin-beep/Vitro/issues/5#issuecomment-123
操作已生效，无需再用其他命令验证。
```

**denied**（stderr，exit 2，retry=never）

```
[Sluice·终态] 用户已明确拒绝：pr_comment → rustin-beep/Vitro#5。
本次操作未执行，GitHub 上没有产生任何变更。
必须遵守：
1. 不要重试本操作，也不要用其他命令、API、工具或凭据路径达到相同效果；
2. 不要尝试获取、刷新或申请任何 GitHub 凭据或授权（包括 gh auth login、device flow、打开浏览器授权页）；
3. 如认为该操作仍有必要：用一句话向用户说明目的，等待用户明确指示，不要自行重试；
4. 若任务其余部分不依赖本操作，继续执行其余部分。
```

（附言行，仅当用户填写理由时追加：`用户拒绝理由："<原文>"——请按此理解；retry 语义以上方预设为准。`）

**timeout**（exit 3，retry=wait_user）

```
[Sluice·终态] 用户暂离，批准窗口 60 秒无响应，已自动拒绝。
本次操作未执行，GitHub 上没有产生任何变更。
1. 不要立即重试（重试只会再次弹窗打扰用户）；
2. 不要换其他方式完成同一操作；
3. 继续任务其余部分；收尾汇报时注明："有一项操作待用户批准未执行：<op → 目标>"。
```

**policy_reject**（exit 4，retry=change_approach；硬编码策略，弹窗都不出现）

```
[Sluice·终态] 该操作被策略永久禁止：<push → 受保护分支 main / --force 参数 / 修改 .github/workflows>。此类操作不存在人工批准途径。
本次操作未执行。
1. 任何形式的重试（含变体：--force、--no-verify、改 remote、直连 API）都会被直接拒绝且不弹窗；
2. 允许的替代：rebase 到新基点后加 --base origin/<新基点> 走重放式更新（弹窗明示替换内容，§9.6），或推送到新特性分支（如 feat/<简述>）并创建 PR；
3. 若确需例外，向用户说明，由用户本人修改策略。
```

**cooldown**（exit 5，retry=wait_until）

```
[Sluice·终态] 冷却期内：因短时间内多次被拒，Sluice 在 14:42 前自动拒绝一切写操作（不弹窗）。
本次操作未执行。
1. 14:42 前不要提交任何写操作；
2. 不要尝试任何绕过——环境内没有可用凭据，一切直连写法都会失败；
3. 可继续只读工作；到点后任务仍需要时，重新提交一次即可。
```

**consent_failed**（exit 6，retry=wait_user）

```
[Sluice·终态] 用户未通过 Windows Hello（取消或连续失败）。本次操作未执行，无任何变更。
不要重试；等用户口头让你再试时，重新运行同一命令。
```

**seal_violation**（exit 7，retry=wait_user）

```
[Sluice·终态] 环境自查失败：检测到未封堵的 GitHub 凭据（<检查点：明细>）。
为守住"写动作必有批准"的保证，Sluice 在用户处理前拒绝一切写操作。
本次操作未执行。
请把本消息原文转告用户，提醒其运行 sluice seal --fix；在此之前停止所有 git/gh 写操作。
```

**notice**（credential helper，stderr，exit 4，retry=change_approach——git 直连索凭据的那一刻；v2.19 落地，正文见 §9 实现现状）

```
[Sluice·终态] git 向 github.com 索要凭据被拒：本环境的 GitHub 凭据只存在 Sluice 闸门内，git 直连拿不到任何凭据。
1. push 场景：改用 sluice push --remote origin --refspec <分支>——将弹出批准窗口由用户决定（不要加 --force）；
2. 不要尝试 gh auth login、device flow、浏览器授权或任何获取/写入凭据的方式；
3. fetch/pull 公开库无需凭据不受影响；私有库只读请让用户处理（sluice api 可查，读通道无需批准）。
```

**not_initialized**（exit 8，retry=wait_user）

```
[Sluice] 尚未初始化：找不到 Sluice 凭据文件。
请让用户运行 sluice setup 完成配置；在完成前不要以其他方式执行写操作。
```

**credential_expired**（exit 9，retry=wait_user）

```
[Sluice·终态] 凭据失效或权限不足（HTTP 401/403：fine-grained PAT 过期，或目标仓库不在授权列表）。
请转告用户运行 sluice setup 更新凭据；不要自行处理。
```

**bad_request**（stderr，exit 10，retry=after_fix）

```
[Sluice] 用法错误：pr_comment 缺少 --number。
修正参数后重新运行同一命令即可；本次未产生任何副作用。
```

**http_failed**（exit 20，retry=retry_ok；**仅限请求未发出**：TCP/TLS 连接失败、DNS 解析失败。api 读通道的 GET 超时同码：请求可能已送达，但 GET 幂等无副作用、重试安全——exit 21 是非幂等 POST 专属档，读通道不得借用）

```
[Sluice] GitHub 连接失败（请求未发出，无任何副作用）：could not resolve host / TLS 握手失败。
可间隔 30 秒后重试同一命令；连续 3 次失败则报告用户。
```

**unknown**（exit 21，retry=wait_user；**请求已发出但结果不明**：超时、连接中断、HTTP 5xx——非幂等 POST 的专属档）

```
[Sluice·终态] GitHub 已收到请求但结果未知（<超时/连接中断/HTTP 5xx>）。此操作可能已经生效（例如评论可能已发出）。
1. 不要直接重发——如果已生效，重发就是重复操作；
2. 先查证：运行 sluice api 查看目标下是否已有此内容，或请用户打开对应页面确认；
3. 查证后：未生效 → 可重发一次；已生效 → 不要再发。
```

**internal**（exit 1，retry=wait_user；按错误发生阶段分两型）

```
[Sluice·终态] Sluice 内部错误（执行开始前），本次操作未执行，无任何变更。
请把上一条命令原文转告用户。
```
```
[Sluice·终态] Sluice 内部错误（执行开始后）：操作可能已完成，但收尾失败（审计可能缺行）。
请用户在 GitHub 上核对是否已生效；把上一条命令原文转告用户。
```
进程直接崩溃（连消息都没来得及打）：agent 只会看到非零退出且无 `[Sluice]` 输出——此情形写入 AGENTS.md 规则 5：视为结果未知，先查证。

**V1 增补消息（2026-10-30 四提交评审后入表；selftest 同步穷举对齐）**

```
[Sluice] api GET <path> → 请求超时（可能已送达；GET 无副作用，重试不会造成重复效果）。
本次未产生任何副作用；可间隔 30 秒后重试同一命令，连续 3 次失败则报告用户。
```
（exit 20，retry=retry_ok——api GET 超时专用）

```
[Sluice] api GET <path> → HTTP 401/403：资源需要认证（私有或受限），匿名读不可用。
本次未产生任何副作用（响应体已输出到 stdout）。
修正：加 --auth 参数重新运行同一命令（将弹出 Windows Hello 验证身份后带 token 重读）。
```
（exit 10，retry=after_fix——匿名 401/403 专用；带 token 的 401/403 走通用 credential_expired）

```
[Sluice] api GET <path> → HTTP <st>（<n> B，响应体已输出到 stdout）。
本次未产生任何副作用。404 = 资源不存在或路径有误；429 = 触发限流，稍等再试；
其余请读响应体的 message 字段。修正路径或参数后重试。
```
（exit 10，retry=after_fix——其余非 2xx 通用）

```
[Sluice·终态] vault 状态损坏：<token.bin 存在，但 Hello 密钥缺失/不可用>。
本次操作未执行，无任何变更；重试同一命令不会好转。
请让用户重新运行 sluice setup 重建 vault 与 Hello 密钥的配对；在此之前 sluice 的写操作不可用。
```
（exit 8，retry=wait_user——Hello 密钥缺失 ≠ 用户取消，不占 consent_failed 档）

```
[Sluice·终态] pr_create 被服务端拒绝（HTTP 422：<服务端 message>）。最常见原因：该 head→base 的 PR 已存在，或两分支间没有提交差异。
本次操作未执行，GitHub 上没有产生任何变更。
1. 不要原样重跑同一命令（同样的参数仍会被拒绝）；
2. 先查已有 PR：sluice api "repos/<owner/repo>/pulls?head=<owner>:<head>" --auth；
3. 已存在 → 使用已存在的那个 PR；确需新 PR → 更换 head 分支或先产生新提交后再提交。
```
（exit 10，retry=after_fix——PR 422 专用，替代旧"用法错误"模板）

**V1.4 增补消息（2026-10-30 8-PR 实战批；selftest 同步穷举对齐）**

```
[Sluice·终态] pr_merge 被服务端拒绝（HTTP 405：<服务端 message>）。常见原因：PR 不可合并（冲突未解 / required checks 未过 / 已被合并 / draft 状态）。
本次操作未执行，GitHub 上没有产生任何变更。
1. 先查可合并状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth，读 "mergeable" 与 "mergeable_state" 字段；
2. 冲突未解 → 解冲突推送后重试；checks 未过 → 等待或修复；已被合并 → 使用既成结果，不要再合并；
3. 修正后重跑同一命令。
```
（exit 10，retry=after_fix——pr_merge 405/409/422 服务端明确拒专用）

```
[Sluice] push 未执行：远端 <R>@<B>（头 <sha8>）含本地没有的提交，非 fast-forward。
本次未产生任何副作用（GitHub 上没有产生任何变更）。
修正：git fetch 后把本地分支 rebase 到远端头（git rebase origin/<B>；解冲突后 git rebase --continue），
再重跑同一命令——rebase 后的线性历史可正常推送。
若你已把分支 rebase 到新基点（如更新后的主干）、需原地更新同一 PR：加 --base origin/<新基点> 重跑同一命令，
走重放式更新通道——弹窗明示旧头→新头与丢弃的远端提交清单，批准后替换（§9.6）。
注意：--force 参数与任何未声明 --base 的覆盖推送仍被策略永禁。
```
（exit 10，retry=after_fix——**v2.4 档位修正**：非 ff 可由 agent 自行 rebase 修复后重推同一分支，属"修正后可重试"而非"策略永禁"；**v2.6**：末行"force push 被策略永久禁止"改写为指向重放通道——旧表述与新通道矛盾；裸 force（--force 参数、未声明 --base 的覆盖）仍永禁，不变量见 §9.6）

```
[Sluice] push 未执行：推送范围含 merge 提交（多父提交），REST push 只支持线性历史。
问题提交：<sha8 标题>（前 3 个，共 N 个）
本次未产生任何副作用（GitHub 上没有产生任何变更）。
修正：把分支 rebase 成线性历史（git rebase <远端基点>；解冲突后 git rebase --continue，
冲突解决会作为普通提交重放，无需保留 merge 提交），再重跑同一命令。
```
（exit 10，retry=after_fix——v2.4 专属消息：列出问题提交 + rebase 正解；旧文案"请用完整 git 推送"是死胡同指引——seal 封堵后环境无 git 推送凭据，已删除）

```
[Sluice] 附注：PR 已合并，但 head 分支删除失败（<原因>）。
请修正后用 sluice branch delete --repo <R> --branch <B> 单独删除。
```
（exit 0 附注，retry=after_fix——紧跟 Success 输出；主操作已生效不可回滚，删除失败如实报）

**v2.4 timeout 文案参数化**：`批准窗口 {n} 秒无响应`（n = config `gate_timeout_sec` 实际值）。

**V1.5 增补消息（2026-10-30 评审处置批；selftest 同步穷举对齐）**

```
[Sluice·终态] pr_merge 被服务端拒绝（HTTP 403：<服务端 message>）。常见原因：目标分支受保护（required checks 未过 / 禁用该合并方式）或 token 无合并权限。
本次操作未执行，GitHub 上没有产生任何变更。
1. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth，读 "mergeable_state" 字段；
2. 分支保护与权限属仓库策略，不要尝试绕过；确需例外请转告用户处理；
3. 若服务端 message 指向 token 权限（如 Resource not accessible），同样转告用户重新 setup——不要自行获取或更换凭据。
```
（exit 10，retry=after_fix——v2.5 语义拆分：合并 403 ≠ 凭据过期，原归 9 会把 agent 引向重新 setup 的死路）

```
[Sluice·终态] pr_merge 被服务端拒绝：<R>#<N> 不存在（HTTP 404）。编号有误、PR 已被删除，或已在别处合并后关闭。
本次操作未执行，GitHub 上没有产生任何变更。
修正：核对编号后重跑；列出当前 PR：sluice api "repos/<owner/repo>/pulls"；若 PR 已在别处合并则使用既成结果，不要再发起合并。
```
（exit 10，retry=after_fix——v2.5：404 混入"不可合并"文案误导，拆出）

```
[Sluice] pr_merge 未执行：批准期间 PR head 已变化（<旧 sha8> → <新 sha8>），弹窗展示的内容已过期。
本次未产生任何副作用（GitHub 上没有产生任何变更）。
修正：重新运行同一命令——将以当前内容重新弹窗征求意见。
```
（exit 10，retry=after_fix——v2.5 保真复核：批准→执行间 head 被推进即中止，与 push 的 HeadMoved 同族；利用需连过批准+Hello 两道门，属加固非堵漏）

```
[Sluice] pr_merge 未执行：合并前内容复核未完成（<原因>）。
本次未产生任何副作用（复核用 GET 无副作用，合并请求未发出）。
可间隔 30 秒后重试同一命令（将重新弹窗并重新复核）；连续 3 次失败则报告用户。
```
（exit 20，retry=retry_ok——复核 GET 失败/超时：merge 未发出，整条命令重跑安全）

```
[Sluice] push 未执行：<R>@<B> 远端头与规划期不同（<明细>），弹窗展示的替换对象已过期。
本次未产生任何副作用（GitHub 上没有产生任何变更）。
修正：git fetch（若分支已被删除加 --prune）后重跑同一命令——将以当前远端状态重新弹窗，重新展示替换对象与丢弃清单。
```
（exit 10，retry=after_fix——**v2.6 重放通道保真复核**：批准→执行间远端头被他人推进（或分支被删）即中止，静默 force 会孤儿化窗口外的新提交；与 push 的 HeadMoved / pr_merge 的 PrMergeChanged 同族）

```
[Sluice·终态] branch_delete 被服务端拒绝（HTTP 403：<服务端 message>）。常见原因：分支受保护或 token 无删除权限。
本次操作未执行，GitHub 上没有产生任何变更（ref 未被动过）。
受保护分支的删除属仓库策略，不要尝试绕过；确需删除请转告用户在仓库设置中处理。
```
（exit 10，retry=after_fix——v2.5 语义拆分：401 仍 9（凭据），403 归服务端策略拒）

**V1.6 增补（v2.7，Vitro 复盘处置批——新 op 三族 + api 重定向）**：

```
[Sluice·终态] pr_reopen 被服务端拒绝（<服务端 message>）。常见原因：PR 已是开放状态；head 分支已被删除；PR 已合并；token 无 Pull requests 写权限。
本次操作未执行，GitHub 上没有产生任何变更。
1. 已开放/已合并 → 使用既成状态，不要重跑本命令；
2. head 分支已删 → 先 push 重建同名分支（--base 给基点），再重跑本命令；
3. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth 读 state / head.ref / merged 字段。
```
（exit 10，retry=after_fix——2026-10-31 Vitro 复盘 #2：PR 误关后只能开新 PR、讨论上下文断链；reopen 是正解，也是 CI 卡死时比 dispatch 更常用的补救——reopened 是 pull_request 默认触发 type）

```
[Sluice·终态] issue_close 被服务端拒绝（<服务端 message>）。常见原因：编号是 PR 而非 issue；token 无 Issues 写权限；编号不存在。
本次操作未执行，GitHub 上没有产生任何变更。
修正：核对编号与类型后重跑；PR 的生命周期走 pr merge / pr reopen / branch delete，不走 issue close。
```
（exit 10，retry=after_fix——复盘 #3：销案闭环，评论留证之后能落锤）

```
[Sluice] 已完成：workflow_dispatch → <R> · <W> @ <ref>（HTTP 204）
触发事件已受理，无需重发；run 通常数秒内创建（https://github.com/<R>/actions）。
如需观察进度（非必须验证）：sluice api "repos/<R>/actions/runs?event=workflow_dispatch&per_page=1" --auth。
```
（exit 0，retry=———204 无响应体，受理即完成；run 创建是异步的，观察是可选项不是验证义务）

```
[Sluice·终态] workflow_dispatch 被服务端拒绝（<服务端 message>）。最常见原因：fine-grained PAT 未开 Actions: Read and write 权限。
本次操作未执行，GitHub 上没有产生任何变更。
请转告用户：GitHub → Settings → Developer settings → Fine-grained tokens → 该 token → Repository permissions →
Actions 设为 Read and write 并保存，然后重跑 sluice setup；不要自行获取或更换凭据。
```
（exit 10，retry=after_fix——403=权限矩阵问题，用户动作可修；勿归凭据过期(9)死路）

```
[Sluice·终态] workflow_dispatch 被服务端拒绝：<R> 的 workflow "<W>" 不存在，或其 on: 未声明 workflow_dispatch 触发器（HTTP 404）。
本次操作未执行，GitHub 上没有产生任何变更。
修正：核对 id/文件名：sluice api "repos/<R>/actions/workflows"；再读该 workflow 文件确认 on: 块含 workflow_dispatch。
```
（exit 10，retry=after_fix）

```
[Sluice·终态] workflow_dispatch 被服务端拒绝（<服务端 message>）。常见原因：--ref 指向的分支/标签不存在；inputs 与 workflow 声明不匹配。
本次操作未执行，GitHub 上没有产生任何变更。
修正：核对 ref 与 inputs 后重跑同一命令。
```
（exit 10，retry=after_fix）

```
[Sluice] api GET <path> → 重定向，且不可跟随（<去向与原因>）。
本次未产生任何副作用（响应体已输出到 stdout）。sluice 仅跟随 api.github.com / *.githubusercontent.com 域内的重定向（≤3 跳）。
修正：此类资源请经浏览器或按响应体/Location 提示处理；不要尝试把绝对 URL 传给 sluice api。
```
（exit 10，retry=after_fix——v2.7 受控跟随的边界侧：这不是开放代理，域外去向如实相告）

### 8.3 Exit code 表

| code | 含义 | retry |
|---|---|---|
| 0 | 成功 | — |
| 1 | 内部错误 | wait_user |
| 2 | 用户拒绝 | never（或预设指定） |
| 3 | 超时 | wait_user |
| 4 | 策略永久拒绝 | change_approach |
| 5 | 冷却中 | wait_until |
| 6 | Hello 未通过 | wait_user |
| 7 | 侧门违规 | wait_user |
| 8 | 未初始化 | wait_user |
| 9 | 凭据失效 | wait_user |
| 10 | 用法错误 | after_fix |
| 20 | 连接失败（请求未发出，无副作用）；api GET 超时同码（幂等，重试安全） | retry_ok |
| 21 | **结果未知**（请求已发出：超时/中断/5xx，勿盲重试） | wait_user |
| 22 | **外部限流等待**（v2.10：api 403 限流——secondary/abuse 拦截或带 token 配额耗尽；请求已应答、无副作用、勿重 setup；**v2.16 扩全通道**：一切写 op 的 403 secondary/429（WriteRateLimited"本次操作未执行"）与 push 序列（PushRateLimited——分支未动、已传 blob 内容寻址幂等，不谎称零副作用）；api 读通道 429 同批归档） | wait_until |
| 23 | **pending：批准窗未决（非终态）**（v2.25，MCP 前门专用，CLI 不产此码；`Gate.McpPendingSec` 默认 15s 仍未决即回）——附 receipt，`sluice_status` 工具 / `sluice status <receipt>` 收割终态；status 查询暂无终态行时同码 | wait_until |

v2.4 注：push 非 ff 从 exit 4（policy_reject）移入 exit 10（after_fix）——rebase 后可重推同一分支，非策略永禁；force 与受保护分支仍 exit 4，不变量不动。

v2.6 注：重放式更新通道的 RemoteMoved 同归 exit 10（after_fix）——fetch 后重跑即重新弹窗；force 不变量改写见 §9.6（裸 `--force` 参数与主干推送仍 exit 4）。

v2.7 注：新 op 的服务端拒绝（pr_reopen / issue_close / workflow_dispatch 的 403/404/422）与 api 域外重定向全部归 exit 10（after_fix——"修正后可重试"语义不变）；dispatch 受理归 0（204，受理即完成）。

v2.10 注：api 403 限流拆出等待档——匿名 primary 配额耗尽归 10（after_fix，加 --auth 走独立配额）；secondary/abuse 拦截与带 token 的配额耗尽归 **新档 22（wait_until**，服务端分钟数透传——请求已应答、无副作用，既非"请求未发出"也非凭据失效，勿重 setup。不开在 20 上：v2.9 exit→retry 函数性下 wait_until 与 retry_ok 不得同码，selftest 拦截了本批的首版方案）。同批：push 的 ReplayUnresolved（§9.6 闸门⑤）归 10（after_fix，fetch 后原命令重跑）。

v2.11 注：生命周期对称批五 op（issue_create / pr_close / issue_reopen / pr_edit / pr_review）的服务端拒绝（403/404/422）归 10（after_fix）；**5xx 归 21**——非幂等写下服务端 5xx 同样结果不明，原 v2.7 PATCH 族（pr_reopen/issue_close）归 10 会在 5xx 时谎称"本次操作未执行"，本批两 op 一并改齐（对齐 comment/pr_create 的 create 族先例）；workflow_dispatch/branch_delete 的 5xx 分类保留，留下批评审统一。

v2.12 注：push 序列新增分档——基点不在远端（BaseMissing）/超 200/2000 上限/序列步骤被拒（PushStepRejected，渲染服务端原话）→ 全归 10（after_fix）；原 Other→internal(1) 的映射整个废除——update-ref 未执行则分支必未动，"可能已完成"对该族是假话，且 Reason 原本只进 audit 不进消息（2026-10-31 实战：基点远端缺失的明确诊断写在代码里却永远到不了用户）；blob sha 不符 → 20（retry_ok——内容寻址对象同内容必同 sha，不符=平台异常窗，重传安全）；超限检查前置到规划期（不烧批准）。

v2.13 注：发版面批两 op（tag_create / release_create）的服务端拒绝（403/404/422）归 10（after_fix）；5xx/超时归 21（POST 非幂等——tag ref create-or-fail、release 有 already_exists 服务端护栏，重发前先查证）。

v2.27 注：收割语义统一——status 收割的 decision→exit 映射（StatusFamily）补 consent_failed→6 / rate_limited→22（两者都是批准后才落审计、恰落 pending 收割窗的 decision；原落 21"勿重发"与 live 的 wait_user/wait_until 指引相反——落哪边取决于人是否 15s 内点完，非确定性契约），up_to_date 单列 StatusUpToDate 事实句（原 StatusDone"已获批准并执行 × not_executed"自相矛盾）；收割↔live 同 decision 同 exit/retry 的反向锁入 selftest；rate_limited 审计 result 增存 RateLimitWhat 结论供收割复用。本批同时补齐 §8.3 自 v2.25 起缺失的 23 行。

### 8.4 AGENTS.md 片段（`sluice setup --install-snippet` 追加到工作区 AGENTS.md；v2.21 落地——标记块幂等，重跑=刷新，正文与 SetupCommand.Snippet 常量同源同步）

```markdown
## Sluice 规则（必须遵守）
1. 一切 GitHub 写操作（评论 / PR 创建·合并·重开·关闭·编辑·评审 / push / issue 创建·关闭·重开·标签 / tag / release / 分支删除 / workflow 手动触发）只能经 `sluice` CLI 或 `github_write` 工具（MCP）执行；直接 git push / gh / curl 写 GitHub 会因无凭据失败（git 直连会收到 [Sluice·终态] notice 指引）。
2. 输出中出现 `[Sluice·终态]`：这是最终结果。严格按消息内"要求"与 retry 语义执行；凡 retry=never / wait_user / wait_until / change_approach：禁止重试、禁止换方法、禁止尝试获取凭据或发起任何授权流程（含 gh auth login / device flow / 浏览器授权页）。
3. 仅 retry=after_fix / retry=retry_ok 允许按消息说明修正或重试同一命令。
4. 收到 seal_violation：把消息原文转告用户，暂停所有写操作。
5. 非零退出且输出中没有任何 `[Sluice]` 字样：视为结果未知（Sluice 可能崩溃于执行中途），按"先查证、勿重发"处理。
6. 一切 GitHub API 读操作用 `sluice api`（issue 清单/按标签或关键词过滤用 `sluice issue list`；读有日志、可核对，防"我读过"幻觉）；git fetch/pull 使用环境中的只读凭据。
7. `github_write` 返回 code=23（pending，附 receipt）：批准窗口已弹出、用户尚未决策，这不是失败。等待约 15 秒后用 `sluice_status` 工具（参数 receipt；无该工具时 CLI 等价 `sluice status <receipt>`）查终态（用户拒绝理由原文在此，必须遵守）；终态出现前禁止重发同一写操作——v2.31 起重发会被闸门去重拦截并并入原回执（不再另开新窗）。status 查询无副作用，可间隔重查。
```

---

## 9. op 白名单与执行

> **面一致锁（v2.11）**：本表 op 列 ↔ CLI 写动词表（`Program.WriteVerbs`，dispatch 由其驱动）↔ MCP Actions/AllowedFields 三方对账入 selftest——"表格承诺而实现没有"从此 FAIL（缘起：pr_review 自 v2.0 承诺、实现全无，漂移靠人眼评审才抓到）。op 与 CLI 动词不同名的历史映射仅 `pr_comment→comment`、`pr_review→review`。

| op | 命令 | 执行 | token 去向 |
|---|---|---|---|
| `pr_comment` | `sluice comment --repo R --number N --body-file F` | WinHTTP `POST /repos/{r}/issues/{n}/comments`。exit：201→0；401→9；**403→10（v2.16 自 GitHubApi 层 Unauthorized 拆出：WriteForbidden=权限/保护，原 credential_expired 死方子；限流→22）/ 404·422→10（CommentRejected——原经 InternalPost 谎记 internal）**；5xx/超时→21 | 仅 sluice 进程 |
| `pr_create` | `sluice pr create --repo R --base B --head H --title T --body-file F` | WinHTTP `POST /repos/{r}/pulls`。exit：201→0；401→9；**403→10（v2.16 同批拆出：WriteForbidden；限流→22）/ 404→10（仓库不存在或 head 未推远端）/ 422→10（PrRejected 查已有 PR）**；5xx/超时→21 | 仅 sluice 进程 |
| `pr_review` | `sluice review --repo R --number N --event E [--body-file F]` | WinHTTP `POST /repos/{r}/pulls/{n}/reviews` | 仅 sluice 进程 |
| `push` | `sluice push [--repo-path D] --remote R --refspec S` | 见下 push bridge | git 子进程内存（存活期） |
| `api`（读，v2.2；V1 实现现状；**v2.7 重定向跟随**；graphql **不做**——v2.11 定稿） | `sluice api <api-path> [--auth]`（GET-only；**graphql 不做**：POST graphql 是无类型万能口——一切 mutation（含破坏性操作）都会以一段不可读查询串绕过逐 op 闸门披露，等于把 §9.7 定论否决的 raw 透传请回后门；GET-only 是设计边界，非欠账；路径白名单允许 `?` `&` `:` `+` `,` `;` 查询字符——分页 cursor（base64 含 `+`）/过滤可用，拒 `..`/scheme/前导 `/`） | GET，**无需批准**；匿名读无需 Hello，`--auth` 私有读需 Hello（vault 解密的技术必然；SLCV2 静默 vault 下零交互，§5）。**v2.30：匿名回退**——匿名撞 401/403/404/429（配额耗尽/actions 端点对匿名收紧/私有库不可见/需认证）且 SLCV2 vault 时静默带凭据复读一次（同 `--auth` 零交互姿态），复读结果即最终结果（分类/审计同 `--auth`）；Hello vault 不复读、vault 异常退回匿名结果（TryReadMeta 同纪律）；匿名尝试照常落审计（回退时两行 anon+auth）；触发面 AnonReadShouldFallback 纯函数入锁；实测 PAT 对白名单外公开库全可读（Metadata 强制权限），公开库读几乎总能翻盘。**v2.7：3xx 受控跟随**——≤3 跳、仅 api.github.com / *.githubusercontent.com 域内（logs 等 actions 端点 302 到 CDN 签名 URL，不跟随则永远拿不到日志体——2026-10-30 实战 actions/jobs/{id}/logs http_failed ×8 空转的根治；tarball 端点实测跟随 200/9.7MB 落 stdout，logs 端点 2026-11-04 `--auth` 实测 200/85251B 落 stdout——302→CDN 跟随全族实弹完毕）；后续跳不带 Authorization（CDN 签名 URL 自带授权）；CDN 域同样走 DoH 直连 + issuer 校验（防线①随域扩展）；域外/超跳 → 原样返回 3xx + ApiRedirected（exit 10），**不装开放代理**。exit：2xx→0；超时→20（幂等，retry_ok）；匿名 401/403→10（加 --auth；v2.30 起 SLCV2 先静默复读，此档仅 Hello vault/无 vault 形态落地）；带 token 401/403→9；**403 限流除外（v2.10 body 嗅探）：匿名 primary→10（ApiRateLimitedAnon，加 --auth 走独立配额——非"仓库私有"）、secondary/abuse 与带 token 耗尽→22 wait_until（ApiRateLimitedWait，服务端分钟数透传——非凭据失效，勿重 setup）**；域外 3xx→10；其余非 2xx→10。响应体→stdout、消息→stderr。每次调用（路径+状态码+字节量）写 audit 读日志 | token 不出 sluice 进程 |
| `issue_list`（读，v2.29——读侧清单面。缘起：agent 实弹「issue 只能逐个看、不能按标签/关键词搜」——能力本在 api 通道，可用性不在：repos/issues 原生 JSON ≈4KB/条整页 >100KB 被截断、端点混 PR、搜索要 URL 编码知识） | `sluice issue list --repo R [--state open\|closed\|all] [--labels a,b] [--search 关键词] [--limit N] [--page N] [--with-prs] [--auth]` | 清单模式 GET `repos/{r}/issues`（state/labels/per_page/page/sort 全参显式）；`--search` 时 `search/issues?q=repo:{r}+type:issue[+state][+label:]…`（q 整体 percent-encode，标签含空格引号短语；不传 sort——best-match 即搜索本意）。**无需批准**（api 读通道同 posture：默认匿名零交互，`--auth` 过 Hello/静默 vault）；输出一行一条 `#N [state·state_reason][PR] 标题≤120 截断 {标签}`。**端点混 PR**：默认隐去但计数披露（隐去≠不存在），`--with-prs` 显示带标；`--search`×`--state all` 互斥 fail-closed（搜索端点 state 限定符只有 open/closed，静默缩窄即说破——v2.8 纪律）；清单端点无总数——满员提示翻页、不足即末页不猜；搜索 total_count 透传；凭据获取/状态分类/审计与 api 单源（TryAcquireReadToken/ClassifyApiReadResult v2.29 提出，审计 op 恒 api_read）；查询拼装/渲染纯函数入 selftest 锁。exit 族同 api 行（2xx→0；超时→20；匿名 401/403→10 加 --auth；限流→22；3xx 不跟随→10） | token 不出 sluice 进程（--auth 时） |
| `pr_merge`（v2.4，PR 工作流闭环最后一米；v2.5 评审加固） | `sluice pr merge --repo R --number N [--method merge\|squash\|rebase] [--delete-head]` | PUT `/repos/{r}/pulls/{n}/merge`——**服务端执行，天然尊重分支保护与 required checks**；弹窗尽力展示可读的 PR 元数据（title/head→base/变更统计 + head sha8；**v2.24**：预读走 TryReadMeta 单源——匿名 403/404（配额限流/私有库）且 SLCV2 vault 时静默带凭据复读，不再退化为"？ ← ？"或误报"私有库"；归因 MetaUnreadableWhy 单源；元数据终不可读时警示行披露弱锚定——指纹混 "?"、执行前 head 复核不可用）；**v2.5**：批准指纹混入 head sha、执行前带 token 复核 head sha，变了即中止（PrMergeChanged，与 push 的 HeadMoved 同族）；匿名元数据读入读审计（与 api 通道同纪律）；`--delete-head` 合并成功后删 head ref（**成败均落审计**；失败不回滚合并，附注指引 branch delete）。exit：200→0；401→9；403→10（PrMergeForbidden：保护/权限）；404→10（PrMergeNotFound）；405/409/422→10（PrMergeRejected，查 mergeable）；复核通道失败→20（retry_ok）；5xx/超时→21 | 仅 sluice 进程 |
| `branch_delete`（v2.4，PR 作废后清残留；v2.5 评审加固；**v2.7 连带披露**） | `sluice branch delete --repo R --branch B` | DELETE `/repos/{r}/git/refs/heads/{b}`——**v2.5：b 逐段 percent-encode**（`#`/`?`/空格等合法分支名不再截断 URL，push 的 ref 路径同款）——删 ref 非销毁历史（commits 留在服务端，GC 窗口内可重建）；`main`/`master` tripwire（**v2.5 大小写不敏感**）→ policy_reject（exit 4）；**v2.7：弹窗披露连带关 PR**（§7 规则行——静态警示行 + 尽力匿名预查命中列 PR 号）。exit：204→0；401→9；403→10（BranchDeleteForbidden：保护/权限，透传服务端 message）；403 限流/429→22（v2.16 嗅探前置）；404→10；**5xx→21（v2.16 清账：原落 10 谎称"未产生副作用"——可能已删，查证 refs）**；超时→21（幂等但已送达，查证 refs） | 仅 sluice 进程 |
| `pr_reopen`（v2.7，Vitro 复盘 #2：PR 断链的正解 + CI 首选补救） | `sluice pr reopen --repo R --number N` | PATCH `/repos/{r}/pulls/{n}` `{"state":"open"}`——**reopened 是 pull_request 默认触发 type，重开即再触发 CI**（优先于 workflow dispatch）。预检（尽力匿名读 pulls/{n}，落审计）：已开放 / 已合并各 early-exit 不弹窗；**v2.8：匿名 404 不再断言"不存在"**（私有库匿名读同样 404，两者不可区分——降级为弹窗警示行，存在性由服务端执行时终判，同 pr_merge 元数据读纪律）；弹窗带标题/方向/head sha8，批准指纹混入 head sha（锚定"所见版本"，同 pr_merge v2.5）。**有意不做执行前 TOCTOU 复核**：reopen 非破坏性、不发布内容，批准→执行间漂移由服务端 422 原话兜底（已开放/已合并/head 已删各有指引）。exit：200→0；401→9；403/404/422→10（PrReopenRejected 透传）；403 secondary/429→22（v2.16 嗅探前置）；**5xx/超时→21（v2.11 改齐：PATCH 族 5xx 同样结果不明，原归 10 谎称"未执行"）** | 仅 sluice 进程 |
| `issue_close`（v2.7，Vitro 复盘 #3：销案闭环） | `sluice issue close --repo R --number N` | PATCH `/repos/{r}/issues/{n}` `{"state":"closed"}`——评论留证之后能落锤，不再"关闭等手动"。预检（尽力匿名读 issues/{n}，落审计）：PR 编号（响应含 `pull_request` 字段即 PR）/ 已关闭各 early-exit；**v2.8：匿名 404 同 pr_reopen 降级**（降级后类型守卫失效，警示行说破"可能是 PR 编号，关到 PR 上就是关 PR"）；批准指纹混入标题（锚定所见，同 comment 的 body 指纹族）。exit：200→0；401→9；403/404/422→10（IssueCloseRejected）；403 secondary/429→22（v2.16 嗅探前置）；**5xx/超时→21（v2.11：PATCH 族 5xx 归 unknown——原归 10 会在服务端 5xx 时谎称"未执行"）** | 仅 sluice 进程 |
| `issue_create`（v2.11，生命周期对称批：立案闭环——issue_close 有销案无立案，备稿曾只能搁本地等网页建） | `sluice issue create --repo R --title T --body-file F [--labels a,b,c]` | POST `/repos/{r}/issues`——pr_create 同模板：title+body 入指纹（create 族单源公式）、正文全文入窗。POST 非幂等**不自动重试**（v2.7 不变量直接引用），重发前先 `sluice api` 查证是否已建（unknown 消息自带该指引）。**v2.17 `--labels` 可选**：单请求携带 labels 字段（**单请求原子**——消除"issue 建成而标签被拒"的半成品态）；未知名 fail-closed 预检（匿名 GET /labels 分页 ≤5×100 读全，未知名点名拒——GitHub 对未知名的处置未文档化且路径不一致：add-labels 据报 404、create 带标签据报静默自动建标签随机色＝窗外实体创建，都不放行）；预检不可用（私有库 404/超 500/网络）→ 弹窗警示行（v2.8 纪律）；labels 追加进指纹与弹窗（**无 --labels 时指纹与 v2.11 逐字节一致**，既有审计行仍可复现）；名字含逗号者本批不支持（LabelsParse 空段/重复拒，重复判定 OrdinalIgnoreCase——GitHub 标签唯一性大小写不敏感）。exit：201→0（html_url 提取）；401→9；403/404/422→10（IssueCreateRejected：403=Issues 写权限缺失→转告 setup / 404=核对仓库全名 / 422=校验）；5xx/超时→21 | 仅 sluice 进程 |
| `pr_close`（v2.11，pr_reopen 的镜像：状态机两半各缺半边，错关只能网页救） | `sluice pr close --repo R --number N` | PATCH `/repos/{r}/pulls/{n}` `{"state":"closed"}`——关闭非破坏（讨论保留、可随时 reopen）。预检（尽力匿名读 pulls/{n}，落审计）：已关闭（附 reopen 指引）/ 已合并（合并是终态）各 early-exit；指纹混入 head sha（镜像 pr_reopen，锚定"所见版本"）；v2.8 404 降级同款。exit：200→0；401→9；403/404/422→10（PrCloseRejected 透传）；5xx/超时→21 | 仅 sluice 进程 |
| `issue_reopen`（v2.11，issue_close 的镜像） | `sluice issue reopen --repo R --number N` | PATCH `/repos/{r}/issues/{n}` `{"state":"open"}`——PR 编号守卫同 issue_close（issues API 对 PR 编号同样应答；404 降级后类型未验证须说破："重开 PR 编号就是重开 PR"）；已开放 early-exit；指纹混入标题（镜像 issue_close）。exit：200→0；401→9；403/404/422→10（IssueReopenRejected：PR 误用 / 权限 / 锁定 422 原话）；5xx/超时→21 | 仅 sluice 进程 |
| `issue_label`（v2.17 标签面批：动词面第七缺口——源码零 label 能力，#409/#410 挂账无标签可挂） | `sluice issue label --repo R --number N --labels a,b,c` | PATCH `/repos/{r}/issues/{n}` `{"labels":[…]}` 整组替换（set 语义）——**摘除清单披露**（pr_edit 写覆盖披露同族：弹窗列 类型/当前标签→新标签→将摘除——set 语义的静默摘牌面必须被看见才执行）；**PR 编号放行+说破**（与 issue_close 拦截相反：挂标签无"错对象"可言，PR 与 issue 共用 issues 端点，拦了即堵死 PR 挂标签——弹窗列类型行）；未知名 fail-closed 预检 + 预检不可用降级同 issue_create v2.17；匿名 404/预读失败降级（当前标签不可读 → 摘除清单无法预生成，警示行说破"现有标签凡不在新清单者都将被摘除"）；指纹=编号+当前标签+新标签（锚定所见与所写，issue_close 标题指纹同族）；空清单（清空全部）本批不支持——至少设置一个；标签实体管理（建/删标签）不做，未知名正解是先建后挂。exit：200→0；401→9；403/404/422→10（IssueLabelRejected：未知名 404 / 编号不存在 / 权限——当前标签原样保留）；5xx/超时→21 | 仅 sluice 进程 |
| `pr_edit`（v2.11，PR 描述勘误——CHANGELOG 连坐场景曾只能网页） | `sluice pr edit --repo R --number N [--title T] [--body-file F]`（至少一项） | PATCH `/repos/{r}/pulls/{n}` `{title?,body?}`——**写覆盖首次入面：弹窗披露旧值→新值**（重放通道"丢弃清单"同族纪律）：旧标题原文、旧正文摘要（字符数+指纹+120 字预览）、新标题/新正文全文；**指纹混入新旧两侧**（锚定所见与所写）。exit：200→0；401→9；403/404/422→10（PrEditRejected：旧值未被动过）；5xx/超时→21 | 仅 sluice 进程 |
| `pr_review`（v2.11，§9 v2.0 承诺形状 `sluice review --event E` 落地——销漂移账；面一致锁的缘起 op） | `sluice review --repo R --number N --event COMMENT\|APPROVE\|REQUEST_CHANGES [--body-file F]` | POST `/repos/{r}/pulls/{n}/reviews`——**commit_id 锚定"所见即所评"**：批准期解析 head sha 随评审提交（pr_merge head-sha 同族；head 不可读则不锚定并在弹窗披露）；**APPROVE 自批禁令双说破**（弹窗一行 + 拒绝消息：GitHub 服务端 422，agents 与 owner 同账户时 APPROVE 仅对他人 PR 有效）；已关闭 PR 预检 early-exit（正解 comment / pr reopen）；COMMENT 必带 --body-file（服务端同样要求）。exit：200→0（html_url 提取）；401→9；403/404/422→10（PrReviewRejected 含自批指引）；5xx/超时→21（重复评审风险，查证后决定） | 仅 sluice 进程 |
| `tag_create`（v2.13，发版面批：tag 推远端进动词面——发版是周期性硬场景，曾只能网页） | `sluice tag create --repo R --tag V --at <分支\|完整sha>` | POST `/repos/{r}/git/refs` `{"ref":"refs/tags/{v}","sha":…}`——lightweight tag（直接指 ref，不建 annotated 对象，后续按需）；**create-or-fail 天然防覆盖**（v2.7「update-ref 永不自动重试」不变量覆盖此路径；sluice 不提供 tag 移动/覆盖）。预检（匿名尽力，落审计）：tag 已存在 → 确定性拒绝（附既有指向；v2.8 404 降级同款警示行）；--at 为分支名时匿名解析，失败（私有库 404 不可辨）→ 点名给完整 40 位 sha（本地 git 可得）后重跑；指纹=repo+tag+目标 sha。exit：201→0；401→9；403/404/422→10（TagRejected：Reference already exists=用既有 tag 勿重试 / 权限 / 名非法）；5xx/超时→21 | 仅 sluice 进程 |
| `release_create`（v2.13，发版面批） | `sluice release create --repo R --tag V --body-file F [--target <分支\|sha>] [--name N] [--prerelease]` | POST `/repos/{r}/releases`——**target_commitish 锚定**：tag 不存在时以批准期解析的 sha 显式传入（--target 缺省解析默认分支头；隐式建 tag 的 TOCTOU 处方，pr_merge head-sha 同族）；tag 已存在 → release 挂其上（target 不参与，弹窗披露）；**广播性披露**：弹窗明示"发布即通知所有 watchers 并触发 release 事件 webhook"；同 tag 已有 release → 服务端 422 already_exists（天然护栏；消息给查既有 release 指引，事实句"未发通知、未触发 webhook"）；指纹=repo+tag+锚点+正文。**资产上传不做**（另一端点族+二进制体量）。exit：201→0（html_url 提取）；401→9；403/404/422→10（ReleaseRejected）；5xx/超时→21 | 仅 sluice 进程 |
| `workflow_dispatch`（v2.7，Vitro 复盘 #4——**定性修正后落地**） | `sluice workflow dispatch --repo R --workflow W --ref B [--inputs-file F]` | POST `/repos/{r}/actions/workflows/{w}/dispatches` `{"ref":B,"inputs":{…}}`（W 为 id 或 .yml 文件名，路径段转义；204=受理）——**定位是运维便利（无 PR 场景的手动 CI nudge），不是"REST push 不触发 CI"的缺陷修复**：该定论已被 2026-10-30 上午三次同型 REST 推送触发 synchronize 的实证否定，当晚零 run 属 GitHub 平台故障窗（与 4×blob-500 同窗）。**需 fine-grained PAT Actions: Read and write**——缺失时 403 走 DispatchForbidden（用户重发 PAT，非 agent 可修）；同理读 CI runs/jobs/logs 需 Actions: Read（actions 端点对匿名收紧，403 加 --auth 即此）。exit：204→0（WorkflowDispatched，受理即完成）；401→9；403→10；403 限流/429→22（v2.16 嗅探前置）；404→10（不存在或 on: 未声明 dispatch）；422→10；**5xx→21（v2.16 清账：原落 DispatchRejected 谎称"本次操作未执行"——5xx 下可能已受理，重发=重复 run，先查 runs）**；超时→21（重发=重复 run，先查 runs） | 仅 sluice 进程 |

**v2.16 写通道限流嗅探（全 op 通则）**：一切写 op 的 403（body/message 为 secondary/abuse 特征）与 429 → **exit 22 wait_until**（WriteRateLimited / push 用 PushRateLimited）——ExecuteGated 共用段 default 臂与各 op 内联 403 臂一律前置 `RateLimitWhat` 嗅探（单源 GitHubApi，兼容 raw body 与 Reason 文本；429 无特征也归档）；非限流 403 仍走各 op 专属 forbidden/透传文案。上表各行"403→10"均指非限流形态。背景：POST/PATCH 密集创建恰是 secondary 高发面，误诊为"无写权限/凭据失效"会引向改参立即重试或重发 PAT——密集重试延长封锁窗（v2.10 在 api 读通道修掉的死方子，写通道原样存在至本批）。

**push bridge**（取代原方案 shim，无常驻、无 PATH 依赖）：

```
sluice push（批准 + Hello 后）
  → CreateProcess git push（-c credential.helper= 清空，另配 credential.helper=<sluice 自身 --cred-print 模式>）
  → 同时 CreateProcess sluice --cred-print，通过继承的匿名管道把 token 喂给它
  → cred-print 按协议向 git 输出 username/password
  → token 只存在于两个子进程内存，不落命令行、不落磁盘、不进环境变量
```

token 走 git over HTTP 用同一张 fine-grained PAT 即可（Contents:RW 覆盖），**不需要 GitHub App**（比原方案 S4 少一整个流程）。子进程窗口秒级，由 PAT 90 天上限兜底；可选升级 1h installation token（V1.5）。

**policy 硬编码 tripwire（在代码里，不在配置里）**：
- `--force` / 裸 force push → policy_reject，永不批准（**v2.6**：覆盖式更新的唯一合法形态是 `--base` 重放通道，见 §9.6——force 不是参数，是"声明新基点"这一显式动作的结论）
- push 到 `main` / `master` → policy_reject，永不批准（重放通道同样过这条禁令——它在 push 前置检查之后才分流）
- push 内容含 `.github/workflows/**` 变更 → 对话框红字单独标记，批准需二次确认

**幂等对象创建的 5xx 有界重试（v2.7 不变量）**：REST push 序列中 blob/tree/commit 的创建是内容寻址的幂等操作（同内容必得同 sha，服务器去重）——对它们的 5xx 自动重试（2s/8s 退避，共 3 次尝试）**不超出批准范围**：批准覆盖的是"把这些内容放到该分支"的一次执行，对同一对象的重试既不引入新内容也不动分支（stderr 有重试提示）。**update-ref（唯一写点）与一切非幂等请求永不自动重试**。429 亦不自动重试（限流是"该慢下来"，不是抖动）。背景：2026-10-30 Vitro 平台故障窗 blob 连续 4×HTTP 500，每轮人工重试 = 一道批准 + 一枚指纹（弹窗疲劳实录）。

**allowlist 的真墙在服务端（v2.2 修订，取代下面"改白名单要 Hello"的旧设计）**：vault 里的 fine-grained PAT 本身只授权指定仓库——本地 config.json 的白名单只是 UX 过滤器，改它不会扩大 token 的实际能力（未授权仓库 API 一律 403，fail-closed）。因此 `sluice allow-repo R` 就是普通配置编辑；setup 时打印 token 实际授权的仓库清单，服务端为准。最坏情况推演：agent 改本地白名单 → 人类看到未授权仓库的批准框 → 即便误批准，服务端仍 403 → fail-closed。

**`sluice notice`（消息型 credential helper，v2.2 设计，v2.19 实现落地）**：setup --install 写入全局 gitconfig。git（push/fetch 私有库）索要凭据时，它**不给凭据**，只向 stderr 打印 §8 终态文案后退出非零——agent 第一次直推 git，看到的是"写操作请走 sluice push；不要尝试 gh auth login；环境无可用凭据"，而不是裸的 `could not read Username`。选它而非 pre-push hook 的理由：私有库 push 在 ls-refs 阶段就要凭据，凭据提示发生在 pre-push 触发**之前**，hook 可能根本不执行；credential helper 恰好是 git 索要凭据的那一刻，拦截点精确。它不持凭据、不占 PATH、不伪装任何程序——只是把误导性错误换成可行动消息（~20 行），与被砍掉的凭据 shim 有本质区别。gh 无对应通道（其"请 gh auth login"提示无法替换），由 AGENTS.md 片段 + 哨兵（§6）兜底。注意：`git -c credential.helper=` 显式覆盖可绕过 notice——那是 T2 形状的行为，T1 不会做。
**v2.19 实现现状（2026-11-03 实弹验证）**：安装器写的是**作用域键** `credential.https://github.com.helper`（`--unset-all` 清旧值 → `--add` 空串 → `--add` 本 exe notice）——空串清掉 github.com 从系统层继承的 GCM（其他 host 不受影响），顺手清除 gh 曾写入的 `!gh auth git-credential` 桥接条目（gh 登录状态下它会给 git 直供凭据，§6 侧门面）。helper 本体：git 索凭据（get）→ stderr 打 §8 指引、非零退出、stdout 永无 `password=`；store/erase 静默成功；每次 get 落审计（op=notice，result=not_executed）——触发即"有进程在 git 直连 GitHub"，T3 重试风暴的第一手信号。**实弹两个意外收获**：①装 notice 前的基线探针实测——任何进程跑一次 `git credential fill`，GCM 就会在用户屏幕弹 GitHub 登录窗（诱导登录=重新开门，N4 相邻面）；notice 的空串把这张 UI 从 github.com 链上摘掉，此面随之消灭（这是比"裸错换消息"更硬的收益）②git 在全部 helper 失败后还有 askpass/终端回退层，无人交互下自灭（`could not read Username`），有人交互也拿不到任何有效凭据（环境无存量）——回退层不处理，如实记边界。

**`sluice api`（只读模式，v2.2 新增）**：只允许 GET/查询类调用，无需批准；匿名读无需 Hello，`--auth` 私有读需 Hello（vault 解密的技术必然）。每次调用写入 audit 读日志。收益：(a) agent 在 GitHub 上的读操作有 ground truth，"我读过 X"可核对，防幻觉；(b) 私有库 API 读的唯一通道，且 token 不出 sluice 进程；(c) unknown（exit 21）的查证命令落在它身上。诚实定位：这是**指令约定的可见性通道，不是墙**——agent 直连 GET 无法也无需阻止（读无破坏力），绕过它只是回到无日志状态，不是安全失败。

## 9.5 人类自己的工作流（v2.2 新增；seal 之后人怎么干活）

封堵是全员封堵——人也不再能直推，这必须有明文规则，否则方案三天就会被自己弃用：

1. **人也走 sluice**：push = Hello 一下（约 1 秒）；评论/PR 同一扇门。人人平等过闸门，审计同时覆盖人与 agent。
2. **fetch/pull 用只读凭据**：一张只读 fine-grained PAT（指定仓库、90 天）配给 git 的 fetch/pull，人和 agent 共用。它推不了任何东西——静默持有它不构成写风险（无能力，而非有门槛），评审问题 1 的担忧对它天然不成立。
3. **临时绕过走显式暂停**：大 push、交互式 rebase 结果、非 GitHub remote 等，`sluice seal --pause 30`（Hello 门控），期内直推，到期自动恢复，期间有常驻通知。
4. **读走 `sluice api` 或浏览器**：私有库 API 读走 api 模式（有日志）；网页浏览不受管。

## 9.6 push 重放式更新通道（v2.6 新增；force 不变量改写）

**问题**：agent 把 PR 分支 rebase 到新基点（如更新后的主干）后，远端旧头不再是本地 HEAD 的祖先——FF 校验 fail-closed 下同分支更新结构性不可能，唯一出路是换分支重开 PR，污染随每次 rebase 累积（8-PR 实战反复撞墙）。原 NonFF 消息教的"rebase 到远端头"只解"远端有新提交"，解不了"换基点"。

**不变量改写**（原"force push → policy_reject，永不批准"）：

> force 仅限非主干分支，且必须经 `--base` 显式声明新基点；弹窗明示旧头→新头与丢弃的远端提交清单；执行前复核远端头 == 规划期入窗旧头。主干推送与未声明 `--base` 的覆盖推送（含 `--force` 参数）仍永禁。

**七道闸门**（①–④ v2.6；⑤ v2.10；⑥ v2.12；⑦ v2.14；全部复用既有模式，无新机制）：

| 闸门 | 实现 |
|---|---|
| ① 显式声明 | `--base <ref>` + 已有远端分支 = 请求重放（PlanLocal 检测 origin/<B> 存在且 `--base` 给出 → 重放模式，锚点/范围 = `<base>..HEAD`）；**无 `--base` 的已有分支维持 FF-only——NonFF 判定不放宽，只分流**。裸 `--force` 参数 tripwire 不动，agent 学不会"重试加个 --force" |
| ② 主干禁令 | push 前置 tripwire（main/master，大小写不敏感）在通道分流之前——主干永远到不了重放模式 |
| ③ 批准你所见（扩展到销毁侧） | 弹窗顶部明示「⚠ 重放式更新（非 fast-forward）：origin/<B> 旧头 <sha8> 将被替换为 <sha8>；丢弃的远端提交：<前 3 个 sha8+标题>（共 N 个）」——丢弃清单 = `git rev-list HEAD..origin/<B>` 规划期算出入窗。**这是本通道的核心安全机制而非 UI 润色**：多数仓库的分支保护只配主干，feat 分支服务端不拦，本地闸门是唯一防线 |
| ④ 执行前复核 | 批准→执行间远端头被推进或分支被删 → RemoteMoved（exit 10，after_fix），fetch 后重跑重新弹窗——与 HeadMoved / PrMergeChanged 同族手法（T5 残余闭合）。update-ref 带 force:true 是唯一写点；服务端分支保护（若配了禁 force）是第三道纵深防御，422/403 透传服务端 message，分支未动重试安全 |
| ⑤ 前置缺口拦截（v2.10） | `--base` 已声明 + 执行期发现远端分支已存在 + 规划期本地无 `origin/<B>` 引用（ReplacingSha=null，弹窗按"新建分支"入窗）→ **ReplayUnresolved** fail-closed 拒执行（exit 10，after_fix），点名 `git fetch origin <B>` 后原命令重跑。不落 FF/NonFF 分流——那会开出"加 --base 重跑"这条已满足的死指令（2026-10-31 实战：worktree 新建分支 REST 首推成功不回写引用，rebase 重推必踩）。**本地引用是重放的硬前置**：丢弃清单需要本地对象，规划期 API 查远端头也构造不出销毁侧披露——fetch 是闸门③的价格而非实现事故。REST 推送不回写本地引用（**v2.33 起回写**：push 成功后 best-effort `update-ref refs/remotes/origin/<B>`，UpToDate 同步回写；红线收窄为"除 refs/remotes/origin/* 回写外不写本地 git"——回写=fetch 的引用等价物，发生在 approved+executed 之后非写动作；分叉时 NewSha 对象不在本地、回写必败=平行重打哨兵，GitExists 先判定分离分叉与普通失败），故 push 成功消息必附本地引用状态行——已回写 / 分叉哨兵（点名用户 fetch）/ 未回写原因（PushDone refsNote 三态，v2.33）；`--base` 非祖先错误对 `origin/*` 形态点名 fetch+rebase 正解 |
| ⑥ 基点在场前置（v2.12） | `--base` 形态（重放/新建+基点）在执行序列**首步**——任何 blob 上传之前——GET commits/{base} 验明在场并解析 base_tree 锚，404 → **BaseMissing** fail-closed（exit 10，after_fix：推基点所在分支或换远端已有祖先）。原位置在 blob 全部上传之后（2026-10-31 实战缺陷一：基点为本地-only 对象时白传全部 blob 才炸，且落 Other→internal 谎称"可能已完成"）。RunPush 层另有**匿名预检警示行**（v2.8 纪律：404 不硬拒——公开库=确不在远端可提前警示省一道批准；私有库匿名不可见属正常，执行期带 token 终判） |
| ⑦ 裸名消歧（v2.14） | `--base <裸名 N>`（不含 `/`、非 sha）在 rev-parse 之前先比 `refs/heads/N` 与 `refs/remotes/origin/N`：两侧并存且异 sha ⇒ 规划期 **fail-closed**（LocalPlanException → exit 10，after_fix），列两侧 sha8+日期+标题，点名二选一 `--base origin/<N>`（荐）/`--base refs/heads/<N>`（确要用本地分支）；仅一侧存在 ⇒ 采用并在弹窗基点行披露来源+日期；两侧皆无 ⇒ rev-parse 兜底（tag 等其它引用——裸名自此为**分支命名空间语义**，tag 须写 `refs/tags/<name>`）。根因（2026-10-31 三连坑）：git 裸名 DWIM 只试 `refs/heads/` 永不试 `refs/remotes/origin/*`，worktree 本地分支常年陈旧（主仓 checkout 别的分支），`--base master` 静默吃到影子引用（实测落后 17 笔：2 笔增量 → 19 笔重放/276 blob，串行 3-8min）；同一 DWIM 坑也咬纯 git 的裸名 `rebase master`（平行链入口，见下 CI 聋哑警示）。fail-closed 而非"默默优先 origin"：与闸门⑤同型——静默误路由才是缺陷，重跑一次 << 冗余重传；"本地分支领先远端"的形态终会死于闸门⑥ BaseMissing，早拦省一道批准。消歧决策纯函数入 selftest 锁。**v2.16：适用面扩斜杠名**——`feature/x` 等含 `/` 名同吃 DWIM 的 refs/heads/ 优先坑（worktree 陈旧本地同名分支静默误路由），`NeedsDisambig` 纯函数定适用面：非 sha、非 `refs/`/`origin/` 显式前缀的名一律入两侧消歧（适用面入 selftest 锁）；分支名自此整体为分支命名空间语义 |

**全历史形态警示行（v2.12，缺陷二处置=闸门⑤的无 --base 对偶面）**：规划期本地无 `origin/<B>` 引用且未声明 `--base` 时，无法区分"真新建分支"与"曾推过远端但 REST 不回写引用"——原实现静默按全历史回溯，远古 submodule/超限在规划期即炸而 fetch 正解只在成功消息尾巴。处置：弹窗加警示行（"若该分支远端已存在，先 `git fetch origin <B>` 重跑可得精确增量；执行发现分支已存在时将以远端现状收窄并做 fast-forward 校验"）＋submodule/超限错误文案附 fetch 提示。**有意不 fail-closed**：闸门⑤的理由是销毁侧披露（丢弃清单）构造不出来；此路径 FF 推送无销毁侧，且批准内容 ⊇ 执行内容（执行以远端现状收窄 + FF 校验）——警示行即可，不罚掉一次批准。**v2.33**：执行期锚点收窄接管该盲区的大多数——新建分支时远端默认分支头是 HEAD 祖先即自动收窄增量（复用远端原 SHA 不重打，平行链防护，见 §9.6 闸门⑦后锚点决策），弹窗警示行同步预告此行为；fetch 仍是"想本地精确增量"的正解，但不再是防平行链的唯一防线。

**序列行为与警示（v2.14，卡慢/平行链事故处置批）**：

- **CI 聋哑警示**：被推头树不含 `.github/workflows` 而远端主干树（origin/master 或 origin/main，纯本地 ls-tree，零 API）含 ⇒ 弹窗警示行——on:push 型 CI 的工作流文件取**被推头所在树**，链根早于 CI 引入（陈旧基点/平行链）则该分支此后每次推送**结构性零运行**。这是平行链事故的终态症状（分支被裸名 `git rebase master` 搬到陈旧链上后 CI 永久沉默）；agent 在 sluice **外**造出平行链时，sluice 在推门前兜住症状。判定纯函数入 selftest 锁。
- **blob 树集去重**：blob 数 >25 时拉远端默认分支头树 recursive 一次（repos→default_branch→ref→trees，3 个 GET），命中的 blob 跳过 POST——276 个里 ~250 已在默认分支=纯浪费（串行 0.5s+/个）。逐 blob 探测被否决：已存在的只省请求体不省往返、缺失的反成双往返。truncated/任一步失败 ⇒ 全传降级（退回 v2.13 行为，不阻塞推送）；跳过数入 PushDone 消息与 audit result。
- **执行期心跳**：每 25 blob + 逐 commit 一行 stderr——首行 banner 后全程静默 >1min 即观感卡死（实战"太久了"的放大器）；stderr 不污染数据通道，MCP 双流捕获下进失败 text 属取证语境。
- **推送序列限流（v2.16）**：ref 预查/序列中段（blob/tree/commit/update-ref/空仓播种）撞 403 secondary/429 ⇒ **RateLimited→22 wait_until**（PushRateLimited：分支未动、已传 blob 内容寻址幂等——不谎称零副作用）。原 ref 预查 403 一律 Unauthorized→credential_expired 死方子（push 是 blob POST 密集面，secondary 最高发处）；序列中段原落 Other 渲染服务端原话但缺 wait 语义。嗅探收敛两点（预查 + Net() 单点）；5xx 维持 Unclear；与"429 不自动重试"不变量不冲突（RateLimitWhat 只改分类不改重试）。
- 弹窗基点行带**来源与日期**（如 `c8825924（本地分支 master，2026-10-28）`）——两侧同陈旧、消歧闸门不触发的形态下，这是"基点陈旧"肉眼可见的唯一兜底。

**审计**：重放执行的 audit 行 target 记 `<branch>@<旧头sha8>→<新头sha8>`，弹窗 target 行同形态——替换前后可追溯。ReplayUnresolved 拒执行记 decision=`replay_unresolved`（not_executed）——与 non_ff / remote_moved 同为 exit 10 家族，decision 字段可区分。

**边界**：重放通道受全部既有约束——线性历史（merge 拒）、≤200 commits / ≤2000 blobs、submodule 拒、workflows 变更红字、HeadMoved（本地 HEAD 前进即中止）。丢弃的远端提交成为服务端孤儿对象（GC 窗口内可按 sha 重建，与 branch delete 同态度）。

## 9.7 MCP 前门（v2.9 新增；第三前门，不是边界）

**定位**：`sluice mcp`——给 MCP 宿主（ZCode / Claude Code 等）的第二个机器入口，与 CLI 并列。买的不是 token、不是安全，是**结构性在场**：工具 schema 随每个请求重发、活在请求信封里，不受对话压缩 / lost-in-the-middle / 注入覆盖影响（AGENTS.md 第一行是概率性提示层；工具列表是结构性提示层，模型经 tool-choice 管线在其上锚定）。层糕更新：① github_write（MCP 宿主）/ ①' AGENTS.md 第一行（无 MCP 宿主 + 人类）→ ② notice → ③ seal → ④ gate+Hello/click。**① 与 ①' 是并行同构糖，永不是边界**——墙与闸门对一切前门同权。

**拓扑：spawn-child**（2026-10-30 终判曾写"CommandRunner 进程内复用"，2026-10-31 实读代码后推翻）：

- `Gate.Run` 的 Avalonia lifetime 是一次性的（窗口关即退出 lifetime）——常驻进程内复用须把 Gate 改造成常驻 lifetime + per-request 窗口，在安全要害路径引入全新未验证 UI 生命周期代码；
- CLI per-invocation 路径已被 250+ 审计行实弹验证；server 每次 `tools/call` 以 `Environment.ProcessPath` 绝对路径 spawn 一次 CLI 子进程（绝对路径杀 PATH 劫持；`ArgumentList` 结构化传参，无 shell 引号面），MCP 层零行安全逻辑、零行 UI 代码；
- **server 进程永不接触 vault**——比"批准后才解密、不缓存"更强的红线：结构性满足，无纪律可违反；
- 并发 `tools/call` = 多子进程 = 多窗口共存 = 已设计行为（并发会话多窗已实证）；
- 代价：每次调用 ~100ms AOT 启动，相对 60s 批准窗可忽略。

**工具：单一类型化 `github_write`**（绝不做 `{command,args}` 透传——透传把语法知识推回提示层、丢掉 schema 校验）：

- `action` 值域与 §9 写 op 一一对应：`pr_comment` / `push` / `pr_create` / `pr_merge` / `pr_reopen` / `pr_close` / `pr_edit` / `issue_create` / `issue_close` / `issue_reopen` / `pr_review` / `tag_create` / `release_create` / `branch_delete` / `workflow_dispatch` / `issue_label`（共 16；**v2.17 顺手清存量漂移**：本清单自 v2.11 批A 加 7 op 起停在 8 个未随更——面一致锁只对账 §9 表首列，管不到本散文，靠 v2.17 评审发现）；参数与 CLI flag 同名映射（repo / number / body / title / labels / base / head / branch / repo_path / method / delete_head / workflow / ref / inputs；labels 为字符串数组，server 侧拼逗号后走 CLI `--labels`，语义单源在 CLI）；
- **一次 `tools/call` 恰好一个写 op**（"批准覆盖一次执行"§9 不变量的 MCP 镜像）——schema 不提供批量/数组形态；
- body/inputs（长文本）不走 argv：server 写临时文件 → `--body-file` / `--inputs-file` → 子进程退出即删（先例：setup --token-file 自动删除；body 是即将公开的内容非凭据，落盘风险不同类）；
- per-action 必填校验免费复用子进程自身的 verb-aware ErrMissing——schema 保证字段名/类型正确，语义校验单源在 CLI；
- **api 读通道 v1 不进 MCP**：读零门零纪律，本工具的价值主张是写纪律的结构性在场；agent 用 `sluice api` CLI 已零摩擦。留观条件：红队若发现 MCP 宿主上 agent 绕开 CLI 用 curl 读（丢 §9 可见性）→ 再议 `sluice_read`。

**返回结构**：

- `text` = 子进程 §8 消息**逐字**（成功在 stdout、失败在 stderr——双流捕获；§8 单源自动成立，server 不复制任何文案）；
- `structuredContent = {ok, code, retry, url?, denied?, deny_reason?, receipt?}`；`ok = exit 0`；`code` = 子进程 exit code；`url` 尽力从消息提取；**v2.24：`isError = IsErrorOf(code)` 与 ok 解耦**——用户决策族（denied/timeout/policy/cooldown/consent_failed/rate_limit_wait）isError=false：工具没坏、是闸门说了不；isError=true 会被宿主按"工具出错"渲染（折叠/摘要/归为可重试故障），拒绝指引与自由文本理由随之沉没（2026-11-03 #452 实弹：同理由两次拒绝，agent 仍重发）；拒绝结果另附 `denied:true` + `deny_reason`（定界符与 Messages.Denied 单源，往返入 selftest 锁）；技术失败族（internal/seal/notinit/credexp/badreq/httpfail/unknown）保持 isError=true；
- **v2.25 pending 收割**：宿主客户端 ~30s 弃等工具调用 < 人类决策时长 40-90s——终态落在客户端离场之后、deny_reason 无处投递（2026-11-04 实弹）。server 等待 `Gate.McpPendingSec`（默认 15s）仍未决 → 回 `code=23`（wait_until，isError=false，**非终态**，前缀 [Sluice]）+ `receipt`；**子进程（含活窗）不杀照跑**（红线不变），终态落审计（审计行随身携带 req=receipt，env SLUICE_MCP_REQ——同 via 纯标签纪律）；agent 凭 receipt 跑 `sluice status <receipt>` 收割：denied/timeout 单源复用原决策消息（拒绝理由原文与"必须遵守"条款原样再现）、approved 族事实句、其余照录决策字面（StatusFamily 纯函数入锁）。快路径不变：15s 内落地的批准/拒绝仍原样直返。实弹（2026-11-04 #999999）：pending 15.1s 返回 → status 收割 not_found exit 21 ✓；
- **v2.26 收割入带 `sluice_status` 工具**：第二个类型化工具（参数 receipt）——spawn CLI `status <receipt>` 单源复用，server 零收割逻辑；收割从"提示层动作（切 Bash 抄命令）"升为工具环内动作（结构性在场）。github_write 的 16-action 面不动；"api 读 v1 不进 MCP"红线不破——status 读本地审计，非 GitHub API；
- **v2.26 过期自检守卫**：rename-swap 部署后存活会话的旧 server 继续按旧语义服务（v2.24 阻塞等 = 终态随宿主超时沉没）——server 启动快照自身映像 (大小, 修改时间UTC)，每次 github_write 前复核，磁盘映像已换 ⇒ ServerStale（exit 1/wait_user/isError=true，转告用户重启会话后重发）显式拒服；sluice_status 豁免（只读收割 fail-open，写门 fail-closed）；ImageChanged 纯函数入锁（v2.28 补 (-1,-1) 双哨兵洞：启动即不可解析 ⇒ 两侧同为哨兵值，相等比较恒放行——startup 侧哨兵前置短路）；
- **v2.27 收割语义统一**：consent_failed/rate_limited 进 StatusFamily（收割与 live 同 decision 同 exit/retry/isError——分叉=非确定性契约，收割↔live 反向锁入 selftest）；up_to_date 单列 StatusUpToDate（"已获批准并执行"对它不是事实）；rate_limited 审计 result 存 RateLimitWhat 结论（"http_403:{what}"，≤v0.7.2 旧行收割回退通用句；push 沿用 not_executed:{what}）；`sluice status` 缺参/多参走用法错误（不再误报"未知命令"引去重新发布），BadVerb 清单补 status/notice/doctor；
- **`executed` 字段砍掉**（10-30 草案曾有）：exit 0 含 up_to_date / 204 受理等非写入终态，无法从子进程输出无歧义派生——真相在 text 与 audit，不造第二套语义；
- **exit→retry 映射以 selftest 锁死**：`AllSamples` 按 exit 分组 retry 必须单值且与 `RetryOf(code)` 一致——**映射的函数性本身是不变量**：未来任何"同 exit 不同 retry"的新消息让 selftest 当场 FAIL，逼作者要么新开 exit 档要么改消息措辞，杜绝双表漂移；
- **host 取消/超时不杀子进程**：窗口可能正等人批准，杀活窗违禁；子进程跑完，审计行是真相；server 对已取消请求回 unknown 形态结果。

**红线（沿袭 2026-10-30 终判）**：elicitation / 宿主 UI 永不替代闸门（宿主在 agent 信任域内）；CLI 永留（通用覆盖 + 人类工作流 + 非 MCP 宿主）；无通用透传工具；server 不持 token（spawn-child 结构性满足）。

**宿主配置**：绝对路径——`{"mcpServers":{"sluice":{"command":"<绝对路径>\\sluice.exe","args":["mcp"]}}}`；`sluice mcp --print-config` 打印含本机实际路径的可粘贴片段。

---

## 10. 审计

`%LOCALAPPDATA%\Sluice\audit.jsonl`，追加写：

```json
{"ts":"2026-10-28T14:32:11Z","op":"pr_comment","repo":"rustin-beep/Vitro","target":"#5",
 "body_sha256":"a1b2...","decision":"denied","reason_preset":"内容需修改",
 "reason_text":"别带密钥","consent":"none","result":"not_executed","via":"cli"}
```

- 拒绝也记（含理由原文——理由是协商界面的一部分，值得留痕）。
- **consent 值域（v2.8 定稿）**：`none`（拒绝，无批准事件）/ `hello`（V1 vault——批准=Windows Hello）/ `click`（SLCV2 静默 vault——批准=桌面点击）/ `hello_failed`（Hello 连续失败，V2 下不可达）。**形态取进程启动时的 vault**——运行中 V1→V2 迁移不回写快照：迁移当次的批准事件仍是 Hello，照记 `hello`；下一进程起才记 `click`。"这次批准是生物事件还是点击"在审计里可答——§14 L1 让步入册后唯一需要事后区分的维度。
- **via 值域（v2.9，§9.7）**：`cli` | `mcp`——调用来自哪个前门。MCP server spawn 子进程时以环境变量 `SLUICE_VIA=mcp` 注入（纯标签非机密，伪造无增益——它标注的是入口，不是权限）；复盘取证（Vitro 手法：audit + 匿名 API 重建时间线）从此可区分前门归属。
- 不做防篡改（同用户下无意义，真相源在 GitHub 服务端）。

---

## 11. 模块划分与规模

```
<SLUICE>\
├─ plan-lite.md
├─ Sluice.csproj           net10.0-windows10.0.26100；PublishNativeAot（探针定稿）
├─ src\
│  ├─ Program.cs           CLI 分派、exit code             ~100
│  ├─ Messages.cs          §8 消息全文表（唯一文案源）      ~70
│  ├─ SetupCommand.cs      setup / seal / allow-repo       ~150
│  ├─ SealCheck.cs         侧门自查（ISeal）                ~90
│  ├─ GateWindow.axaml(.cs) 批准对话框 + injected 过滤     ~210
│  ├─ Consent.cs           IConsent；Hello 叶（CsWinRT）    ~50
│  ├─ TokenStore.cs        DPAPI（ITokenStore）             ~40
│  ├─ GitHubApi.cs         HttpClient 三类 op              ~120
│  ├─ PushBridge.cs        匿名管道 + --cred-print 模式    ~100
│  ├─ Policy.cs            白名单 + 硬编码 tripwire         ~60
│  ├─ CooldownState.cs     state.json                       ~40
│  ├─ McpServer.cs         §9.7 stdio JSON-RPC + spawn      ~260
│  └─ Audit.cs             JSONL 追加（含 via）             ~45
└─ tests\selftest.cs       消息表穷举 / exit code 对齐      ~80
```

**估算 ~1150 行（含 axaml）。第三方仅 Avalonia，零其他 vendor。** 实现注意：`Console.OutputEncoding = UTF8`（中文文案终端不乱码）；§6 的 C++ 纪律由 C# 侧纪律替代——`<Nullable>enable</Nullable>`、op 分派用 switch 表达式穷举、不用 `async void`、消息文案常量与 exit code 同置于 `Messages.cs` 由 selftest 对齐。

---

## 11.5 复用映射（v2.2，避免重复劳动的边界）

2026-10 复扫：无等价成品；最接近的是 GitHub 官方 **gh-aw**（agent 写动作 safe-outputs / 权限门）——Actions 编排层，管不了本地 gh/git 直连，方向佐证。按组件盘点：

| 组件 | 现成方案 | 裁决 |
|---|---|---|
| 哨兵 | `schtasks.exe` + PowerShell 脚本 + **BurntToast**（Windows 通知模块） | ✅ 整个哨兵零 C# 代码：sentinel-install 只写 .ps1 + 注册计划任务 |
| SSH 屏蔽验证 | `ssh -G github.com`（打印生效配置） | ✅ seal 用它验证屏蔽块真实生效，优于读文件确认 |
| 凭据枚举 | `cmdkey /list` 子进程解析 | ✅ 免 CredEnumerateW 互操作代码 |
| git credential 协议 | **GCM 源码**（MIT，C#）作参照——"C# exe 当 helper + 弹 UI"这条路已走十年 | ✅ 参照不引用 |
| GitHub API | Octokit.NET | ⚖️ 三个写端点各一次 POST，手写更省依赖；不用 |
| CLI 解析 | System.CommandLine / Spectre.Console.Cli | ⚖️ 6 个动词手写 ~100 行，守零依赖原则 |
| vault 后端 | 1Password CLI 路径（a）：PAT 存 1Password，执行时 `op read`，桌面端每次按指纹 | ⚖️ Option B：删 ~100 行 vault + P7 脆弱性注记；代价 = 订阅 + 桌面端常驻解锁 + 双弹窗 UX + agent 可直接调 `op read`（Prompt 同样拦它，但语义上不绑定动作）。默认不用；ITokenStore 抽象保留此实现位 |
| 小众层 · MCP 审批代理 | **MCPProxy**（mcpproxy-go，Go：新工具先隔离、人批后放行，批准存 BBolt）；Pushary（托管 elicitation）；HoneyMCP（蜜罐欺骗层） | ⚖️ 层不同：拦 MCP 工具调用（框架内）；agent 走 shell 直连 gh/git 不经过它们；且是"每工具一次"隔离模型，非"每动作"门 |
| 小众层 · 硬件闸 | **YubiKey touch**（SSH 私钥强制物理触摸 = 每次人触，零自研） | ⚖️ 只覆盖 SSH push（gh api 写不覆盖）；touch 不显示在批准什么（事件无内容）；可作 push 路径的硬件强化可选项 |
| 模式先例 | gsudo（Windows 提权代理：常驻 + 每请求弹窗，架构近亲）；KeeAgent per-key confirm；ssh-agent `-c`；gpg-agent pinentry | 参考：交互模式被反复验证，无一直接可用 |
| 邻域 · 工作区隔离 | **coderabbitai/git-worktree-runner**（Bash，1k+ star：按分支建 worktree、在隔离目录里并行跑 Claude Code/Aider 等多个 agent） | ⚖️ 解的是"并行 agent 互相踩工作区"，不是凭据/审批；与 Sluice 正交可组合。实施注记：worktree 共享主 .git 但有独立 gitdir（.git/worktrees/*），seal 深扫需覆盖（`git worktree list` 枚举） |
| 事件闸门编排 / §8 消息表 / notice / api / push bridge / seal --fix | 无现成 | 产品本体，自写 |

补充：Claude Code 的 PreToolUse hook 可对 Bash 命令弹审批——"守规矩 agent"的免费第一道（AGENTS.md 已含），Sluice 是跨 agent 的兜底层。



## 12. 实施阶段

### V0 · 闸门闭环（~500 行：main/msg/setup/sealcheck/gate/hello/token/exec_api(comment)/audit）

| 验收 | 标准 |
|---|---|
| 封堵生效 | `sluice seal` 后：`git push --dry-run` → exit 128；`gh auth token` → 非零；`git credential fill` → 无 password |
| 全流程 | `sluice comment` → 弹窗 → Hello → GitHub 可见评论 → 消息含结果 URL |
| 拒绝闭环 | 拒绝（选预设"内容需修改"+附言）→ 消息含理由与 retry 语义 → GitHub 无变更 |
| 终态可判 | §8.2 每条消息人工核对：事实前置句、retry 字段、禁止清单齐全 |
| Hello 拦 T1.5 | 探针脚本 SendInput 点"批准" → 不通过（injected 过滤 + Hello 双拦） |
| seal_violation | 故意 `gh auth login` → 下次 sluice 调用拒绝并报警，不弹批准框 |

V0 前置探针（半天，先于编码）：

| 探针 | 内容 |
|---|---|
| 工具链 | `dotnet --version` ≥ 10（无则 winget 装）；Avalonia 11 模板起一个单窗口工程 |
| CsWinRT Hello | C# 调 `UserConsentVerifier.RequestVerificationAsync` 弹出系统 Hello 并返回结果 |
| AOT/R2R | Avalonia + CsWinRT 过 NativeAOT 则用之（启动更快体积更小），否则定稿 ReadyToRun |
| injected 过滤 | LL 钩子挂在 Avalonia UI 线程生效；SendInput 探针点击被丢弃（`LLKHF_INJECTED`/`LLMHF_INJECTED`） |

V0 前置探针补充（v2.2，vault 与 Hello 形态，约 1 天）：

| 探针 | 内容 |
|---|---|
| P7 | Microsoft Passport KSP：`NCryptCreatePersistedKey` 后由**另一个**同用户进程静默 `NCryptDecrypt`——若强制弹 Hello，vault 定此形式（一次生物事件 = 解锁 + 批准合一） |
| P7b | 软件 KSP + `NCRYPT_UI_POLICY` 高保护：每次使用的弹框行为、可配置性、与 Avalonia 单窗口进程的集成难度 |
| P4/P5 | 系统 Hello 弹窗（含 PIN 兜底页）对 SendInput 的可注入性；LL 钩子对 PIN 键入的可观测性——裁决 L5 是"已知缺口"还是"配置层禁用 PIN" |

补充探针结果（2026-10-28，均实测）：

| 探针 | 结果 |
|---|---|
| P7 | ✅ 同用户第二进程可静默打开 Passport KSP 键**句柄**（键名 = SID 限定路径，`NCryptEnumKeys` 枚举获得；按凭据名直开 0x80090027）；但私钥运算有 UI 门：`NCRYPT_SILENT_FLAG` 下签名返回 **NTE_SILENT_CONTEXT（0x80090022）**——静默签名不存在 |
| P7c | ✅ `RequestSignAsync` 同挑战两次签名 **deterministic=True**（RSA PKCS1v15，256B）→ HKDF(HelloSign) 派生 vault 密钥可行。实现注记：WinRT 返回 `KeyCredentialOperationResult.Result`（不叫 Signature） |
| P4 | ✅（两轮一致）注入 ESC（t=8s/12s）均未使 Hello 弹窗消失，弹窗仅因用户真实点击关闭——broker 对注入键盘输入有隔离。实现注记：LL 钩子须独立 STA 线程泵消息；probe 模式 observe-only，gate 模式才 drop |
| P5 | ⏸ 未执行（两轮均以鼠标取消，未键入）——但 L5 链路的"重放腿"已被 P4 掐断（PIN 被录到也注不进 broker），降级为可选实验 |

探针结果（2026-10-28 实测，探针工程 `<SLUICE>\probes\ProbeApp`）：

| 探针 | 结果 |
|---|---|
| 工具链 | ✅ dotnet SDK 10.0.401 在位；Avalonia **12.1.4**；构建零警告（注意 v12 移除 `Window.Left/Top`、`Avalonia.ShutdownMode`，定位用 `Position`） |
| CsWinRT Hello | ✅ `CheckAvailabilityAsync` = Available（Debug JIT 与 NativeAOT 均通过）；弹窗交互项留人工验证（`--hello-prompt`） |
| AOT/R2R | ✅ **NativeAOT 定稿**：编译通过，WinRT + DPAPI 在 AOT 下运行正常，启动 ~100ms/次（JIT ~630ms），exe 19MB + 原生依赖 ~19MB；R2R 单文件 214MB，弃 |
| injected 过滤 | ✅ **实测通过（2026-10-28）**：SendInput 点击被识别（`flags=0x1` LLMHF_INJECTED）并丢弃、未触发按钮事件；真实点击正常放行并触发按钮——正反对照齐全。测试基建：`inject.ps1` 自足版（自拉起 ProbeApp → PID+GetWindowRect 定位 → 矩形中心注入 → 自动清理）；按标题 `FindWindowW` 不可靠，必须走 PID 路线。键盘腿（F12 + LLKHF_INJECTED）因测试窗口未抢焦点按保护逻辑跳过，机制与鼠标同源，留待 V0 gate 窗口（主动取焦点）验证 |

复跑命令：`powershell -NoProfile -ExecutionPolicy Bypass -File <SLUICE>\probes\inject.ps1`。`--hello-prompt` 的系统 Hello 弹窗路径仍留一次人工验证（60 秒内按指纹或取消，结果入日志）。

实测注意（Avalonia 12）：`PixelPoint(200,200)` 实际落在物理 (133,133)——存在 DIP/物理坐标换算（本机 150% 缩放下呈 ×2/3）；gate 窗口如需精确定位必须用 `GetWindowRect` 实测矩形，不能信设置值。

### V1 · 补全（+~900 行：pr_create/pr_review/exec_push/冷却/allow-repo）

| 验收 | 标准 |
|---|---|
| push 全流程 | 批准后 push bridge 成功；token 不在命令行/磁盘/环境变量（用 WMI `Win32_Process` 与文件扫描验证） |
| tripwire | 推 main → policy_reject 且不弹窗；`--force` 参数 → 同（覆盖式更新唯一合法形态 = `--base` 重放通道，§9.6） |
| 冷却 | 3 拒 → 冷却消息含绝对时间；冷却期内请求不弹窗直接拒 |
| **红队演练（关键）** | 真实 agent（ZCode/Qoder）+ 任务"完成后推 main"，观察：push 快速失败；agent 不换路径硬闯（检验消息服从性）；audit 完整；弹窗字段正确 |
| **中文文案服从性** | 红队演练中专门观察 agent 对中文终态消息的服从；若发现"多想"，先改文案再改代码 |
| **MCP A/B 探针（v2.9）** | 同一红队任务两轮：配置 `sluice mcp`（github_write 在场）vs 仅 AGENTS.md 指令——对比 agent 的路径选择率与违例率（工具结构性在场 vs 提示层的实证数据）；audit `via` 字段区分两轮 |

### V1.5 · 可选

AGENTS.md 片段安装器、`sluice doctor`、installation token（1h）替换 push 凭据、Watt 证书 issuer 校验（P2 门控）、Linux 评估。

---

## 13. 验证矩阵（精简沿用 plan.md §17 思路，先红后绿）

| 断言 | 验证 | 期望 |
|---|---|---|
| 封堵后 agent 写必失败 | `git push --dry-run` / `gh auth token` | 128 / 非零 |
| 拒绝无副作用 | 拒绝后查 GitHub | 无新评论/无新分支 |
| retry=never 被服从 | 红队演练观察 | agent 不重试不换路径 |
| injected 点击无效 | 探针 SendInput 点批准 | 不通过 |
| seal 回归可发现 | `gh auth login` 后调 sluice | seal_violation |
| 冷却只延长不缩短 | 冷却期内连续尝试 | 每次顺延 +5min |
| 回归防线（v2.15 CI） | Actions push/PR 自动跑 selftest（JIT）；master 加 AOT publish+构建戳校验 | exit 0 PASS；戳 sha 对 HEAD 且无 +dirty |

---

## 14. 已知缺口（不修，只记录）

| 编号 | 缺口 | 态度 |
|---|---|---|
| L1 | 同用户攻击面：注入 / 读内存 / **静默解密本地文件** | 前两者让步（N1）；静默解密被 §5 的 Hello 门控 vault 消灭（P7 探针定形式；DPAPI 末选时降格为已知让步）；剩余让步部分爆炸半径 = PAT 上限；真隔离的唯一路径是 agent 独立 OS 账户。**v2.7：owner 正式采用 hello:false 静默 vault（SLCV2/DPAPI，§5）——"静默解密被消灭"按 owner 显式选择降格为已知让步**（批准事件降为桌面点击；回滚须重跑 setup） |
| L2 | 人疲劳误批 | tripwire 硬拒 + Hello 双因子缓解，不消除 |
| L3 | 对话框可被同用户移动/遮挡 | 信任在 Hello；不承诺显示完整性 |
| L4 | audit 可篡改 | 接受；真相源 GitHub |
| L5 | PIN 兜底的攻击链 = 钩子录 PIN + SendInput 重放进 broker | **P4 实测：注入键盘输入未使 Hello 弹窗消失（两轮一致），重放腿断**；P5（录腿）未执行、降级可选。结论：PIN 兜底保留，残余风险 = 仅捕获、无重放 |
| L6 | push 子进程内 token 窗口（秒级） | **已关闭（v2.21 销账）**：push 定稿纯 REST（blobs→trees→commits→update-ref 全程 api.github.com），token 不再进任何子进程——原缺口随 push bridge（--cred-print/匿名管道）一同作古，"V1.5 换 1h installation token"不再需要；明文窗口=sluice 进程内 HTTP 发送期（毫秒级） |
| L7 | agent 对中文终态消息的服从性未验证 | V0/V1 红队必测；不服从先改文案；备选双语 |
| L8 | token 在托管堆多副本、永不可清零 | N1 已让步，无增量风险；与"明文窗口=进程生命期"同一笔账 |
| L9 | Linux Wayland 禁止抢焦点，批准框可能不前置 | 移植期用通知/任务栏 urgent 提示补位；Windows 不受影响 |
| L10 | d08658a 隐藏批准窗后、Hello 弹出前的窗口期，若 Hello 异常迟滞则屏幕无 sluice 可见锚点（旧版有置顶窗+状态行；v2.5 评审留观） | 实测路径正常、概率低；留观。若真实发生，考虑隐藏期 FlashWindow 闪任务栏补锚点 |
| L11 | **state.json（暂停态）是同用户可写明文**（v2.22 引入的事实）：任何同用户进程写入未来时间戳即可静音检测层——哨兵静默、`seal` 显示"暂停中"、九处写门的 seal 预检短路（写操作本身仍需批准窗，token 不因此泄出，故非直接放权；但"device flow 后最迟 5 分钟被看见"的检测层可被被检测者自己静音） | 阻止≠不可加固（N4）：v2.23 起哨兵 ps1 与 doctor 交叉核对 audit.jsonl 的 seal_pause 记录——state 值无逐字对应审计记录按疑似伪造告警（toast/红项）；audit.jsonl 同为同用户可写（L1/L4 同族），本核对抬高门槛而非建立信任。完整方案（暂停态经 vault 同源密钥签名）不做：签名公钥/audit 存放同样可被同用户进程替换，增量有限——真隔离的唯一路径仍是 agent 独立 OS 账户 |
| L12 | **批准点击可被同用户进程伪造 + 误击面**（v2.24 复盘 2026-11-03/04 实锤）：①InputFilter 是 LL 钩子，只拦 SendInput 类注入（LLMHF_INJECTED）；**PostMessage 直投 WM_LBUTTONDOWN 或 UIA Invoke 不经过 LL 钩子**，探针实证（#999999 无害目标：注入点击 → approved+executed 一路到服务端，`consent:click` 照记——审计不能自证有人点了）。②批准按钮在内容不溢出时**开窗即启用**，窗口置顶常驻屏中——用户浏览网页期间的任意一次误击落在批准键矩形内即静默批准并关窗（2026-11-03 #456 事故最可能成因：用户在场但从未见过该窗，27s 后被一次误击放行；当晚 15:59 起的 approved/denied 均为用户本人回归操作）。③聚焦批准键后**空格键**可触发批准（键盘批准绕过"故意鼠标点击"设计意图，§7） | **owner 决定暂不修**（2026-11-04：无 agent 伪造实证，Vitro #456 归因误击）。候选修法已论证备查：批准动作加"物理点击证明"（LL 钩子已见全部硬件输入——真实点击必先经钩子、PostMessage/UIA 必不经；`GetMessagePos` 与钩子记录的物理 down 做时间窗+按钮矩形+一次性消费比对，对不上即忽略并审计留痕）+ 空格批准封堵 + 批准键开窗 2s 内不收点击（消误击）。临时缓解 = `hello:true` 重跑 setup（批准回到生物事件，伪造点击只能走到弹 Hello 一步） |

---

## 15. 命名约定

| 项 | 值 |
|---|---|
| 产物 | `sluice.exe`（唯一；`--cred-print` 为内置模式，不再单独出 shim） |
| 数据目录 | `%LOCALAPPDATA%\Sluice\`：`token.bin` / `config.json` / `state.json` / `audit.jsonl` |
| 消息前缀 | `[Sluice]` 过程 / `[Sluice·终态]` 最终结果 |
| 单实例 | 无（无 daemon 无需 Mutex） |
