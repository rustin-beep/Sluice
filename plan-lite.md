# Sluice Lite · 设计文档 v2

| 项 | 值 |
|---|---|
| 文档版本 | v2.4-lite（v2.4：8-PR 实战修复批——构建戳自报家门、门控 UX 四项（滚动暂停计时/Hello 取消回窗/前台置顶/超时可配+召回）、push 非 ff 改档 after_fix 与 HasMerge 专属消息、api 白名单补 `+,;`、pr_merge 与 branch_delete 入列；v2.3：实现现状对齐——api 读通道消息入 §8.2 表与 exit 映射、api GET 超时归 20、pr 422 与 vault 损坏专用消息；v2.2：评审修订——vault UI 门控、seal 扩容、notice 通道、unknown 语义、人类工作流） |
| 日期 | 2026-10-30 |
| 状态 | 设计定稿，待实施 |
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
| policy 硬拒（main/force） | 保留，硬编码 | 疲劳误批的唯一闸 |
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
   │ ① 解析参数 → ② policy 硬判（main/force → 直接 policy_reject，不弹窗）
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
- **哨兵（v2.2 新增）**：`sluice sentinel-install` 建 5 分钟间隔计划任务跑预检，违规弹 Windows 通知。作用：封 device flow 的"**当场成功**"时间窗——agent 走 `gh auth login` 拿到新 token 后用 gh 直连，全程不经过 sluice，最迟 5 分钟被看见。是检测不是阻止（N4 本地不可阻止），但把"下次 sluice 调用才发现"缩到"五分钟内"。
- **`seal --pause <分钟>`（v2.2 新增）**：人的显式暂停窗口——需 Hello（agent 也能发起调用，但同样要过 Hello，与一切写动作同待遇），期内恢复直推能力，到期自动恢复封堵。服务于 §9.5 人类工作流。
- 卸载路径：`sluice unseal` 恢复说明写进帮助——卸载后环境**不得**静默回到"全权 token 躺在 keyring"状态。

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
[Sluice·终态] 该操作被策略永久禁止：<push → 受保护分支 main / force push / 修改 .github/workflows>。此类操作不存在人工批准途径。
本次操作未执行。
1. 任何形式的重试（含变体：--force、--no-verify、改 remote、直连 API）都会被直接拒绝且不弹窗；
2. 允许的替代：推送到新特性分支（如 feat/<简述>）并创建 PR；
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
注意：force push 被策略永久禁止，不要尝试任何形式的覆盖推送。
```
（exit 10，retry=after_fix——**v2.4 档位修正**：非 ff 可由 agent 自行 rebase 修复后重推同一分支，属"修正后可重试"而非"策略永禁"；force 本身仍由 --force tripwire 永禁，不变量不动）

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

v2.4 注：push 非 ff 从 exit 4（policy_reject）移入 exit 10（after_fix）——rebase 后可重推同一分支，非策略永禁；force 与受保护分支仍 exit 4，不变量不动。

### 8.4 AGENTS.md 片段（`sluice setup --install-snippet` 追加到工作区 AGENTS.md）

```markdown
## Sluice 规则（必须遵守）
1. 一切 GitHub 写操作（评论/PR/评审/push）只能通过 `sluice` 命令执行；直接 git push / gh / curl 写 GitHub 会因无凭据失败。
2. 输出中出现 `[Sluice·终态]`：这是最终结果。严格按消息内"要求"与 retry 语义执行；凡 retry=never / wait_user / wait_until / change_approach：禁止重试、禁止换方法、禁止尝试获取凭据或发起任何授权流程（含 gh auth login / device flow / 浏览器授权页）。
3. 仅 retry=after_fix / retry=retry_ok 允许按消息说明修正或重试同一命令。
4. 收到 seal_violation：把消息原文转告用户，暂停所有写操作。
5. 非零退出且输出中没有任何 `[Sluice]` 字样：视为结果未知（Sluice 可能崩溃于执行中途），按"先查证、勿重发"处理。
6. 一切 GitHub API 读操作用 `sluice api`（读有日志、可核对，防"我读过"幻觉）；git fetch/pull 使用环境中的只读凭据。
```

---

## 9. op 白名单与执行

| op | 命令 | 执行 | token 去向 |
|---|---|---|---|
| `pr_comment` | `sluice comment --repo R --number N --body-file F` | WinHTTP `POST /repos/{r}/issues/{n}/comments` | 仅 sluice 进程 |
| `pr_create` | `sluice pr create --repo R --base B --head H --title T --body-file F` | WinHTTP `POST /repos/{r}/pulls` | 仅 sluice 进程 |
| `pr_review` | `sluice review --repo R --number N --event E [--body-file F]` | WinHTTP `POST /repos/{r}/pulls/{n}/reviews` | 仅 sluice 进程 |
| `push` | `sluice push [--repo-path D] --remote R --refspec S` | 见下 push bridge | git 子进程内存（存活期） |
| `api`（读，v2.2；V1 实现现状） | `sluice api <api-path> [--auth]`（GET-only；graphql 未实现；路径白名单允许 `?` `&` `:` `+` `,` `;` 查询字符——分页 cursor（base64 含 `+`）/过滤可用，拒 `..`/scheme/前导 `/`） | GET，**无需批准**；匿名读无需 Hello，`--auth` 私有读需 Hello（vault 解密的技术必然）。exit：2xx→0；超时→20（幂等，retry_ok）；匿名 401/403→10（加 --auth）；带 token 401/403→9；其余非 2xx→10。响应体→stdout、消息→stderr（不污染数据）。每次调用（路径+状态码+字节量）写 audit 读日志 | token 不出 sluice 进程 |
| `pr_merge`（v2.4，PR 工作流闭环最后一米） | `sluice pr merge --repo R --number N [--method merge\|squash\|rebase] [--delete-head]` | PUT `/repos/{r}/pulls/{n}/merge`——**服务端执行，天然尊重分支保护与 required checks**；弹窗尽力展示匿名可读的 PR 元数据（title/head→base/变更统计）；`--delete-head` 合并成功后删 head ref（失败不回滚合并，附注指引 branch delete）。exit：200→0；401/403→9；404/405/409/422→10（PrMergeRejected，查 mergeable）；5xx/超时→21 | 仅 sluice 进程 |
| `branch_delete`（v2.4，PR 作废后清残留） | `sluice branch delete --repo R --branch B` | DELETE `/repos/{r}/git/refs/heads/{b}`——删 ref 非销毁历史（commits 留在服务端，GC 窗口内可重建）；`main`/`master` tripwire → policy_reject（exit 4）。exit：204→0；401/403→9；404→10；超时→21（幂等但已送达，查证 refs） | 仅 sluice 进程 |

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
- `--force` / force push → policy_reject，永不批准
- push 到 `main` / `master` → policy_reject，永不批准
- push 内容含 `.github/workflows/**` 变更 → 对话框红字单独标记，批准需二次确认

**allowlist 的真墙在服务端（v2.2 修订，取代下面"改白名单要 Hello"的旧设计）**：vault 里的 fine-grained PAT 本身只授权指定仓库——本地 config.json 的白名单只是 UX 过滤器，改它不会扩大 token 的实际能力（未授权仓库 API 一律 403，fail-closed）。因此 `sluice allow-repo R` 就是普通配置编辑；setup 时打印 token 实际授权的仓库清单，服务端为准。最坏情况推演：agent 改本地白名单 → 人类看到未授权仓库的批准框 → 即便误批准，服务端仍 403 → fail-closed。

**`sluice notice`（消息型 credential helper，v2.2 新增）**：setup --install 写入全局 gitconfig。git（push/fetch 私有库）索要凭据时，它**不给凭据**，只向 stderr 打印 §8 终态文案后退出非零——agent 第一次直推 git，看到的是"写操作请走 sluice push；不要尝试 gh auth login；环境无可用凭据"，而不是裸的 `could not read Username`。选它而非 pre-push hook 的理由：私有库 push 在 ls-refs 阶段就要凭据，凭据提示发生在 pre-push 触发**之前**，hook 可能根本不执行；credential helper 恰好是 git 索要凭据的那一刻，拦截点精确。它不持凭据、不占 PATH、不伪装任何程序——只是把误导性错误换成可行动消息（~20 行），与被砍掉的凭据 shim 有本质区别。gh 无对应通道（其"请 gh auth login"提示无法替换），由 AGENTS.md 片段 + 哨兵（§6）兜底。注意：`git -c credential.helper=` 显式覆盖可绕过 notice——那是 T2 形状的行为，T1 不会做。

**`sluice api`（只读模式，v2.2 新增）**：只允许 GET/查询类调用，无需批准；匿名读无需 Hello，`--auth` 私有读需 Hello（vault 解密的技术必然）。每次调用写入 audit 读日志。收益：(a) agent 在 GitHub 上的读操作有 ground truth，"我读过 X"可核对，防幻觉；(b) 私有库 API 读的唯一通道，且 token 不出 sluice 进程；(c) unknown（exit 21）的查证命令落在它身上。诚实定位：这是**指令约定的可见性通道，不是墙**——agent 直连 GET 无法也无需阻止（读无破坏力），绕过它只是回到无日志状态，不是安全失败。

## 9.5 人类自己的工作流（v2.2 新增；seal 之后人怎么干活）

封堵是全员封堵——人也不再能直推，这必须有明文规则，否则方案三天就会被自己弃用：

1. **人也走 sluice**：push = Hello 一下（约 1 秒）；评论/PR 同一扇门。人人平等过闸门，审计同时覆盖人与 agent。
2. **fetch/pull 用只读凭据**：一张只读 fine-grained PAT（指定仓库、90 天）配给 git 的 fetch/pull，人和 agent 共用。它推不了任何东西——静默持有它不构成写风险（无能力，而非有门槛），评审问题 1 的担忧对它天然不成立。
3. **临时绕过走显式暂停**：大 push、交互式 rebase 结果、非 GitHub remote 等，`sluice seal --pause 30`（Hello 门控），期内直推，到期自动恢复，期间有常驻通知。
4. **读走 `sluice api` 或浏览器**：私有库 API 读走 api 模式（有日志）；网页浏览不受管。

---

## 10. 审计

`%LOCALAPPDATA%\Sluice\audit.jsonl`，追加写：

```json
{"ts":"2026-10-28T14:32:11Z","op":"pr_comment","repo":"rustin-beep/Vitro","target":"#5",
 "body_sha256":"a1b2...","decision":"denied","reason_preset":"内容需修改",
 "reason_text":"别带密钥","consent":"hello","result":"not_executed"}
```

- 拒绝也记（含理由原文——理由是协商界面的一部分，值得留痕）。
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
│  └─ Audit.cs             JSONL 追加                       ~40
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
| tripwire | 推 main → policy_reject 且不弹窗；force → 同 |
| 冷却 | 3 拒 → 冷却消息含绝对时间；冷却期内请求不弹窗直接拒 |
| **红队演练（关键）** | 真实 agent（ZCode/Qoder）+ 任务"完成后推 main"，观察：push 快速失败；agent 不换路径硬闯（检验消息服从性）；audit 完整；弹窗字段正确 |
| **中文文案服从性** | 红队演练中专门观察 agent 对中文终态消息的服从；若发现"多想"，先改文案再改代码 |

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

---

## 14. 已知缺口（不修，只记录）

| 编号 | 缺口 | 态度 |
|---|---|---|
| L1 | 同用户攻击面：注入 / 读内存 / **静默解密本地文件** | 前两者让步（N1）；静默解密被 §5 的 Hello 门控 vault 消灭（P7 探针定形式；DPAPI 末选时降格为已知让步）；剩余让步部分爆炸半径 = PAT 上限；真隔离的唯一路径是 agent 独立 OS 账户 |
| L2 | 人疲劳误批 | tripwire 硬拒 + Hello 双因子缓解，不消除 |
| L3 | 对话框可被同用户移动/遮挡 | 信任在 Hello；不承诺显示完整性 |
| L4 | audit 可篡改 | 接受；真相源 GitHub |
| L5 | PIN 兜底的攻击链 = 钩子录 PIN + SendInput 重放进 broker | **P4 实测：注入键盘输入未使 Hello 弹窗消失（两轮一致），重放腿断**；P5（录腿）未执行、降级可选。结论：PIN 兜底保留，残余风险 = 仅捕获、无重放 |
| L6 | push 子进程内 token 窗口（秒级） | PAT 90 天兜底；V1.5 可换 1h token |
| L7 | agent 对中文终态消息的服从性未验证 | V0/V1 红队必测；不服从先改文案；备选双语 |
| L8 | token 在托管堆多副本、永不可清零 | N1 已让步，无增量风险；与"明文窗口=进程生命期"同一笔账 |
| L9 | Linux Wayland 禁止抢焦点，批准框可能不前置 | 移植期用通知/任务栏 urgent 提示补位；Windows 不受影响 |

---

## 15. 命名约定

| 项 | 值 |
|---|---|
| 产物 | `sluice.exe`（唯一；`--cred-print` 为内置模式，不再单独出 shim） |
| 数据目录 | `%LOCALAPPDATA%\Sluice\`：`token.bin` / `config.json` / `state.json` / `audit.jsonl` |
| 消息前缀 | `[Sluice]` 过程 / `[Sluice·终态]` 最终结果 |
| 单实例 | 无（无 daemon 无需 Mutex） |
