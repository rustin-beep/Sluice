using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Sluice;

/// <summary>
/// 纯 REST push（V1，依 2026-10-29 实测裁决：github.com 直连间歇失败 + Watt hosts-MITM，
/// git 子进程不可走；blobs→trees→commits→update-ref 全程 api.github.com，享受 DoH+issuer 双防线）。
/// 序列幂等：blob/tree/commit 均内容寻址，重复提交得同 sha；update-ref 是唯一写点
/// （FF/新建 force:false，服务端再拦一次非 FF；重放式更新通道 force:true，v2.6 §9.6）。
/// 不支持：裸 force/--force 参数（policy 硬拒）、merge 提交（单父增量模型）、submodule、>200 commits / >2000 blobs。
/// </summary>
internal static class RestPush
{
    public sealed class LocalPlanException(string msg) : Exception(msg);

    public sealed record Plan(string Repo, string Branch, string RepoPath, string HeadSha, string WindowText,
        bool TouchesWorkflows, int CommitCount, int BlobCount, string? Base = null,
        string? ReplacingSha = null); // ReplacingSha：重放入窗的 origin/{branch} 旧头（非 null 即重放模式，§9.6）

    public enum PushResult { Done, UpToDate, NeedFetch, NonFF, Submodule, HasMerge, Unauthorized, NotSent, Unclear, Other, HeadMoved, RemoteMoved, ReplayUnresolved, BaseMissing, TooLarge, BlobMismatch }

    public sealed record Result(PushResult Kind, string? NewSha = null, string? Reason = null);

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    // ---------- 本地规划（只读 git，无网络；弹窗展示用，执行时以远端为准重算） ----------

