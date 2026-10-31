# Sluice — GitHub 写操作事件闸门

一个 Windows 单文件 CLI（.NET 10 NativeAOT，~19MB，零第三方运行时依赖）：把 agent/人的一切
GitHub **写**操作收进一扇「批准窗 + Windows Hello（可选）+ 审计」的闸门，同时把环境里的
旁门凭据封堵干净——**没有批准事件，就没有写动作**。

```
agent ──sluice <verb>──▶ 批准窗（全文所见 + 注入输入过滤 + 倒计时）
                          └─批准▶ Hello 指纹（或静默 vault 点击即批）▶ vault 解 PAT ▶ REST 执行 ▶ audit.jsonl
旁路 ──git push / gh / curl──▶ seal 封堵（无凭据可用）+ notice 指引回到 sluice
```

三条设计底线（详文 plan-lite.md）：

1. **批准你所见**——弹窗展示将被发布的全文/替换对象/丢弃清单，长文必须滚到底才亮批准键；
2. **能力缺席优于拦截**——fine-grained PAT 只授权指定仓库，本地配置不是墙，服务端 403 才是；
3. **读有日志、写有审计**——`sluice api` 读全量落审计，写操作逐条落 `%LOCALAPPDATA%\Sluice\audit.jsonl`。

---

## 安装与初始化

前置：Windows 10/11 x64；GitHub **fine-grained PAT**（不是 classic）。

```
dotnet publish -c Release -r win-x64     # 产物在 bin/Release/net10.0-windows10.0.26100/win-x64/publish/
sluice.exe setup                          # 粘贴 PAT → 校验 → Windows Hello 建 vault
sluice.exe seal --fix                     # 封堵侧门：凭据管理器 / gh / git-credentials / SSH / 环境变量
sluice.exe selftest                       # 消息表与 exit code 自检
sluice.exe --version                      # 构建戳（时间 + git sha[+dirty] + plan-lite 版本）——排障先看这个
```

### PAT 权限矩阵（fine-grained，按用到的动词开）

| 动词 | Repository permissions |
|---|---|
| `comment` / `issue create` / `issue close` / `issue reopen` | **Issues: Read and write** |
| `push` / `tag create` / `release create` | **Contents: Read and write**（tag 走 git/refs、release 走 releases 端点，同 Contents 面） |
| `pr create` / `pr reopen` / `pr close` / `pr edit` / `review` | **Pull requests: Read and write** |
| `pr merge` | **Pull requests: Read and write** + **Contents: Read and write**（merge 端点要向 base 写 commit；`--delete-head` 删 ref 同走 Contents——按上行最小开权的 PAT 会在 merge 上 403） |
| `workflow dispatch` | **Actions: Read and write** |
| `api --auth` 读 CI（runs/jobs/logs） | **Actions: Read**（actions 端点对匿名一律 403） |
| 所有 | Metadata: Read（强制自带） |

两个高频坑：权限下拉**默认 Read-only**，改完必须滚到底点绿色 **Update token** 保存；
仓库必须在 token 的 Repository access 列表里——否则服务端 403（fail-closed，属设计内行为）。

## 命令速查

写操作全部经过批准窗；示例中 `O/R` = owner/repo。

