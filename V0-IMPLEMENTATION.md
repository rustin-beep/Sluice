# Sluice V0 实施记录

日期：2026-10-29 · 状态：V0 代码完成，自动化验收全过，人工验收项待用户执行

## 交付清单（§12 V0 · 闸门闭环）

| 文件 | 职责 | 验证 |
|---|---|---|
| `Sluice.csproj` | net10.0-windows10.0.26100 + Avalonia 12.1.4 + NativeAOT | ✅ 发布成功（exe 16MB + 原生依赖 ~19MB），启动 ~90ms |
| `src/Program.cs` | CLI 分派、comment 主流程（参数→初始化→seal→弹窗→Hello→执行→审计） | ✅ 各终态 exit code 实测 |
| `src/Messages.cs` | §8.2 全部消息逐字落地 + exit code 表（唯一文案源） | ✅ selftest 穷举 PASS |
| `src/TokenStore.cs` | vault：master key = HKDF-SHA256(HelloSign(challenge), salt, "sluice-vault-v1")，AES-GCM 包封 | 编码完成；解密路径待 setup 后人工验证 |
| `src/Consent.cs` | KeyCredential 签名叶；批准即解密（§5 单生物事件） | 同上（探针 P7/P7c 已验证机制） |
| `src/GitHubApi.cs` | POST comment；证书 issuer 校验（非 DigiCert 拒发）；三分类（未发出/结果未知/明确） | issuer 校验逻辑在位；真实请求待 setup |
| `src/SealCheck.cs` | 预检 6 项（凭据管理器/gh hosts.yml/.git-credentials/env/SSH/gitconfig）+ --fix | ✅ 实机检出 6 条凭据 |
| `src/GateWindow.axaml(.cs)` | 批准框：倒计时 60s、默认焦点拒绝、Enter/Esc=拒绝、长文滚到底解锁批准、拒绝理由预设+附言 | 窗口逻辑待人工验证 |
| `src/InputFilter.cs` | LL 钩子丢弃 injected 输入（独立 STA 泵线程，可卸载） | 机制探针已验证；gate 集成待人工 |
| `src/Audit.cs` | audit.jsonl 追加（Utf8JsonWriter，AOT 安全） | ✅ 实测落行 |
| `src/Selftest.cs` | 消息表穷举 / exit code 对齐（§8.3 硬编码期望） | ✅ PASS |

## 实测发现与计划偏差

0. **（2026-10-29 修复 ×4）setup→全流程链路四个 bug，全部实测通过**：
   a. `粘贴 PAT:` 无换行 `Console.Write` 在 conhost 不刷新显示 → 改 WriteLine + 逐字符掩码读（`*` 回显）+ `--token-file` 备选。
   b. **issuer 白名单写错（真 bug，曾误导向 Watt）**：白名单只写 DigiCert，但 GitHub 已换 CA——实测真链路 issuer = `Sectigo Public Server Authentication CA DV E36`。修正为 `DigiCert + Sectigo` 集合，拒绝消息打印**实际 issuer**。
   c. **漏写 App.axaml**：GateApp 无配对 XAML，弹窗线程启动即崩（`No precompiled XAML found`，JIT/AOT 皆崩，此前测试未走过窗口路径）。补 axaml；gate 线程异常收敛为 `Crashed` outcome → 干净的 internal 终态。
   d. **fine-grained PAT 权限清单缺口（plan §3 沿袭）**：pr_comment 需要 `Issues: Read and write`，setup 文案与 §3 均漏列。文案已补；实测勾 Read-only 仍 403，改 Read and write 后 201。
   **全流程已闭环**：comment → 弹窗 → Hello → vault 解密（P7 确定性签名实战首通）→ HTTP 201 → 结果 URL（issuecomment-6501174862）→ audit `executed`。拒绝闭环（exit 2 + audit 理由）与 credential_expired 分类（403）顺带实测。
