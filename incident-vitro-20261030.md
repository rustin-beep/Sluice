# Vitro 2026-10-30 夜事故复盘与处置 · 2026-10-31

> 证据源：`%LOCALAPPDATA%\Sluice\audit.jsonl`（476 行，含当晚全部写操作与读日志）×
> `sluice api` 匿名读（actions/runs、pulls/N、issues/N/events、contents）交叉对齐。
> 方法：先审计后结论——时间戳、decision、target 三列还原完整时间线，不靠回忆。

## 0. 时间线（UTC）

| 时刻 | 事件 | 证据 |
|---|---|---|
| 12:35 | 重放通道（当晚未提交工作树）在 feat 分支弹窗 → **用户拒绝**，free-text「功能我会进行增加 不必重放 让ci绿即可」 | audit `push feat@ac2d1e0f→2db08b8a denied`（§9.6 审计格式，先于 22160c3 提交 39 分钟） |
| 13:57:22 | PR #389 opened（head `iss-377-376-include-diag` @ `21ed96f1`） | pulls/389 + audit pr_create |
| 13:57:25 | CI run（pull_request/opened）→ **failure** | runs 41067578177 |
| 14:24–14:25 | push ×4 连续 `blob 上传 HTTP 500`（每轮 = 一道批准 + 一枚指纹） | audit ×4 `unknown not_executed:blob 上传 HTTP 500` |
| 14:28 | 第 5 轮成功推上 `a7e815e3` → **零 CI run** | audit `approved executed:a7e815e3`；runs 该 sha 无记录 |
| 14:45 | 空提交 `82542e9`（CI re-nudge 尝试）→ **零 CI run**（= #389 关闭时的 head） | audit + p389.head.sha |
| 01:08 | push non_ff 被拒（agent 未再提重放——尊重 12:35 否决） | audit `non_ff not_executed` |
| 01:09:47 | branch delete → **#389 自动关闭**（弹窗未提示此连带后果） | issues/389/events `head_ref_deleted`+`closed`；audit branch_delete |
| 01:11–01:15 | 重建分支 + #390/#391/#392 → CI 3 秒内逐个触发，全绿 | audit + runs 41154272843… |
| 01:18 | AOT republish（92e902e5） | --version 戳 |

## 1. 缺陷表逐行裁定（用户原始 7 行）

| # | 原表 | 裁定 | 依据 |
|---|---|---|---|
| 1 | push 重放通道（v2.6-lite）未实现 | **过时** | 通道已实现且**当晚二进制就有**（12:35 弹窗为证）；01:08 撞 non_ff 后 agent 尊重否决走了删+建老路。真实代价 = 3 道门（delete+push+pr_create）vs 重放 1 道，外加 #389 断链。处置 = 批准一次实弹首胜（至今零实弹），非建设 |
| 2 | 无 pr reopen op | **成立** | 动词表无此 op。处置 = **已建** `pr reopen`（v2.7） |
| 3 | 无 issue close op | **成立** | 同上。处置 = **已建** `issue close`（v2.7） |
| 4 | 无 workflow_dispatch op；「REST push 的 synchronize 不触发 CI，本只能靠 dispatch 补」 | **观察实锤，定性错误** | 「REST push 不触发 synchronize」被同日 10:42/11:07/11:17 三次同型推送（PR #384 分支，FF、PATCH git/refs）分钟级触发 run 直接证伪；ci.yml 三个旧 sha 逐字节相同（101317B）无 `types:` 收窄；晚间两次推送审计 target 为裸分支名（非重放格式）= 普通 FF。**最可能根因 = GitHub 平台故障窗：14:24–14:28 的 4×blob-500 是同窗直接证据**（API 与事件管道同源退化，21:47 schedule 已恢复）。处置 = dispatch **已建**（v2.7）但明确定性为运维便利；**CI 补救优先 `pr reopen`**（reopened 是默认触发 type）。钉死平台故障结论的证伪实验：任一开放 PR 上做一次 FF 推送，60s 内看 run |
| 5 | blob 上传 500 无自动退避，第 4 次才成功 | **成立（数字修正：4 次 500 后第 5 次成功 = 5 道门）** | audit 14:24×2+14:25×2+14:28。处置 = **已建**幂等对象创建 5xx 有界重试（2s/8s×3，update-ref 永不自动重试；不变量入 plan-lite §9）+ hello:false（已启用）消指纹 |
| 6 | --auth 断，「证书白名单未做」 | **过时 + 三拆** | ①CA 白名单**已修**（33557dd，AllowedIssuers+Microsoft TLS；01:29Z runs 列表 200 = 修复后实弹）②「403 admin」= actions 端点对匿名收紧 + fine-grained PAT 需 **Actions: Read**——用户重发 PAT 可解，非代码缺陷（读 runs/jobs/logs 同因）③**新发现的结构缺陷已修**：api 读通道原不跟随重定向，logs 端点 302→CDN 签名 URL 永远拿不到日志体——v2.7 受控跟随（≤3 跳，仅 GitHub 域；DoH 同域扩展；tarball 端点实测 200/9.7MB） |
| 7 | branch delete 弹窗不明示连带关 PR | **成立** | 原弹窗只警告孤儿提交。处置 = **已建**静态警示行 + 尽力匿名预查 `pulls?head=` 命中列 PR 号红字提示（§7 规则行）；预查失败不阻塞 |

