using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Sluice;

/// <summary>
/// 纯 REST push（V1，依 2026-10-29 实测裁决：github.com 直连间歇失败 + Watt hosts-MITM，
/// git 子进程不可走；blobs→trees→commits→update-ref 全程 api.github.com，享受 DoH+issuer 双防线）。
/// 序列幂等：blob/tree/commit 均内容寻址，重复提交得同 sha；update-ref force:false 是唯一写点。
/// 不支持：force（policy 硬拒）、merge 提交（单父增量模型）、submodule、>200 commits / >2000 blobs。
/// </summary>
internal static class RestPush
{
    public sealed class LocalPlanException(string msg) : Exception(msg);

    public sealed record Plan(string Repo, string Branch, string RepoPath, string HeadSha, string WindowText,
        bool TouchesWorkflows, int CommitCount, int BlobCount, string? Base = null);

    public enum PushResult { Done, UpToDate, NeedFetch, NonFF, Submodule, HasMerge, Unauthorized, NotSent, Unclear, Other, HeadMoved }

    public sealed record Result(PushResult Kind, string? NewSha = null, string? Reason = null);

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    // ---------- 本地规划（只读 git，无网络；弹窗展示用，执行时以远端为准重算） ----------

    public static Plan PlanLocal(string repo, string branch, string repoPath, string? baseRef = null)
    {
        var head = Git(repoPath, "rev-parse HEAD").Trim();
        var tracking = $"origin/{branch}";
        var hasTracking = GitExists(repoPath, tracking);
        if (baseRef is not null && hasTracking)
            throw new LocalPlanException(
                $"已存在 origin/{branch}（远端分支已知），无需 --base；--base 仅用于远端不存在的分支指定增量基点");
        string? baseSha = null;
        if (baseRef is not null)
        {
            baseSha = Git(repoPath, $"rev-parse {baseRef}^{{commit}}").Trim();
            if (baseSha.Length < 40 || !GitIsAncestor(repoPath, baseSha, head))
                throw new LocalPlanException($"--base {baseRef} 不是 HEAD 的祖先提交，无法作为增量基点");
        }
        var range = hasTracking ? $"{tracking}..HEAD" : baseSha is not null ? $"{baseSha}..HEAD" : "HEAD";
        var diffBase = hasTracking ? tracking : baseSha ?? EmptyTree;

        var commits = GitLines(repoPath, $"rev-list --reverse {range}");
        if (commits.Count == 0)
            throw new LocalPlanException($"没有待推提交（{(hasTracking ? $"origin/{branch}..HEAD 为空" : "当前分支无提交")}，HEAD={head[..8]}）");

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
        var blobs = CollectBlobs(repoPath, commits);

        var window = $"""
            提交数：{commits.Count}{(hasTracking ? $"（origin/{branch}..HEAD）" : baseSha is not null ? $"（新建分支，基点 {baseSha[..8]}..HEAD）" : "（全历史，新建分支）")}
            待传对象：{blobs.Count} 个 blob

            提交：
            {log.Trim()}

            变更统计：
            {stat}

            （若目标仓库为空，将先自动创建 README.md 完成初始化）
            """;
        return new Plan(repo, branch, repoPath, head, window, workflows, commits.Count, blobs.Count, baseSha);
    }

    /// <summary>收集 commit 序列相对第一父引入的全部 blob（去重；跳过删除/未合并）。</summary>
    private static HashSet<string> CollectBlobs(string repoPath, List<string> commits)
    {
        var set = new HashSet<string>();
        foreach (var c in commits)
            foreach (var line in GitLines(repoPath, $"-c core.quotePath=false diff-tree -r --no-renames --raw {FirstParentOrEmpty(repoPath, c)} {c}"))
                ParseRaw(line, (_, _, sha, st, submodule) =>
                {
                    if (st is 'D' or 'U') return;
                    if (submodule) throw new LocalPlanException($"REST push 不支持 submodule（在提交 {c[..8]} 引入）");
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
            $"repos/{plan.Repo}/git/ref/heads/{plan.Branch}", token, null);
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
                $"repos/{plan.Repo}/git/ref/heads/{plan.Branch}", token, null);
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

        // 快进校验（以本地对象库为准，fail-closed）
        string range;
        if (isNew)
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
                return new(PushResult.NonFF, "远端分支包含本地没有的提交（非 fast-forward）；force push 被策略永久禁止");
            range = $"{remoteSha}..HEAD";
        }

