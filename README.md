# Sluice — GitHub 写操作事件闸门

一个 Windows / Linux 单文件 CLI（.NET 10 NativeAOT，~19–25MB，零第三方运行时依赖）：把 agent/人的一切
GitHub **写**操作收进一扇「批准窗 + 同意因子（Windows Hello / Linux 口令，可选降级）+ 审计」的闸门，
同时把环境里的旁门凭据封堵干净——**没有批准事件，就没有写动作**。

```
agent ──sluice <verb> / MCP github_write──▶ 批准窗（全文所见 + 注入输入过滤 + 倒计时）
                          └─批准▶ 同意因子（Hello 指纹 / Linux 口令；静默 vault 点击即批）▶ vault 解 PAT ▶ REST 执行 ▶ audit.jsonl
旁路 ──git push / gh / curl──▶ seal 封堵（无凭据可用）+ notice 指引回到 sluice
```

三条设计底线（详文 plan-lite.md）：

1. **批准你所见**——弹窗展示将被发布的全文/替换对象/丢弃清单，长文必须滚到底才亮批准键；
2. **能力缺席优于拦截**——fine-grained PAT 只授权指定仓库，本地配置不是墙，服务端 403 才是；
3. **读有日志、写有审计**——`sluice api` / `sluice issue list` 读全量落审计，写操作逐条落审计文件（Windows `%LOCALAPPDATA%\Sluice\audit.jsonl`；Linux `~/.local/share/Sluice/audit.jsonl`）。

## 适用场景：先判断你需不需要

两种极端都不需要本项目：

| 你的用法 | 为什么不需要 |
|---|---|
| **逐个审阅**——agent 每步都过你的眼睛 | 你本人就是闸门，再叠一扇批准窗是纯摩擦 |
| **纯本地开发**——agent 不碰 GitHub 写操作 | 没有写面可门控 |
| **范围 token 放养**——把白名单仓库 + 最小权限 + 短有效期的 fine-grained PAT 直接交给 agent，范围内它随便写 | 爆炸半径已被服务端封顶：范围外一律 403（本地配置不是墙，服务端 403 才是） |
| **独立账号**——agent 挂牺牲账号，或跑在独立 OS 账户里 | 损失以账号为界，事后审计即可 |

本项目的位置在中间：**主力账号 + 主力机器上长期放手使用 agent**——你自己还要用 gh/git（全权凭据必须留在环境里），又要 agent 高频真实写入，又不想逐条盯。那么：环境凭据封堵掉（agent 直推必失败），写入只走闸门——每笔一个批准事件 + 一行审计，放手放到「批/不批」为止。

与「范围 token」的本质区别：范围 token 封顶的是**写到哪**（范围内无人值守照样写）；本项目门控的是**写不写**——vault 里同样是一枚范围 PAT，但 agent 拿不到，且范围内每一笔也要过批准窗。

---

## 快速上手

**支持平台**：Windows 10/11 x64 + **Linux x64（v0.8.0 落地并 WSL 实弹）**——同源 multi-target，平台差异收口在叶：同意因子 Windows=Hello / Linux=批准窗口令（T1.5 等价：agent 点得动按钮打不出口令）；降级档 `config hello:false`（Windows=DPAPI 静默 / Linux=0600 文件，点击即批）；哨兵 Windows=schtasks+toast / Linux=systemd 用户定时器+notify-send。macOS 不在目标内（plan-lite §1.5）。

前置：.NET 10 SDK；Windows 另需 MSVC C++ 生成工具、Linux 另需 `clang zlib1g-dev libicu-dev` 及 X11 系运行库（`libx11-6 libxkbcommon-x11-0 libgbm1 libegl1 libfontconfig1`）；GitHub **fine-grained PAT**（不是 classic）。暂无预编译下载件，从源码构建：

```
dotnet publish -c Release -f net10.0-windows10.0.26100 -r win-x64   # Windows → …/win-x64/publish/sluice.exe
dotnet publish -c Release -f net10.0 -r linux-x64                   # Linux/WSL → …/linux-x64/publish/sluice（AOT ~24.5MB，启动 ~16ms）
```

Linux 批准窗需图形会话（X11/Wayland；WSL 用 WSLg）。哨兵需 systemd（WSL 在 `/etc/wsl.conf` 加 `[boot]` `systemd=true` 后 `wsl --shutdown` 重启）。

初始化五步 + 验证（每步做什么见注释）：

