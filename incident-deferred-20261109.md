# 发现时机事故复盘（deferred 降级 + ①'缺位）· 2026-11-09

> 证据源：`%LOCALAPPDATA%\Sluice\audit.jsonl` 的 **op=notice 行 83 条**（2026-11-03T14:03Z → 2026-11-09T04:42Z，
> v2.19 起每次 git 索要凭据自动落行）× vivarium v3-math-printf 会话窗写操作行（11-08 17:07–17:40Z）×
> 用户与当事 agent 的复盘对话（2026-11-09，agent 自述"按通用习惯直接走 git push → gh 标准路径，失败后
> 也没回头看工具清单，直到环境提示点出 sluice、用户点破 MCP 才加载 github_write"）。
> 方法：先审计后结论——notice 行还原直推尝试分布（时序/簇形），via 字段区分前门，不靠回忆。

## 0. 侧门尝试全景（op=notice 遥测，v2.19 的第一手信号兑现）

| 日期 | 11-03 | 11-04 | 11-05 | 11-06 | 11-07 | 11-08 | 11-09 | 合计 |
|---|---|---|---|---|---|---|---|---|
| 行数 | 25 | 15 | 3 | 4 | 17 | 16 | 3 | **83** |

- **簇状分布**：尝试集中在每个新会话开始后的几分钟内（首撞），此后同会话基本不再犯——发现成本是
  每会话重付的，不是一次性的。
- **零越权**：83 次尝试全部 fail-closed（层③无凭据 + 层②notice 指路），无一笔绕过闸门的写入。
- 语义边界：notice = "git 向 github.com 索要凭据"，含 push 与私有库 fetch/pull 两形，不全是 push 尝试；
  **gh 直连尝试不在其内**（gh 不走 git credential 链——见 §5 遗留）。

## 1. 典型簇时间线（2026-11-08 17:07–17:27Z，vivarium v3-math-printf 会话，UTC）

| 时刻 | 事件 | 证据 |
|---|---|---|
| 17:07:42 | notice #1（agent 直推 git） | audit op=notice |
| 17:08:12 | notice #2 | audit op=notice |
| 17:13:01 | notice #3 | audit op=notice |
| 17:13:32 | **首笔 sluice 调用**（CLI push，pending window:b82a24b9） | audit via=cli |
| 17:17:06 | push approved → executed（bcc5bf4a） | audit via=cli |
| 17:27:13 | 首笔 via=mcp（github_write push 窗口） | audit via=mcp |

**Δ 首撞 → 首笔 sluice 调用 ≈ 5 分 50 秒**，代价 = 3 次浪费的直推 + 用户介入点破。层②③按设计兜住，
层①①'双双缺位——这就是本事故的全部形状。

## 2. 定性（层糕语言）

- **agent 绕路不是事故，是 T1 基线**——层糕模型本就假设 agent 会先撞直推（§2 砍 shim 的理由原文），
  83 次尝试零越权恰是"封堵后 agent 写必失败"断言（§13）的生产侧持续验证。
- **事故 = ①层被宿主降级 + ①'层缺位**：
  - ① ZCode 的 **deferred 工具机制**把未加载工具的 schema 移出请求信封、只在 system-reminder 留裸名——
    §9.7 的核心价值主张（"schema 活在请求信封里、经 tool-choice 管线锚定"）在该形态下**静默不成立**，
    ①层退化为弱于①'的提示层（裸名连"必须且只能"的政策文本都没有）。
  - ①' vivarium 工作区 AGENTS.md **无规则块**——`--install-snippet` 是 V1.5 独立可选项，作者自己在最活跃
    的工作区也忘了跑（D:\Sluice 源码仓装了，docs 仓与 vivarium 都没装）。
- 复盘对话把防线误称"环境 hook"——实为 `sluice notice` 消息型 credential helper（§9），属 sluice 自己的
  管辖，文案与时机可自改（v2.33 已修化石语法，本批补 path 遥测）。

## 3. 当事 agent 复盘三条改进建议 · 逐行裁定