    public static Plan PlanLocal(string repo, string branch, string repoPath, string? baseRef = null)
    {
        var head = Git(repoPath, "rev-parse HEAD").Trim();
        var tracking = $"origin/{branch}";
        var hasTracking = GitExists(repoPath, tracking);
        string? baseSha = null;
        string? replacingSha = null; // 重放式更新（§9.6）：将被替换的 origin/{branch} 旧头；非 null 即重放模式
        if (baseRef is not null)
        {
            baseSha = Git(repoPath, $"rev-parse {baseRef}^{{commit}}").Trim();
            if (baseSha.Length < 40 || !GitIsAncestor(repoPath, baseSha, head))
            {
                // v2.10（2026-10-31 实战）：泛化提示逼 agent 自诊——两次踩的是同一个真实原因
                // （远端基点已前进而本地引用未更新），点名正解即可自救
                var fix = baseRef.StartsWith("origin/", StringComparison.Ordinal)
                    ? $"\n最常见原因：远端 {baseRef} 已前进而本地引用未更新。修正：git fetch origin {baseRef["origin/".Length..]} 后 git rebase {baseRef}，再以 --base {baseRef} 重跑同一命令（目标分支已存在于远端时将走重放式更新通道，弹窗明示替换与丢弃清单）"
                    : "";
                throw new LocalPlanException($"--base {baseRef} 不是 HEAD 的祖先提交，无法作为增量基点。{fix}");
            }
            if (hasTracking)
                // --base + 已有远端分支 = 显式请求重放（原地替换旧头，PR 不换分支；§9.6 闸门①）。
                // 裸 force（--force 参数）仍永禁——重放必须以"声明新基点"的形式表达
                replacingSha = Git(repoPath, $"rev-parse {tracking}^{{commit}}").Trim();
        }
        var replay = replacingSha is not null;
        var range = hasTracking
            ? baseSha is not null ? $"{baseSha}..HEAD" : $"{tracking}..HEAD" // 重放取新基点 / 常规 FF 取远端头
            : baseSha is not null ? $"{baseSha}..HEAD" : "HEAD";
        var diffBase = baseSha ?? (hasTracking ? tracking : EmptyTree);

        var commits = GitLines(repoPath, $"rev-list --reverse {range}");
        if (commits.Count == 0)
            throw new LocalPlanException(
                $"没有待推提交（{(replay || baseSha is not null ? $"{(replay ? "重放" : "新建分支")}范围 {baseSha![..8]}..HEAD 为空" : hasTracking ? $"origin/{branch}..HEAD 为空" : "当前分支无提交")}，HEAD={head[..8]}）");

        // 完整提交信息（批准你所见）：≤8 个提交逐个全文 message + 每文件增删统计；
        // 超出部分列标题（弹窗滚动解锁机制承载长文，§7）
        var fullCount = Math.Min(commits.Count, 8);
        var log = Git(repoPath, $"log --no-color --format=medium --stat -n {fullCount} {range}");
        if (commits.Count > fullCount)
            log += $"\n\n…其余 {commits.Count - fullCount} 个提交（仅标题）：\n"
                + Git(repoPath, $"log --no-color --format=\"%h %s\" --skip {fullCount} {range}");
        var stat = Git(repoPath, $"diff --stat --no-renames {diffBase} HEAD").Trim();
        var workflows = GitLines(repoPath, $"diff --name-only --no-renames {diffBase} HEAD")
            .Any(p => p.StartsWith(".github/workflows/", StringComparison.Ordinal));

        // v2.12（缺陷二处置）：全历史形态 = 规划期盲区——本地无 tracking 引用时无法区分"真新建分支"
        // 与"曾推过远端但 REST 不回写引用"。错误文案附 fetch 正解、弹窗加警示行；有意不 fail-closed：
        // FF 推送无销毁侧（闸门⑤ fail-closed 的理由在此不成立），且批准内容 ⊇ 执行内容（执行以远端现状收窄）
        string FhHint(string what) => !hasTracking && baseSha is null
            ? $"\n提示：本次因本地无 origin/{branch} 引用走了全历史回溯——若该分支曾推过远端（REST 推送不回写本地引用），git fetch origin {branch} 后重跑即改走增量，可绕开此{what}"
            : "";
        var assumeNewNote = !hasTracking && baseSha is null
            ? $"""
              ⚠ 本地无 origin/{branch} 引用，按「新建分支·全历史」规划——若该分支远端已存在（如曾用 sluice 推过，REST 不回写本地引用），先 git fetch origin {branch} 重跑可得精确增量；执行发现分支已存在时将以远端现状收窄并做 fast-forward 校验（不销毁任何远端提交）

              """
            : "";
        HashSet<string> blobs;
        try { blobs = CollectBlobs(repoPath, commits); }
        catch (LocalPlanException ex) { throw new LocalPlanException(ex.Message + FhHint("历史提交")); }

        // v2.12：超限前置到规划期（原只在执行期——批准已烧）；执行期同款检查保留为范围收窄后的兜底
        if (commits.Count > 200)
            throw new LocalPlanException($"提交数 {commits.Count} 超过 200 上限，请分批推送{FhHint("限制")}");
        if (blobs.Count > 2000)
            throw new LocalPlanException($"待传对象 {blobs.Count} 超过 2000 上限，请分批或精简{FhHint("限制")}");

        // 重放披露（§9.6 闸门③——"批准你所见"扩展到销毁侧）：所见必须覆盖将被替换/丢弃的部分。
        // 丢弃清单 = rev-list HEAD..origin/{branch}（远端有而新历史没有的提交），规划期算出入窗
        string replayNote = "";
        if (replay)
        {
            var dropped = GitLines(repoPath, $"rev-list {head}..{tracking}");
            var shown = dropped.Count == 0
                ? "无（远端旧头已是新历史的祖先，等价于 fast-forward）"
                : string.Join("；", GitLines(repoPath, $"log --no-color --format=\"%h %s\" -n 3 {head}..{tracking}"))
                  + (dropped.Count > 3 ? $"（共 {dropped.Count} 个）" : "");
            replayNote = $"""
                ⚠ 重放式更新（非 fast-forward）：origin/{branch} 旧头 {replacingSha![..8]} 将被替换为本地 HEAD {head[..8]}。
                丢弃的远端提交：{shown}

                """;
        }

        var window = $"""
            {assumeNewNote}{replayNote}提交数：{commits.Count}{(replay ? $"（重放，基点 {baseSha![..8]}..HEAD）" : hasTracking ? $"（origin/{branch}..HEAD）" : baseSha is not null ? $"（新建分支，基点 {baseSha[..8]}..HEAD）" : "（全历史，新建分支）")}
            待传对象：{blobs.Count} 个 blob

            提交：
            {log.Trim()}

            变更统计：
            {stat}

            （若目标仓库为空，将先自动创建 README.md 完成初始化）
            """;
        return new Plan(repo, branch, repoPath, head, window, workflows, commits.Count, blobs.Count, baseSha, replacingSha);
    }