```
sluice setup [--token-file F]             # ① 配置凭据（交互粘贴或 --token-file 读文件）→ 校验 → 建 vault
                                             （Windows 弹 Hello；Linux 设口令——此后每次批准在窗内输入，口令即解密因子）
sluice seal --fix                         # ② 封堵侧门：凭据存储 / gh / git-credentials / netrc / SSH / 环境变量 / repo-local helper 清查
                                             + 置 GCM 无 UI 封堵键（credential.https://github.com.interactive=never——错门毫秒级死不弹登录窗）
sluice setup --install                    # ③ 装 notice helper + 默认写规则片段进工作区 AGENTS.md（--no-snippet 跳过）
sluice sentinel-install                   # ④ 哨兵：每 5 分钟 seal 预检，违规弹通知（Windows 计划任务+toast / Linux systemd 定时器+notify-send；
                                             封 device flow"当场成功"窗）
sluice setup --install-hook [all]         # ⑤ 宿主 PreToolUse hook（ZCode/Claude Code）：agent 直推 git/gh 在执行前被 deny，
                                             秒回指路文案——首错代价从"登录窗+挂起"降为"一条消息"；幂等，exe 路径漂移自动刷新
sluice doctor                             # 验证：八项只读健康检查（版本戳/vault/config/seal 预检/notice/GCM 封堵键/哨兵/AGENTS 片段）全绿即就绪
```

（Windows 下二进制名 `sluice.exe`，Linux 为 `sluice`；下同。）

**GCM 登录窗被封堵后，人要交互登录怎么办**（人的逃生口，只写进人文档——agent 可见文案刻意不教绕过）：
单次放行 `git -c credential.https://github.com.interactive=always <命令>`；当前 shell 放行 `export GCM_INTERACTIVE=always`；
或开暂停窗 `sluice seal --pause N`（期内封堵键与 notice 一并摘除，可正常交互登录，到期自动恢复）。

日常运维（按需）：

```
sluice seal --pause N / --resume      # 暂停封堵 N 分钟（批准窗门控，期内直推开放）/ 立即恢复
sluice unseal                         # 卸载环境侧安装件（notice/哨兵/SSH 块；善后清单打印）
sluice setup --install-snippet        # 单独装/刷新 AGENTS.md 规则片段（标记块幂等，重跑=刷新，手改会被覆盖）
sluice selftest / --version           # 消息表自检 / 构建戳（时间 + sha[+dirty] + plan-lite 版本）——排障先看版本
```

**首次写操作会发生什么**：任何写动词（如 `sluice comment …`）弹出批准窗——展示将发布的全文，长文滚到底才解锁批准键，倒计时内 Enter / Esc / 关窗均按拒绝处理；批准再过同意因子（Windows=Hello 指纹；Linux=窗内输入 vault 口令；静默降级 vault 则桌面点击即批），随后 REST 执行并在 `audit.jsonl` 落一行。读操作（`sluice api` / `sluice issue list`）零交互、不弹窗。被拒时消息自带事实句与 retry 语义（见文末 exit 表）。

### PAT 权限矩阵（fine-grained，按用到的动词开）

| 动词 | Repository permissions |
|---|---|
| `comment` / `comment edit` / `issue create` / `issue close` / `issue reopen` / `issue label` | **Issues: Read and write** |
| `push` / `tag create` / `release create` | **Contents: Read and write**（tag 走 git/refs、release 走 releases 端点，同 Contents 面） |
| `pr create` / `pr reopen` / `pr close` / `pr edit` / `review` | **Pull requests: Read and write** |
| `pr merge` | **Pull requests: Read and write** + **Contents: Read and write**（merge 端点要向 base 写 commit；`--delete-head` 删 ref 同走 Contents——按上行最小开权的 PAT 会在 merge 上 403） |
| `workflow dispatch` | **Actions: Read and write** |
| `api --auth` 读 CI（runs/jobs/logs） | **Actions: Read**（actions 端点对匿名一律 403） |
| `issue list --auth`（私有库） | **Issues: Read**（只读清单面；开过上面 Issues:RW 写动词的 PAT 已覆盖。公开库连白名单外的都可直接读——Metadata: Read 实测即覆盖 issues/actions 端点） |
| 所有 | Metadata: Read（强制自带） |

两个高频坑：权限下拉**默认 Read-only**，改完必须滚到底点绿色 **Update token** 保存；
仓库必须在 token 的 Repository access 列表里——否则服务端 403（fail-closed，属设计内行为）。

## 命令速查

写操作全部经过批准窗；示例中 `O/R` = owner/repo。

