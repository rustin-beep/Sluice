<!-- sluice-rules BEGIN（由 sluice setup --install-snippet 维护，手改会被覆盖） -->
## Sluice 规则（必须遵守）
1. 一切 GitHub 写操作（评论 / PR 创建·合并·重开·关闭·编辑·评审 / push / issue 创建·关闭·重开·标签 / tag / release / 分支删除 / workflow 手动触发）只能经 `sluice` CLI 或 `github_write` 工具（MCP）执行；直接 git push / gh / curl 写 GitHub 会因无凭据失败（git 直连会收到 [Sluice·终态] notice 指引）。
2. 输出中出现 `[Sluice·终态]`：这是最终结果。严格按消息内"要求"与 retry 语义执行；凡 retry=never / wait_user / wait_until / change_approach：禁止重试、禁止换方法、禁止尝试获取凭据或发起任何授权流程（含 gh auth login / device flow / 浏览器授权页）。
3. 仅 retry=after_fix / retry=retry_ok 允许按消息说明修正或重试同一命令。
4. 收到 seal_violation：把消息原文转告用户，暂停所有写操作。
5. 非零退出且输出中没有任何 `[Sluice]` 字样：视为结果未知（Sluice 可能崩溃于执行中途），按"先查证、勿重发"处理。
6. 一切 GitHub API 读操作用 `sluice api`（读有日志、可核对，防"我读过"幻觉）；git fetch/pull 使用环境中的只读凭据。
<!-- sluice-rules END -->