| # | 建议 | 裁定 | 依据 |
|---|---|---|---|
| 1 | 宿主向会话注入能力摘要（工具名+首行描述），deferred 也保一行 | **方向对，拆两档**：首行摘要买回"知道"，仍是提示层（一次性、可被压缩/lost-in-middle）；治本 = **server 可标记 policy-critical 工具不参与 deferred**（schema 留在信封，"锚定"恢复）；折中 = server 级一行摘要（每 server 一行而非每 tool 一行，context 成本可控） | §9.7 买的不是描述是信封内锚定 |
| 2 | 宿主前置拦截 hook（命令含 git push/gh/api.github.com 时审批前提示） | **成立**（ZCode 恰有 PreToolUse 事件），且补 notice 盖不到的 **gh/curl 面**；附赠价值 = 侧门尝试遥测统一（直推今天只有 git 面计数） | §9 原文"gh 无对应通道" |
| 3 | `sluice seal --fix` 往 shell rc 注入 git-push 包装提示函数 | **砍**，与 §2 被砍的凭据 shim 同族：提示版拦截点劣于 notice（rc 依赖交互 shell 加载，`sh -c`/全路径/沙箱全绕开；notice 卡在 git 索凭据的精确时刻）；转译版（自动改走 sluice）让"错误路径有时能走通"，违背"agent 一定失败→人来处理"立场，且重开 lull/PATH 抢占面 | plan-lite §2 砍单、§9 notice 选型段 |
| 旁 | （复盘自省）接到任务先扫工具清单再选路径 | 同意并入 agent 侧纪律；但系统设计必须假设首撞必发生——可预测的失败靠设计兜底，不靠个体记性 | 83 行簇状分布即证明 |

## 4. 处置落点（v2.34 / v0.7.9，全部 LANDED，零宿主依赖）

- **代码**：`setup --install` 扩为环境安装器（notice 成功后对 CWD git 工作区默认装 §8.4 片段，退出阀
  `--no-snippet`，非工作区跳过）；doctor 第七项（`ResolveAgentsVisibility` 四态巡检，CWD 向上至 .git
  边界，与 ZCode 工作区解析同窗口）；notice 审计 target 补 `path=`（`ParseCredentialMeta` 单源，
  username/password 一概不读）；setup 尾部清单两步改三步（补 sentinel-install）。
- **selftest**：发现时机锁（meta 提取 / 可见性四态 temp 树）。
- **实弹**（2026-11-09）：v0.7.9 rename-swap 发布（5 个存活 MCP server 锁 publish 文件——v2.18 纪律重演）；
  doctor 实弹**抓到 helper 路径漂移**（gitconfig 曾指向框架构建 exe ≠ 规范 publish exe，v2.23 前缀精确检查
  按设计红脸）→ 从规范 exe 重跑 `setup --install` 修复，全绿；notice 首行带 `target=rustin-beep/vivarium.git`
  落盘；D:\code\Sluice docs 工作区补装片段。
- **文档**：plan-lite **v2.34**（§8.4 默认装决定 / §9 notice path / §9.7 deferred 降级注记 / §12 V1.5 对账 /
  §13 A/B 探针加 deferred 第三臂 / §14 新 L13）；本文。

## 5. 遗留（非本批）

- **宿主（ZCode）两条治本 feature**（§3 裁定 #1/#2 的落地形态）：①server 级能力摘要或 policy-critical
  免 deferred 标记；②PreToolUse 命令内容 matcher（sluice 可代装配置进 `~/.zcode/cli/config.json`）。
  本批明确不做——不依赖宿主演进是处置的约束条件。
- **L13（新入册）**：gh 桥接条目写在无协议键 `credential.github.com.helper`（2026-11-03 只清了 `https://`
  键，漏网）；gh 直连尝试零遥测。当前检测网靠 hosts.yml（哨兵 ≤5min）仍闭合，封堵面待另批评审。
- **vivarium 工作区片段**：待用户在仓库根跑 `sluice setup --install-snippet`（活跃 PR 工作树，未擅动；
  装完记得随仓库提交——片段对 agent 的约束力来自项目可见性）。
- **A/B 探针第三臂**（deferred 形态）：生产侧数据已自行积累（本文 §0 即基线），正式受控跑仍待 §12。