| 命令 | 作用 | 备注 |
|---|---|---|
| `sluice comment --repo O/R --number N --body-file F` | 在 issue/PR 下评论 | 正文全文入窗 |
| `sluice push --repo O/R --branch B [--repo-path D] [--base REF]` | 纯 REST 推送本地 HEAD | 新分支给 `--base origin/master` 做增量基点；**已有分支 + `--base` = 重放式更新**（原地替换旧头，弹窗明示丢弃清单）；`--force` 与主干推送永禁；多请求序列，调用方超时给 ≥300s |
| `sluice pr create --repo O/R --base B --head H --title T --body-file F` | 建 PR | |
| `sluice pr merge --repo O/R --number N [--method merge\|squash\|rebase] [--delete-head]` | 合并 PR | 服务端 merge，天然尊重分支保护与 required checks |
| `sluice pr reopen --repo O/R --number N` | 重开已关 PR | `reopened` 会再触发 pull_request 类 CI——CI 卡死时的首选补救 |
| `sluice pr close --repo O/R --number N` | 关闭 PR | pr reopen 的对偶件；讨论保留、可随时重开 |
| `sluice pr edit --repo O/R --number N [--title T] [--body-file F]` | 勘误 PR 标题/描述 | 弹窗披露旧值→新值（写覆盖纪律）；`--title`/`--body-file` 至少一项 |
| `sluice review --repo O/R --number N --event COMMENT\|APPROVE\|REQUEST_CHANGES [--body-file F]` | 在 PR 上落评审 | commit_id 锚定「所见即所评」；APPROVE 不能批自己账户的 PR（服务端 422） |
| `sluice issue close --repo O/R --number N` | 关闭 issue | PR 编号会被预检拦下 |
| `sluice issue create --repo O/R --title T --body-file F` | 新建 issue | issue close 的对偶件（立案闭环）；正文全文入窗 |
| `sluice issue reopen --repo O/R --number N` | 重开已关 issue | issue close 的对偶件；PR 编号同样会被预检拦下 |
| `sluice tag create --repo O/R --tag V --at <分支\|完整sha>` | 新建远端轻量 tag | create-or-fail 不覆盖；私有库 --at 解析失败时给完整 40 位 sha |
| `sluice release create --repo O/R --tag V --body-file F [--target <分支\|sha>] [--name N] [--prerelease]` | 发布 release | target 以批准期解析的 sha 锚定（杜绝隐式建 tag 的 TOCTOU）；发布即通知 watchers |
| `sluice workflow dispatch --repo O/R --workflow W --ref B [--inputs-file F]` | 手动触发 workflow | W=id 或 .yml 文件名；F=JSON 对象对应 inputs；需 PAT Actions:RW |
| `sluice branch delete --repo O/R --branch B` | 删远端分支 ref | 弹窗会披露「将连带关闭其上的开放 PR」；commits 保留可重建 |
| `sluice api <api-path> [--auth]` | GET 只读 | 公开数据匿名零交互；`--auth` 走 vault；logs 等端点 302→CDN 自动跟随（仅 GitHub 域 ≤3 跳）；每次调用入读审计 |
| `sluice seal [--fix]` / `sluice selftest` / `sluice --version` | 自查 / 自检 / 版本戳 | |

推送序列的 5xx（blob/tree/commit，内容寻址幂等）自动退避重试（2s/8s×3）；update-ref 永不自动重试。
步骤被服务端拒绝会**原样透传服务端 message**（exit 10，分支未动）；`--base` 基点不在远端在**一切上传之前**拦截（exit 10，零副作用）；blob sha 与内容寻址不符归 20（平台异常，重传安全）。

## config.json（`%LOCALAPPDATA%\Sluice\config.json`）

| 键 | 默认 | 语义 |
|---|---|---|
| `"hello": false` | `true` | **静默 vault（SLCV2/DPAPI）**：批准点击即批准事件，不再弹 Windows Hello；`--auth` 读零交互。旧 vault 在下次批准时自动迁移（= 最后一次指纹）。**回滚须改回 `true` 并重跑 `setup`**（只改配置不恢复指纹）。降级语义：同用户进程可静默解密，见 plan-lite §14 L1 |
| `"gate_timeout_sec"` | 60 | 批准窗倒计时，夹 [30,600] |
| `"proxy"` | 无 | 显式非 MITM 代理隧道（如 `http://127.0.0.1:7890`） |

网络防线内置：api.github.com 走 DoH 直连（系统 hosts 失效，与本地加速器共存）+ 证书 issuer 白名单（DigiCert/Sectigo/Microsoft TLS）。

## 出错怎么办（exit code 速查）

每条 `[Sluice·终态]` 消息自带事实句 + `retry` 语义，单读可判，按消息行事即可：

| code | 含义 | retry |
|---|---|---|
| 0 | 成功（消息含结果 URL，无需再验证） | — |
| 2 | 用户拒绝 | never（含四条禁令：勿重试/勿换路/勿取凭据/勿授权流程） |
| 3 | 批准窗超时自动拒绝 | wait_user |
| 4 | 策略永禁（--force / 主干推送） | change_approach |
| 6 | Hello 未通过 | wait_user |
| 7 | 侧门违规（先 `seal --fix`） | wait_user |
| 8 | 未初始化 / vault 损坏 | wait_user |
| 9 | 凭据失效/越权 | wait_user（用户重跑 setup） |
| 10 | 用法错/服务端拒（多数带正解指引） | after_fix |
| 20 | 连接失败（请求未发出）/ api GET 超时 | retry_ok（30s 间隔） |
| 21 | **结果未知**（请求已发出） | wait_user（先查证勿盲重发） |

## 更多

- 设计与不变量：[plan-lite.md](plan-lite.md)（§8 消息规范 / §9 op 白名单 / §9.6 重放通道 / §14 已知缺口）
- 实战复盘：[incident-vitro-20261030.md](incident-vitro-20261030.md)（CI 零触发根因、迁移 bug 等）
- V0 实现记录：[V0-IMPLEMENTATION.md](V0-IMPLEMENTATION.md)