        if (GitLines(plan.RepoPath, $"rev-list --merges {range}").Count > 0)
            return new(PushResult.HasMerge, "推送范围含 merge 提交（多父），REST push 暂不支持；请用完整 git（经非 MITM 代理）推送或 rebase 成线性历史");
        var commits = GitLines(plan.RepoPath, $"rev-list --reverse {range}");
        if (commits.Count > 200) return new(PushResult.Other, Reason: $"提交数 {commits.Count} 超过 200 上限，请分批");

        var blobs = CollectBlobs(plan.RepoPath, commits);
        if (blobs.Count > 2000) return new(PushResult.Other, Reason: $"待传对象 {blobs.Count} 超过 2000 上限");

        // blob 上传（内容寻址，幂等；服务器返回 sha 必与本地一致，不符即停）
        foreach (var sha in blobs)
        {
            var content = Convert.ToBase64String(GitBytes(plan.RepoPath, $"cat-file blob {sha}"));
            var blobBody = new JsonObject { ["content"] = content, ["encoding"] = "base64" };
            var (bst, bbody) = await GitHubApi.SendRawAsync(HttpMethod.Post,
                $"repos/{plan.Repo}/git/blobs", token, blobBody.ToJsonString());
            if (bst is not (200 or 201)) return Net(bst, $"blob 上传 {Why(bst, bbody)}");
            if (JsonNode.Parse(bbody)?["sha"]?.GetValue<string>() != sha)
                return new(PushResult.Other, Reason: $"blob sha 不符：{sha[..8]}");
        }

        // 逐 commit：tree（base_tree 增量）+ commit（保真 author/committer/date）
        string prevCommit = isNew ? plan.Base ?? "" : remoteSha;
        string prevTree = "";
        if (prevCommit.Length > 0)
        {
            // 基点/远端 HEAD 的 tree 必须取自服务端（增量 base_tree 的锚）——取不到即 fail-closed
            var (gst, gbody) = await GitHubApi.SendRawAsync(HttpMethod.Get,
                $"repos/{plan.Repo}/git/commits/{prevCommit}", token, null);
            if (gst != 200)
                return new(PushResult.Other, Reason: $"基点提交 {prevCommit[..8]} 远端不存在（{Why(gst, gbody)}）——增量基点必须是远端已有对象");
            prevTree = JsonNode.Parse(gbody)?["tree"]?["sha"]?.GetValue<string>() ?? "";
        }
        var lastSha = prevCommit;

        foreach (var c in commits)
        {
            var parent = FirstParentOrEmpty(plan.RepoPath, c);
            var entries = new JsonArray();
            foreach (var line in GitLines(plan.RepoPath, $"-c core.quotePath=false diff-tree -r --no-renames --raw {parent} {c}"))
                ParseRaw(line, (path, mode, sha, st2, submodule) =>
                {
                    if (submodule) throw new LocalPlanException($"REST push 不支持 submodule：{path}");
                    // 显式 (JsonNode?) 走非泛型 Add——AOT 禁 JsonArray.Add<T> 的动态 JsonValue 创建
                    entries.Add(st2 == 'D'
                        ? (JsonNode?)new JsonObject { ["path"] = path, ["sha"] = null }
                        : new JsonObject { ["path"] = path, ["mode"] = mode, ["type"] = "blob", ["sha"] = sha });
                });

            var treeSha = prevTree;
            if (entries.Count > 0)
            {
                var treeBody = new JsonObject { ["tree"] = entries };
                if (prevTree.Length > 0) treeBody["base_tree"] = prevTree;
                var (tst, tbody) = await GitHubApi.SendRawAsync(HttpMethod.Post,
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
            var (cst, cbody) = await GitHubApi.SendRawAsync(HttpMethod.Post,
                $"repos/{plan.Repo}/git/commits", token, commitBody.ToJsonString());
            if (cst != 201) return Net(cst, $"commit 创建 {Why(cst, cbody)}");
            lastSha = JsonNode.Parse(cbody)?["sha"]?.GetValue<string>() ?? "";
            prevCommit = lastSha;
            prevTree = treeSha;
        }

        // update-ref：唯一写点；force:false 由服务端再拦一次非 FF
        var (rst, rbody) = isNew
            ? await GitHubApi.SendRawAsync(HttpMethod.Post, $"repos/{plan.Repo}/git/refs", token,
                new JsonObject { ["ref"] = $"refs/heads/{plan.Branch}", ["sha"] = lastSha }.ToJsonString())
            : await GitHubApi.SendRawAsync(HttpMethod.Patch, $"repos/{plan.Repo}/git/refs/heads/{plan.Branch}", token,
                new JsonObject { ["sha"] = lastSha, ["force"] = false }.ToJsonString());
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
        fn(parts[4][(tab + 1)..], parts[1], parts[3], parts[4][..tab][^1], parts[1] == "160000");
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