    /// <summary>收集 commit 序列相对第一父引入的全部 blob（去重；跳过删除/未合并）。</summary>
    private static HashSet<string> CollectBlobs(string repoPath, List<string> commits)
    {
        var set = new HashSet<string>();
        foreach (var c in commits)
            foreach (var line in GitLines(repoPath, $"-c core.quotePath=false diff-tree -r --no-renames --raw {FirstParentOrEmpty(repoPath, c)} {c}"))
                ParseRaw(line, (_, _, sha, st, submodule) =>
                {
                    // submodule 判定先于 D/U 早退（2026-10-30 评审）：删除 submodule 的行也必须
                    // 在规划期暴露——否则批准后执行期才炸，违背"批准你所见"的 fail-closed 语义
                    if (submodule) throw new LocalPlanException($"REST push 不支持 submodule（在提交 {c[..8]} 引入或删除）");
                    if (st is 'D' or 'U') return;
                    set.Add(sha);
                });
        return set;
    }

    private static string FirstParentOrEmpty(string repoPath, string commit)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse --verify --quiet {commit}^")
        {
            WorkingDirectory = repoPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null || p.WaitForExit(5000) && p.ExitCode != 0) return EmptyTree;
        return p.StandardOutput.ReadToEnd().Trim();
    }

    // ---------- 执行（网络；批准 + Hello 解密后调用） ----------

    public static async Task<Result> ExecuteAsync(string token, Plan plan)
    {
        // 批准你所见（T5 残余闭合）：窗口文本来自 PlanLocal 快照；若弹窗期间 HEAD 被其它
        // 进程推进，实际推送内容将超出用户所见范围——fail-closed 拒推，重跑即重新走全流程。
        var headNow = Git(plan.RepoPath, "rev-parse HEAD").Trim();
        if (headNow != plan.HeadSha)
            return new Result(PushResult.HeadMoved,
                $"批准期间本地 HEAD 已前进（{plan.HeadSha[..8]} → {headNow[..8]}），弹窗展示的提交范围已过期。本次未推送，请重新运行 sluice push（将以新 HEAD 重新弹窗）");

        // 远端现状（读 API，免批准路径与 §9 api 同类）
        var (st, body) = await GitHubApi.SendRawAsync(HttpMethod.Get,
            $"repos/{plan.Repo}/git/ref/heads/{GitHubApi.RefPath(plan.Branch)}", token, null);
        if (st is 401 or 403) return new(PushResult.Unauthorized);
        if (st == 409)
        {
            // 空仓库:git data API 无法在空仓库创建对象(blobs/trees/commits 均 409
            // "Git Repository is empty")。用 Contents API 播种 README.md 初始化
            // (等价网页建库勾选 Add a README),然后重查目标分支。
            var seed = new JsonObject
            {
                ["message"] = "sluice: initialize repository",
                ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    "# Sluice\n\nGitHub 写操作事件闸门(Windows Hello 批准 · REST push · 审计)。\n设计文档见 plan-lite.md。\n")),
            };
            var (sst, sbody) = await GitHubApi.SendRawAsync(HttpMethod.Put,
                $"repos/{plan.Repo}/contents/README.md", token, seed.ToJsonString());
            if (sst is not (200 or 201)) return Net(sst, $"空仓库初始化(README) {Why(sst, sbody)}");
            (st, body) = await GitHubApi.SendRawAsync(HttpMethod.Get,
                $"repos/{plan.Repo}/git/ref/heads/{GitHubApi.RefPath(plan.Branch)}", token, null);
            if (st is 401 or 403) return new(PushResult.Unauthorized);
        }
        if (st == 409) { /* 播种后仍空(竞态)按 404 处理 */ st = 404; body = ""; }
        if (st != 200 && st != 404) return Net(st, $"查询远端分支 {Why(st, body)}");

        string remoteSha;
        bool isNew;
        if (st == 404) { remoteSha = ""; isNew = true; }
        else
        {
            remoteSha = JsonNode.Parse(body)?["object"]?["sha"]?.GetValue<string>() ?? "";
            if (remoteSha == plan.HeadSha) return new(PushResult.UpToDate);
            isNew = false;
        }

        // 重放前置缺口（v2.10，2026-10-31 实战定案）：--base 已声明 + 远端分支已存在，但规划期
        // 本地无 origin/{branch} 引用（ReplacingSha=null，弹窗按"新建分支"入窗）——丢弃清单 =
        // rev-list HEAD..origin/{branch} 需要本地对象，闸门③销毁侧披露构造不出来。fail-closed
        // 拒执行并点名 fetch；此处落 FF/NonFF 分流会开出"加 --base 重跑"这条已满足的死指令
        if (plan.Base is not null && !isNew && plan.ReplacingSha is null)
            return new(PushResult.ReplayUnresolved, Reason: "远端分支已存在但规划期无本地引用");

        // 重放式更新保真复核（§9.6 闸门④，pr_merge T5 同族手法）：规划期旧头入窗，执行前比对
        // 实况——批准到执行间远端头被推进（或分支被删）时，静默 force 会孤儿化窗口外的新提交。
        // fail-closed：fetch 后重跑即重新弹窗。残余窗口（GET→PATCH 之间）与 pr_merge 复核一致。
        if (plan.ReplacingSha is { Length: 40 or 64 } plannedOld)
        {
            if (isNew)
                return new(PushResult.RemoteMoved,
                    $"远端分支已不存在（规划期旧头 {plannedOld[..8]}），弹窗展示的替换对象已过期（分支在批准期间被删除）");
            if (remoteSha != plannedOld)
                return new(PushResult.RemoteMoved,
                    $"批准期间远端头已变化（规划期 {plannedOld[..8]} → 现在 {remoteSha[..8]}），弹窗展示的替换对象已过期");
        }
        var replay = !isNew && plan.ReplacingSha is not null;

        // v2.12（缺陷一处置）：基点/远端头在场校验 + base_tree 解析前移到任何上传之前——原位置在
        // blob 全部上传之后，基点缺失时白传全部 blob 才失败；前移后失败零上传零副作用。
        // 请求数量不变（该 GET 原本就有，只是来得晚）；执行期位置即权威终判（带 token，无私有不可辨问题）
        string prevCommit = replay ? plan.Base! : isNew ? plan.Base ?? "" : remoteSha;
        string prevTree = "";
        if (prevCommit.Length > 0)
        {
            var (gst, gbody) = await GitHubApi.SendRawAsync(HttpMethod.Get,
                $"repos/{plan.Repo}/git/commits/{prevCommit}", token, null);
            if (gst != 200)
            {
                if (plan.Base is not null && prevCommit == plan.Base)
                    return new(PushResult.BaseMissing,
                        $"--base 基点提交 {prevCommit[..8]} 不在远端（{Why(gst, gbody)}）——增量基点必须是远端已有对象（增量 base_tree 的锚）");
                // plain-FF：prevCommit=remoteSha 刚经 GET ref 取得，此处读不到属竞态/平台异常
                return new(PushResult.Other,
                    Reason: $"远端头提交 {prevCommit[..8]} 读取失败（{Why(gst, gbody)}）");
            }
            prevTree = JsonNode.Parse(gbody)?["tree"]?["sha"]?.GetValue<string>() ?? "";
        }

        // 快进校验（以本地对象库为准，fail-closed）；重放模式另行分流（闸门①：NonFF 判定不放宽，只分流；
        // v2.10：--base+远端已存在+无本地引用已被上方 ReplayUnresolved 前置拦截——本分流内 NonFF 必然未声明 --base）
        string range;
        if (replay)
        {
            // 锚点与范围用声明的新基点（与 isNew+--base 同构）。远端旧头对象无需存在于本地——
            // 丢弃清单已在规划期从本地 tracking 算出入窗，上方相等复核保证"丢弃的就是弹窗里那些"
            if (!GitIsAncestor(plan.RepoPath, plan.Base!, plan.HeadSha))
                return new(PushResult.Other, "--base 基点不是 HEAD 祖先（非增量），拒绝执行");
            range = $"{plan.Base}..HEAD";
        }
        else if (isNew)
        {
            // 新建分支：默认全历史；--base 给了增量基点则只推 base..HEAD（存量仓库新分支唯一可行形态）
            // 长度 40 或 64：SHA-1 与 SHA-256 仓库的对象名（sha-256 漏判会静默退化成全历史重放）
            if (plan.Base is { Length: 40 or 64 } b)
            {
                if (!GitIsAncestor(plan.RepoPath, b, plan.HeadSha))
                    return new(PushResult.Other, "--base 基点不是 HEAD 祖先（非增量），拒绝执行");
                range = $"{b}..HEAD";
            }
            else range = "HEAD";
        }
        else
        {
            if (!GitExists(plan.RepoPath, remoteSha))
                return new(PushResult.NeedFetch,
                    $"本地缺少远端对象 {remoteSha[..8]}（未同步过远端分支），无法验证 fast-forward；请先 git fetch 后重推");
            if (!GitIsAncestor(plan.RepoPath, remoteSha, plan.HeadSha))
                // Reason = 远端头 sha8（Program 拼进 PushNonFF 专属消息；2026-10-30 实战：
                // 非 ff 的正解是 rebase 后重推同一分支，agent 可自行修复，改归 after_fix 档）
                return new(PushResult.NonFF, remoteSha[..8]);
            range = $"{remoteSha}..HEAD";
        }

        var merges = GitLines(plan.RepoPath, $"rev-list --merges {range}");
        if (merges.Count > 0)
        {
            // 列出问题提交（B3：只报数量不说哪个，用户/agent 无从下手——实战绕三圈的根源之一）
            var shown = string.Join("；", merges.Take(3)
                .Select(m => Git(plan.RepoPath, $"log -1 --format=\"%h %s\" {m}").Trim()));
            return new(PushResult.HasMerge, $"{shown}（共 {merges.Count} 个）");
        }
        var commits = GitLines(plan.RepoPath, $"rev-list --reverse {range}");
        if (commits.Count > 200) return new(PushResult.TooLarge, Reason: $"提交数 {commits.Count} 超过 200 上限，请分批");

        var blobs = CollectBlobs(plan.RepoPath, commits);
        if (blobs.Count > 2000) return new(PushResult.TooLarge, Reason: $"待传对象 {blobs.Count} 超过 2000 上限");

        // blob 上传（内容寻址，幂等；服务器返回 sha 必与本地一致，不符即停；5xx 有界重试 v2.7）
        foreach (var sha in blobs)
        {
            var content = Convert.ToBase64String(GitBytes(plan.RepoPath, $"cat-file blob {sha}"));
            var blobBody = new JsonObject { ["content"] = content, ["encoding"] = "base64" };
            var (bst, bbody) = await GitHubApi.SendIdempotentAsync(HttpMethod.Post,
                $"repos/{plan.Repo}/git/blobs", token, blobBody.ToJsonString());
            if (bst is not (200 or 201)) return Net(bst, $"blob 上传 {Why(bst, bbody)}");
            var gotSha = JsonNode.Parse(bbody)?["sha"]?.GetValue<string>();
            if (gotSha != sha)
                // v2.12：内容寻址对象同内容必同 sha——不符属服务端异常窗（Vitro 4×blob-500 同族）；
                // 分支未动、blob 幂等，重传安全 → BlobMismatch（20/retry_ok）
                return new(PushResult.BlobMismatch, Reason: $"blob 上传返回 sha {gotSha?[..Math.Min(8, gotSha.Length)] ?? "?"} 与内容寻址不符（本地 {sha[..8]}）");
        }

        // 逐 commit：tree（base_tree 增量）+ commit（保真 author/committer/date）
        // 重放锚点 = 声明的新基点（不是远端旧头——旧头正是要被替换掉的）；
        // prevCommit/prevTree 已在前移段（任何上传之前）解析完毕
        var lastSha = prevCommit;

        foreach (var c in commits)
        {
            var parent = FirstParentOrEmpty(plan.RepoPath, c);
            var entries = new JsonArray();
            foreach (var line in GitLines(plan.RepoPath, $"-c core.quotePath=false diff-tree -r --no-renames --raw {parent} {c}"))
                ParseRaw(line, (path, mode, sha, st2, submodule) =>
                {
                    if (submodule) throw new LocalPlanException($"REST push 不支持 submodule：{path}");
                    // 显式 (JsonNode?) 走非泛型 Add——AOT 禁 JsonArray.Add<T> 的动态 JsonValue 创建。
                    // 删除条目（sha:null）也必须带合法 mode+type（GitHub create-tree 422
                    // "Must supply a valid tree.mode" 实锤 2026-10-30——删除行的 newmode 是
                    // 000000 不可用，ParseRaw 已折回 oldmode）。
                    entries.Add(st2 == 'D'
                        ? (JsonNode?)new JsonObject { ["path"] = path, ["mode"] = mode, ["type"] = "blob", ["sha"] = null }
                        : new JsonObject { ["path"] = path, ["mode"] = mode, ["type"] = "blob", ["sha"] = sha });
                });

            var treeSha = prevTree;
            if (entries.Count > 0)
            {
                var treeBody = new JsonObject { ["tree"] = entries };
                if (prevTree.Length > 0) treeBody["base_tree"] = prevTree;
                var (tst, tbody) = await GitHubApi.SendIdempotentAsync(HttpMethod.Post,
                    $"repos/{plan.Repo}/git/trees", token, treeBody.ToJsonString());
                if (tst is not (200 or 201)) return Net(tst, $"tree 创建 {Why(tst, tbody)}");
                treeSha = JsonNode.Parse(tbody)?["sha"]?.GetValue<string>() ?? "";
            }

            var meta = Git(plan.RepoPath, $"log -1 --format=%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B {c}");
            var f = meta.Split('\0');
            // git 规范:commit message 恰好以一个 \n 结尾(%B 附加了 log 分隔换行)。
            // 服务器 commit 对象须逐字节一致,否则 sha 与本地分叉,后续 FF 推送断链(实测教训 2026-10-30)。
            var commitBody = new JsonObject
            {
                ["message"] = f[6].TrimEnd('\n') + "\n",
                ["tree"] = treeSha,
                ["author"] = new JsonObject { ["name"] = f[0], ["email"] = f[1], ["date"] = f[2] },
                ["committer"] = new JsonObject { ["name"] = f[3], ["email"] = f[4], ["date"] = f[5] },
            };
            if (prevCommit.Length > 0)
            {
                var parents = new JsonArray();
                JsonNode? p = JsonValue.Create(prevCommit); // 显式 JsonNode → 非泛型 Add，AOT 安全
                parents.Add(p);
                commitBody["parents"] = parents;
            }
            var (cst, cbody) = await GitHubApi.SendIdempotentAsync(HttpMethod.Post,
                $"repos/{plan.Repo}/git/commits", token, commitBody.ToJsonString());
            if (cst != 201) return Net(cst, $"commit 创建 {Why(cst, cbody)}");
            lastSha = JsonNode.Parse(cbody)?["sha"]?.GetValue<string>() ?? "";
            prevCommit = lastSha;
            prevTree = treeSha;
        }

        // update-ref：唯一写点。FF/新建 force:false（服务端再拦一次非 FF）；重放通道 force:true
        // （§9.6：前置条件已在上方全部复核——--base 显式声明 + 非主干 + 弹窗明示替换与丢弃清单 + 远端头复核；
        // 服务端分支保护若禁 force 是第三道纵深防御，422/403 透传服务端 message，分支未动重试安全）
        var (rst, rbody) = isNew
            ? await GitHubApi.SendRawAsync(HttpMethod.Post, $"repos/{plan.Repo}/git/refs", token,
                new JsonObject { ["ref"] = $"refs/heads/{plan.Branch}", ["sha"] = lastSha }.ToJsonString())
            : await GitHubApi.SendRawAsync(HttpMethod.Patch, $"repos/{plan.Repo}/git/refs/heads/{GitHubApi.RefPath(plan.Branch)}", token,
                new JsonObject { ["sha"] = lastSha, ["force"] = replay }.ToJsonString());
        if (rst is not (200 or 201)) return Net(rst, $"update-ref {Why(rst, rbody)}");
        return new(PushResult.Done, NewSha: lastSha);
    }

    private static Result Net(int status, string detail) => status >= 500
        ? new Result(PushResult.Unclear, Reason: detail)   // 序列中断：分支未动，重试安全，按 unknown 诚实分类
        : new Result(PushResult.Other, Reason: detail);

    /// <summary>HTTP 状态 + GitHub 响应 message（权限类错误直接显示服务端原话）。</summary>
    private static string Why(int st, string body)
    {
        try
        {
            var m = JsonNode.Parse(body)?["message"]?.GetValue<string>();
            return m is { Length: > 0 } msg ? $"HTTP {st}：{msg}" : $"HTTP {st}：{Trunc(body)}";
        }
        catch { return $"HTTP {st}"; }
    }

    private static string Trunc(string s) => s.Length > 200 ? s[..200] : s;

    /// <summary>
    /// 解析 `:oldmode newmode oldsha newsha STATUS\tpath`（--no-renames，无 R/C 形态）。
    /// 路径保真依赖调用方带 `-c core.quotePath=false`：默认 C 引号转义会把非 ASCII 路径
    /// 连引号一起发往服务端，tree sha 与本地分叉（P1，2026-10-30 探针定案）。
    /// 残留：`"` 与 `\` 开头的路径在 quotePath=false 下仍被转义，只有 `-z` 才彻底——
    /// NTFS 建不出这类名字，仅 Linux 作者的提交可带入，完整修复（-z 重写）列 V1 红队前。
    /// </summary>
    private static void ParseRaw(string line, Action<string, string, string, char, bool> fn)
    {
        if (line.Length < 2 || line[0] != ':') return;
        var parts = line[1..].Split(' ', 5);
        if (parts.Length < 5) return;
        var tab = parts[4].IndexOf('\t');
        if (tab < 1) return;
        // mode 折回：删除行（D）的 newmode=000000 非法，服务端要求合法 mode——用 oldmode
        var st2 = parts[4][..tab][^1];
        var mode = st2 == 'D' ? parts[0] : parts[1];
        // submodule 判定看两侧 mode（2026-10-30 评审修复）：删除行 ":160000 000000 … D" 的
        // newmode=000000，只看 parts[1] 会漏判——折回 oldmode=160000 生成
        // {"mode":"160000","type":"blob","sha":null} 非法条目，create-tree 照样 422
        var submodule = parts[0] == "160000" || parts[1] == "160000";
        fn(parts[4][(tab + 1)..], mode, parts[3], st2, submodule);
    }

    // ---------- 本地 git 封装（全部只读） ----------

    private static string Git(string repoPath, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new LocalPlanException("无法启动 git");
        var outp = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15_000)) throw new LocalPlanException($"git 超时：{args}");
        if (p.ExitCode != 0)
            throw new LocalPlanException($"git 失败（{Trunc(args)}）：{p.StandardError.ReadToEnd().Trim()}");
        return outp;
    }

    private static List<string> GitLines(string repoPath, string args) =>
        Git(repoPath, args).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static byte[] GitBytes(string repoPath, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi) ?? throw new LocalPlanException("无法启动 git");
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        if (!p.WaitForExit(30_000)) throw new LocalPlanException($"git 超时：{args}");
        return ms.ToArray();
    }

    private static bool GitExists(string repoPath, string rev)
    {
        var psi = new ProcessStartInfo("git", $"cat-file -e {rev} --")
        {
            WorkingDirectory = repoPath, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        return p is not null && p.WaitForExit(5000) && p.ExitCode == 0;
    }

    private static bool GitIsAncestor(string repoPath, string ancestor, string descendant)
    {
        var psi = new ProcessStartInfo("git", $"merge-base --is-ancestor {ancestor} {descendant}")
        {
            WorkingDirectory = repoPath, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        return p is not null && p.WaitForExit(5000) && p.ExitCode == 0;
    }
}