| 命令 | 作用 | 备注 |
|---|---|---|
| `sluice comment --repo O/R --number N --body-file F` | 在 issue/PR 下评论 | 正文全文入窗 |
| `sluice comment edit --repo O/R --comment-id ID --body-file F` | 修改已发表的评论 | 评论 id = 评论链接 `#issuecomment-<id>` 的数字部分；弹窗披露旧值→新值（写覆盖纪律）；只能改 token 所属账户自己的评论（GitHub 只允许作者改评） |
| `sluice push --repo O/R --branch B [--repo-path D] [--base REF]` | 纯 REST 推送本地 HEAD | 新分支给 `--base origin/master` 做增量基点；**已有分支 + `--base` = 重放式更新**（原地替换旧头，弹窗明示丢弃清单）；`--force` 与主干推送永禁；多请求序列，调用方超时给 ≥300s；成功后 best-effort 回写本地 `refs/remotes/origin/<B>`（此前 REST 推送不回写，本地引用结构性陈旧） |
| `sluice pr create --repo O/R --base B --head H --title T --body-file F` | 建 PR | |
| `sluice pr merge --repo O/R --number N [--method merge\|squash\|rebase] [--delete-head]` | 合并 PR | 服务端 merge，天然尊重分支保护与 required checks |
| `sluice pr reopen --repo O/R --number N` | 重开已关 PR | `reopened` 会再触发 pull_request 类 CI——CI 卡死时的首选补救 |
| `sluice pr close --repo O/R --number N` | 关闭 PR | pr reopen 的对偶件；讨论保留、可随时重开 |
| `sluice pr edit --repo O/R --number N [--title T] [--body-file F]` | 勘误 PR 标题/描述 | 弹窗披露旧值→新值（写覆盖纪律）；`--title`/`--body-file` 至少一项 |
| `sluice review --repo O/R --number N --event COMMENT\|APPROVE\|REQUEST_CHANGES [--body-file F]` | 在 PR 上落评审 | commit_id 锚定「所见即所评」；APPROVE 不能批自己账户的 PR（服务端 422） |
| `sluice issue close --repo O/R --number N` | 关闭 issue | PR 编号会被预检拦下 |
| `sluice issue create --repo O/R --title T --body-file F [--labels a,b,c]` | 新建 issue | issue close 的对偶件（立案闭环）；正文全文入窗；`--labels` 单请求携带标签，未知标签名预检拦下 |
| `sluice issue reopen --repo O/R --number N` | 重开已关 issue | issue close 的对偶件；PR 编号同样会被预检拦下 |
| `sluice issue label --repo O/R --number N --labels a,b,c` | 设置标签（整组替换） | 弹窗披露当前标签→新标签→**摘除清单**；PR 编号同样有效（端点共用）；未知标签名预检拦下；清空全部标签本批不支持 |
| `sluice issue list --repo O/R [--state open\|closed\|all] [--labels a,b] [--search 关键词] [--limit N] [--page N] [--with-prs] [--auth]` | issue 清单/搜索（只读） | 一行一条（`#N [状态] 标题 {标签}`）；`--labels` 过滤、`--search` 关键词搜（空格分隔 AND，走 GitHub 搜索端点）；清单端点混入的 PR 默认隐去计数披露（`--with-prs` 显示）；`--limit` 1–100 缺省 30；`--search` 与 `--state all` 互斥（搜索端点只有 open/closed）；只读零交互，读落审计 |
| `sluice tag create --repo O/R --tag V --at <分支\|完整sha>` | 新建远端轻量 tag | create-or-fail 不覆盖；私有库 --at 解析失败时给完整 40 位 sha |
| `sluice release create --repo O/R --tag V --body-file F [--target <分支\|sha>] [--name N] [--prerelease]` | 发布 release | target 以批准期解析的 sha 锚定（杜绝隐式建 tag 的 TOCTOU）；发布即通知 watchers |
| `sluice workflow dispatch --repo O/R --workflow W --ref B [--inputs-file F]` | 手动触发 workflow | W=id 或 .yml 文件名；F=JSON 对象对应 inputs；需 PAT Actions:RW |
| `sluice branch delete --repo O/R --branch B` | 删远端分支 ref | 弹窗会披露「将连带关闭其上的开放 PR」；commits 保留可重建 |
| `sluice api <api-path> [--auth]` | GET 只读 | 公开数据匿名零交互；匿名撞 401/403/404/429 且静默 vault（hello:false）时自动带凭据复读一次（独立配额 5000/h，公开库几乎总能读通）；`--auth` 首请求即带凭据；logs 等端点 302→CDN 自动跟随（仅 GitHub 域 ≤3 跳）；每次调用入读审计 |
| `sluice status <receipt>` | 查 MCP pending 请求的终态 | `github_write` 返回 code=23 时附 receipt（16 位）；拒绝理由原文在此查询；无副作用可间隔重查，终态出现前勿重发同一写操作 |
| `sluice mcp [--print-config]` | MCP 前门（stdio JSON-RPC） | 单一写工具 `github_write`（action 与上表写动词一一对应）+ `sluice_status`（按 receipt 查终态）；spawn-child 拓扑——server 进程不持凭据，每次调用 spawn CLI 子进程走完整闸门；`--print-config` 打印宿主接入配置 |
| `sluice seal [--fix]` / `sluice setup --install` / `sluice sentinel-install` / `sluice selftest` / `sluice --version` | 自查 / 装 notice helper / 装哨兵 / 自检 / 版本戳 | notice 只作用 github.com（其他 host 的 helper 不受影响），卸载 `git config --global --unset-all credential.https://github.com.helper`；每次 git 索凭据触发都落审计（op=notice）；哨兵移除——Windows `schtasks /Delete /TN "Sluice Sentinel" /F`，Linux `systemctl --user disable --now sluice-sentinel.timer` |
| `sluice setup --install-hook [zcode\|claude-code\|all]` / `--uninstall-hook` / `sluice hook-guard` | 宿主 PreToolUse hook 装/卸 / 宿主内部调用 | hook 在**执行前** deny 直推 git/gh 写形态（fail-open 糖，封堵墙仍是 seal/notice/gate）；**暂停窗内仍拦**（agent 直推会撞恢复交互的 GCM 重演挂起；人交互登录走自己终端不经宿主）；deny 落审计（op=hook_guard）；宿主会话启动时读配置——装完对新会话生效；幂等 + exe 路径漂移刷新；unseal 善后清单含此项 |