1. **§11.5「cmdkey /list 解析」裁决被实测推翻**：中文 Windows 的 cmdkey 输出是本地化文本（"目标:"），正则解析在非英文系统必坏。已改 `CredEnumerateW` / `CredDeleteW` API——目标名本就是 Unicode，一并消除 OEM 码页编码问题。
2. **网络形态结论（2026-10-29 夜补充）：sluice 现与 Watt 劫持共存**——`SocketsHttpHandler.ConnectCallback` + IP 直连 DoH（223.5.5.5 阿里 / 120.53.53.53 腾讯，证书含 IP SAN）：api.github.com 一律由 DoH 解析真实 IP 直连，**系统 hosts 对 sluice 失效**（Watt 的 hosts+本地443 MITM 加速照常工作，token 永不经过本地终结点；恶意改 hosts 劫持 token 的攻击面在解析层消灭）。issuer 校验（Sectigo/DigiCert）保留兜底；DoH 全失败退回系统解析并由 issuer 拦截报错。setup 打印 `[net] api.github.com → <IP>（DoH 解析）` 诊断行（仅人运行路径，不进 §8 消息流）。可选 `config.json {"proxy":"http://127.0.0.1:7890"}` 显式走非 MITM 代理隧道。实证：DoH 返回 20.205.243.168（GitHub Singapore）+ 假 token 得 GitHub 服务端 401。
2. **实机凭据比 10-28 清点多 2 条**：`git:https://249558894@github.com`、`gh:github.com:rustin-beep`（环境有新增凭据，seal 预检照单全收——检测面有效）。
3. **hosts.yml 未报警是正确行为**：本机 hosts.yml 存在但无 `oauth_token`（gh 登出残留配置），不构成可用凭据。
4. Avalonia 12 落地细节：`<ColumnDefinitions>` 简写不被 XAML 编译器接受（须 `<Grid.ColumnDefinitions>`）；`DispatcherTimer` 无 (TimeSpan, Priority) 构造；`TextBox.Watermark` → `PlaceholderText`。
5. `PublishAot=true` 与显式 `PublishTrimmed=false` 冲突（AOT 隐含 trimming）。
6. **selftest 断言以 §8.2 逐字原文为准**：§8.1 原则 3"每条终态消息必含'本次操作未执行'"与 §8.2 原文有张力（not_initialized / credential_expired / bad_request / http_failed 各有自己的事实句变体，unknown / internal-后段语义是"结果未知"不得写"未执行"）——按原文实现，断言逐条对应。

## 自动化验证结果

- `dotnet build`：0 警告 0 错误；NativeAOT publish 成功。
- `sluice selftest`（JIT 与 AOT 双跑）：PASS，14 条消息 × exit code × retry 语义对齐。
- `sluice seal`（只读）：正确列出 6 条凭据管理器违规。
- seal_violation 路径：假 vault + 违规环境 → 不弹窗、终态消息、exit 7、audit 落行。
- not_initialized：exit 8；bad_request（缺参/未知动词/坏 repo）：exit 10。

## 剩余人工验收（V0 验收表未闭合项）

1. **`sluice setup`**：需要真实 fine-grained PAT；验证 Hello 建 vault、加密落盘、`GET /user` 校验。
2. **`sluice seal --fix`**：将删除 6 条凭据管理器条目（含 VS 集成与 GCM 的 git 凭据，删后需重新登录）、改备份份 hosts.yml；执行与否由用户决定。
3. **全流程**：`sluice comment …` → 弹窗（布局/倒计时/滚动解锁/理由区）→ Hello → GitHub 可见评论 → 成功消息含 URL。
4. **拒绝闭环**：预设+附言拒绝 → 消息含理由与 retry 语义 → GitHub 无变更。
5. **T1.5 双拦**：`probes/inject.ps1` 适配 sluice 窗口（按 PID+矩形定位）后 SendInput 点批准 → 被 LL 钩子丢弃、Hello 不可注入。
