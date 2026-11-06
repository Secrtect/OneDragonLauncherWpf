using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OneDragonLauncherWpf.Services;

/// <summary>使用隔离的真实 Git 仓库检查清理边界，不访问或更新实际一条龙项目。</summary>
internal static class GitCheckpointCleanupTests
{
    private const string Prefix = "refs/codex/turn-diffs/checkpoints/";

    internal static void Run(Action<string, Action> check, string temporary)
    {
        string git = GitCheckpointCleanup.FindGit(temporary, Environment.GetEnvironmentVariable("PATH"));
        if (git == null) throw new Exception("Git 清理集成测试需要 PATH 中已有 Git。");
        string project = Path.Combine(temporary, "Git cleanup 中文 project");
        Directory.CreateDirectory(project);
        Git(git, project, "init", "-b", "main");
        File.WriteAllText(Path.Combine(project, "tracked.txt"), "original");
        Git(git, project, "add", "tracked.txt");
        Git(git, project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        string head = Git(git, project, "rev-parse", "HEAD").Trim();
        string tree = Git(git, project, "rev-parse", "HEAD^{tree}").Trim();
        string packed = Prefix + "packed";
        string loose = Prefix + "loose-中文";
        Git(git, project, "update-ref", packed, tree);
        Git(git, project, "update-ref", Prefix + "keep-commit", head);
        Git(git, project, "update-ref", "refs/other/keep-tree", tree);
        Git(git, project, "update-ref", "refs/codex/other/keep-tree", tree);
        Git(git, project, "symbolic-ref", Prefix + "keep-symbolic", "refs/other/keep-tree");
        Git(git, project, "tag", "keep-tag");
        Git(git, project, "pack-refs", "--all", "--prune");
        Git(git, project, "update-ref", loose, tree);
        File.WriteAllText(Path.Combine(project, "tracked.txt"), "local changes");
        File.WriteAllText(Path.Combine(project, "untracked.txt"), "keep me");
        string beforeStatus = Git(git, project, "status", "--porcelain");
        string beforeRefs = Git(git, project, "for-each-ref", "--format=%(objectname) %(refname) %(symref)");

        check("Git 清理：锁冲突时整批保留并记录失败，业务可继续", () =>
        {
            string lockPath = Path.Combine(project, ".git", "refs", "codex", "turn-diffs", "checkpoints", "loose-中文.lock");
            File.WriteAllText(lockPath, "fixture lock");
            try
            {
                var log = new LauncherLog();
                GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
                Require(log.Snapshot().Contains("继续原有启动流程") && !log.Snapshot().Contains("已清理"));
                Require(Git(git, project, "for-each-ref", "--format=%(objectname) %(refname) %(symref)") == beforeRefs);
            }
            finally { File.Delete(lockPath); }
        });

        check("Git 清理：删除松散和 packed tree，保留其他引用及工作文件", () =>
        {
            // 模拟 IDE 注入另一个仓库位置；清理必须仍发生在传入的项目中。
            string oldDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
            string oldNamespace = Environment.GetEnvironmentVariable("GIT_NAMESPACE");
            try
            {
                Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(temporary, "wrong.git"));
                Environment.SetEnvironmentVariable("GIT_NAMESPACE", "wrong");
                var log = new LauncherLog();
                GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
                Require(log.Snapshot().Contains("已清理 2 个") && !log.Snapshot().Contains("WARNING"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_DIR", oldDirectory);
                Environment.SetEnvironmentVariable("GIT_NAMESPACE", oldNamespace);
            }
            string[] expected = Lines(beforeRefs).Where(line => !line.Contains(" " + packed + " ") && !line.Contains(" " + loose + " ")).ToArray();
            Require(Lines(Git(git, project, "for-each-ref", "--format=%(objectname) %(refname) %(symref)")).SequenceEqual(expected));
            Require(Git(git, project, "status", "--porcelain") == beforeStatus);
            Require(File.ReadAllText(Path.Combine(project, "tracked.txt")) == "local changes");
            Require(File.ReadAllText(Path.Combine(project, "untracked.txt")) == "keep me");
            Require(Git(git, project, "rev-parse", "HEAD").Trim() == head);
        });

        check("Git 清理：重复启动无需清理，项目无 .git 不触碰父仓库", () =>
        {
            var log = new LauncherLog();
            GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
            Require(log.Snapshot().Contains("没有需要清理"));
            Git(git, project, "update-ref", packed, tree);
            string child = Path.Combine(project, "child-project");
            Directory.CreateDirectory(child);
            GitCheckpointCleanup.TryCleanAsync(child, new LauncherLog()).GetAwaiter().GetResult();
            Require(Git(git, project, "rev-parse", packed).Trim() == tree);
        });

        check("Git 查找：无 Git 跳过，项目 MinGit 优先于 PATH", () =>
        {
            string oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "");
                var log = new LauncherLog();
                GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
                Require(log.Snapshot().Contains("未找到本地 Git"));
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
            Require(Git(git, project, "rev-parse", packed).Trim() == tree);
            string fakeGit = Path.Combine(project, ".install", "MinGit", "cmd", "git.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fakeGit));
            File.Copy(typeof(GitCheckpointCleanupTests).Assembly.Location, fakeGit);
            Require(GitCheckpointCleanup.FindGit(project, oldPath) == fakeGit);
        });

        check("Git 清理：命令出错记录原因并继续，不删除引用", () =>
        {
            File.WriteAllText(Path.Combine(project, "fake-git-mode"), "error");
            var log = new LauncherLog();
            GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
            Require(log.Snapshot().Contains("simulated git failure") && log.Snapshot().Contains("继续原有启动流程"));
            Require(Git(git, project, "rev-parse", packed).Trim() == tree);
        });

        check("Git 清理：命令超时后继续，不无限等待", () =>
        {
            File.WriteAllText(Path.Combine(project, "fake-git-mode"), "timeout");
            var log = new LauncherLog();
            var watch = Stopwatch.StartNew();
            GitCheckpointCleanup.TryCleanAsync(project, log).GetAwaiter().GetResult();
            Require(watch.Elapsed.TotalSeconds < 20 && log.Snapshot().Contains("超过 10 秒"));
            Require(Git(git, project, "rev-parse", packed).Trim() == tree);
        });
    }

    private static string Git(string git, string project, params string[] arguments)
    {
        var info = new ProcessStartInfo(git, CommandLine.Join(new[] { "-C", project }.Concat(arguments)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(info))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("测试 Git 命令超时"); }
            if (process.ExitCode != 0) throw new Exception(error.Result);
            return output.Result;
        }
    }

    private static string[] Lines(string value) { return value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); }
    private static void Require(bool value) { if (!value) throw new Exception("Git cleanup assertion failed"); }
}