推送序列的 5xx（blob/tree/commit，内容寻址幂等）自动退避重试（2s/8s×3）；update-ref 永不自动重试。
步骤被服务端拒绝会**原样透传服务端 message**（exit 10，分支未动）；`--base` 基点不在远端在**一切上传之前**拦截（exit 10，零副作用）；blob sha 与内容寻址不符归 20（平台异常，重传安全）。

## MCP 前门（agent 宿主接入）

除 CLI 外，写操作可经 MCP 工具进入同一闸门：宿主配置 `sluice mcp`（`sluice mcp --print-config`
打印现成接入 JSON——ZCode：设置→MCP 或 `~/.zcode/cli/config.json`；Claude Code：`.mcp.json` /
`~/.claude.json`）。两个工具：

- **`github_write`**——action 值域与上表写动词一一对应，一次调用恰一个写操作；参数与 CLI 同名
  （正文类参数走临时文件桥接 `--body-file`，用后即删）；返回 `structuredContent = {ok, code, retry, url?}`
  ——用户决策族（拒绝/超时等）`isError=false` 且 `deny_reason` 随身携带，工具没坏、是闸门说了不。
- **`sluice_status`**——按 receipt 查 pending 请求终态（等价 CLI `sluice status <receipt>`）。

**pending 语义**：宿主客户端 ~30s 弃等工具调用，短于人类读窗+选预设+填理由的 40–90s——server 等
`mcp_pending_sec`（默认 15s）仍未决即回 **exit 23 + receipt**（非终态，wait_until）：约 15s 后查终态；
终态出现前重发同一写操作会被闸门去重拦截并并入原回执（不另开新批准窗）。审计行携带 via=cli/mcp 区分前门。

## 引导 agent 使用（现实预期）

**推荐接入 = AGENTS.md 片段 + MCP，不做 skill。** MCP 与 skill 本质都只是把能力「暴露」给 agent——
调用权在 agent 手里，人家不调用，你没有任何手段强制；宿主的 deferred 机制还可能把工具 schema 移出
请求信封（agent 连「有这个工具」都未必知道）。skill 更弱：描述不命中就不触发。实测收敛最快的组合是
两件——工作区 AGENTS.md 规则片段（`setup --install` 默认写入，每会话开篇即读）+ 宿主加载 MCP。

**异物感是结构性的，不是你配置错了。** 正常情况下 agent 优先走 `git push` / `gh` / GitHub API 通用
路径（实测首撞集中在每个新会话开始的几分钟，代价 ≈ 3 次浪费的直推 + 5–6 分钟；同会话之后基本不再犯，
但发现成本是每会话重付的）。封堵层保证这些尝试**全部失败、零越权**；notice 指引覆盖 git 面，
**拦不到 gh 直连**（gh 不走 git credential 链）。