## 2. 处置落点（全部 LANDED，单 commit）

- 代码：`pr reopen` / `issue close` / `workflow dispatch` 三 op（含预检 early-exit、403/404/422 拆分消息、指纹锚定所见）；`SendIdempotentAsync` 幂等重试；`ApiGetAsync` 受控跟随 + DoH CDN 扩展；branch delete 披露。
- 消息：+7（PrReopenRejected / IssueCloseRejected / WorkflowDispatched / DispatchForbidden / DispatchNotFound / DispatchRejected / ApiRedirected），selftest 37 条 PASS（JIT+AOT）。
- 文档：plan-lite **v2.7**（§5 SLCV2 采用、§7 删披露行、§8.2 V1.6 增补、§9 三 op 行 + api 行 + 重试不变量、§14 L1 让步入册、--version 戳对齐）；本文。
- 版本：v0.3.0 / build 戳 2026-10-31 02:00UTC。

## 3. 遗留（非本批）

- 重放通道（§9.6）**零实弹**——首个「更新已有 PR 分支」需求时批准一次即完成验证。
- logs 数据体读取待 PAT 补 Actions:Read 后实测（跟随路径已 tarball 实测通过）。
- `--auth` 认证读的 PAT 权限指引（ApiAuthRequired 文案已够；setup 文档可补一句 Actions:Read 的用途说明）。
- 旁证入册：09:16/09:36 两次 `internal_gate_crash (TypeInitializationException)` 未定位（自愈，留观）；10:29/10:54 push timeout 与平台不稳窗吻合。

## 4. 遗留销账（2026-11-04）

- **重放通道：已实弹**——首笔 approved 重放 2026-10-31 03:33Z（`iss-358-359-360-362-linux@0bf3c586→c9ac530d`，本文次日）；至 2026-11-04 05:41Z 累计 124 条重放审计行（target 含 `→` 的 push）：approved 89 / up_to_date 8 / remote_moved 8 / timeout 12 / denied 4 / other 3——CLI 与 MCP 前门（via 字段）都在用。§9.6 通道验证闭环。
- **logs 数据体：实测通过（2026-11-04 05:49Z）**——`sluice api "repos/rustin-beep/Vitro/actions/jobs/126409293571/logs" --auth` → exit 0 / HTTP 200 / 85251B 日志体落 stdout / 2.7s；302→CDN 签名 URL 跟随 + DoH + issuer 校验全程无感，审计行 `http_200 · 85251B auth`。PAT 已带 Actions:Read（同窗匿名 runs 读仍 403 限流，对照成立）。实测二进制 `v0.7.0 · git 2ae969d5+dirty · plan-lite v2.24`。跟随路径全族（tarball/logs）实弹完毕。
- **Actions:Read 指引：已落地**——README PAT 矩阵含「`api --auth` 读 CI（runs/jobs/logs）→ Actions: Read」行。
- **gate 崩溃仍留观**：诊断保真已先行（97cd5ae DescribeCrash，异常链+堆栈随身进审计），根因未再现。