**人工介入点——弹出 gh / GitHub 登录时，手动停止该登录进程。** agent 诱导出的登录流程（浏览器授权页 /
登录窗 / device flow）不会自行结束，而 agent 正阻塞等待这条命令返回——不停止，它会一直卡住。手动停止
几次后，agent 才会在失败输出与 sluice 留下的提示里找到路，转用 `sluice` / `github_write`。

**定性：目前与可预见的将来都无法根治。** 能力只能暴露、不能强制调用，宿主也不提供工具优先级机制；
而「自动把 git push 改写走 sluice」= 让错误路径偶尔能走通，属被砍的 shim 一族，不做（见 plan-lite §2）。
封堵负责「绕不过」，发现靠片段 + 失败信息 + 你偶尔出手关掉登录窗。

## config.json（Windows `%LOCALAPPDATA%\Sluice\config.json`；Linux `~/.local/share/Sluice/config.json`）

| 键 | 默认 | 语义 |
|---|---|---|
| `"hello": false` | `true` | **静默 vault**：批准点击即批准事件，不再要求同意因子（Windows=SLCV2/DPAPI，Linux=SLCF1/0600 文件）；`--auth` 读零交互。旧 vault 在下次批准时自动迁移（= 最后一次指纹/口令）。**回滚须改回 `true` 并重跑 `setup`**（只改配置不恢复到场因子）。降级语义：同用户进程可静默读取 vault，见 plan-lite §14 L1 |
| `"gate_timeout_sec"` | 60 | 批准窗倒计时，夹 [30,600] |
| `"mcp_pending_sec"` | 15 | MCP 前门未决等待上限，夹 [5,55]——必须小于宿主工具超时（实测 ~30s），到点回 exit 23 + receipt |
| `"proxy"` | 无 | 显式非 MITM 代理隧道（如 `http://127.0.0.1:7890`） |

网络防线内置：api.github.com 走 DoH 直连（系统 hosts 失效，与本地加速器共存）+ 证书 issuer 白名单（DigiCert/Sectigo/Microsoft TLS）。

## 出错怎么办（exit code 速查）

每条 `[Sluice·终态]` 消息自带事实句 + `retry` 语义，单读可判，按消息行事即可（唯一例外：23 为非终态，消息前缀 `[Sluice]`）：

| code | 含义 | retry |
|---|---|---|
| 0 | 成功（消息含结果 URL，无需再验证） | — |
| 1 | sluice 内部错误（消息自带构建戳排障） | wait_user |
| 2 | 用户拒绝 | never（含四条禁令：勿重试/勿换路/勿取凭据/勿授权流程） |
| 3 | 批准窗超时自动拒绝 | wait_user |
| 4 | 策略永禁（--force / 主干推送） | change_approach |
| 5 | 冷却中（拒绝后递增延迟）/ CLI 同操作未决重发被去重拦截（无新窗，等原请求终态） | wait_until |
| 6 | Hello 未通过 | wait_user |
| 7 | 侧门违规（先 `seal --fix`） | wait_user |
| 8 | 未初始化 / vault 损坏 | wait_user |
| 9 | 凭据失效/越权 | wait_user（用户重跑 setup） |
| 10 | 用法错/服务端拒（多数带正解指引） | after_fix |
| 20 | 连接失败（请求未发出）/ api GET 超时 | retry_ok（30s 间隔） |
| 21 | **结果未知**（请求已发出） | wait_user（先查证勿盲重发） |
| 22 | GitHub 限流（服务端等待分钟数透传，"本次操作未执行"） | wait_until（按窗口等待后重跑） |
| 23 | **MCP pending**（批准窗未决即回，非终态；附 receipt） | wait_until——约 15s 后 `sluice status <receipt>` / `sluice_status` 查终态；终态前重发会被去重并入原回执 |

## 更多

- 设计与不变量：[plan-lite.md](plan-lite.md)（§8 消息规范 / §9 op 白名单 / §9.6 重放通道 / §14 已知缺口）
- 实战复盘：[incident-vitro-20261030.md](incident-vitro-20261030.md)（CI 零触发根因、迁移 bug 等）
- 发现时机复盘：[incident-deferred-20261109.md](incident-deferred-20261109.md)（侧门直推遥测 83 条、AGENTS 片段层与 MCP pending 的动机链）
- V0 实现记录：[V0-IMPLEMENTATION.md](V0-IMPLEMENTATION.md)
