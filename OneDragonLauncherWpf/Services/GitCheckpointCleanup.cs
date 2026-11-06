using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OneDragonLauncherWpf.Services
{
    /// <summary>在业务启动前尽力清理会干扰 libgit2 本地导入的 Codex tree 检查点。</summary>
    public static class GitCheckpointCleanup
    {
        private const string Prefix = "refs/codex/turn-diffs/checkpoints/";
        private const int TimeoutMilliseconds = 10000;

        /// <summary>
        /// 使用项目 MinGit 或 PATH 中的 Git 清理直接指向 tree 的检查点，不备份。
        /// 没有 Git 时跳过；检查或清理失败时记录原因并允许后续业务继续。
        /// </summary>
        public static async Task TryCleanAsync(string projectDirectory, LauncherLog log)
        {
            try
            {
                // 没有自己的 .git 时不向上查找，避免清理父目录的仓库。
                string metadata = Path.Combine(projectDirectory, ".git");
                if (!Directory.Exists(metadata) && !File.Exists(metadata)) return;
                string git = FindGit(projectDirectory, Environment.GetEnvironmentVariable("PATH"));
                if (git == null)
                {
                    log.Write("[Git 检查] 未找到本地 Git，跳过 Codex 快照清理。");
                    return;
                }

                log.Write("[Git 检查] 检查 Codex tree 快照引用，使用: " + git);
                string output = await RunGitAsync(git, projectDirectory, new[] {
                    "for-each-ref", "--format=%(objecttype)%09%(objectname)%09%(refname)%09%(symref)", Prefix
                }).ConfigureAwait(false);
                var targets = new List<string[]>();
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.Split('\t');
                    if (fields.Length != 4 || fields[0] != "tree" || fields[3].Length != 0 ||
                        !fields[2].StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    if (!Regex.IsMatch(fields[1], @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z") ||
                        fields[2].Any(char.IsWhiteSpace))
                        throw new InvalidOperationException("Git 返回了无法识别的检查点引用。");
                    targets.Add(fields);
                }
                if (targets.Count == 0)
                {
                    log.Write("[Git 检查] 没有需要清理的 Codex tree 快照引用。");
                    return;
                }

                // 同一事务校验旧对象 ID 后删除；锁冲突或引用变化时整批失败，不能盲删新引用。
                var commands = new StringBuilder("start\n");
                foreach (string[] target in targets)
                    commands.Append("delete ").Append(target[2]).Append(' ').Append(target[1]).Append('\n');
                commands.Append("prepare\ncommit\n");
                await RunGitAsync(git, projectDirectory, new[] { "update-ref", "--no-deref", "--stdin" },
                    commands.ToString()).ConfigureAwait(false);
                log.Write("[Git 检查] 已清理 " + targets.Count + " 个 Codex tree 快照引用（未备份）。");
            }
            catch (Exception exception)
            {
                log.Write("[WARNING] Codex 快照检查或清理未完成，继续原有启动流程: " + exception.Message);
            }
        }

        /// <summary>优先使用项目自带 MinGit，再检查 PATH 中已有的 Git，不下载工具。</summary>
        internal static string FindGit(string projectDirectory, string searchPath)
        {
            string bundled = Path.Combine(projectDirectory, ".install", "MinGit", "cmd", "git.exe");
            if (File.Exists(bundled)) return bundled;
            foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                    if (!Path.IsPathRooted(directory)) continue;
                    string candidate = Path.Combine(directory, "git.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return null;
        }

        /// <summary>隐藏执行本地 Git，隔离外部仓库环境变量，并限制命令及管道等待时间。</summary>
        private static async Task<string> RunGitAsync(string git, string directory, string[] arguments, string input = null)
        {
            var info = new ProcessStartInfo(git, CommandLine.Join(new[] { "-C", directory }.Concat(arguments)))
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // 防止从 IDE 或终端继承的 GIT_DIR、GIT_NAMESPACE 等将清理指向其他仓库。
            foreach (string key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) info.EnvironmentVariables.Remove(key);
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new InvalidOperationException("无法启动本地 Git。");
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task<string> completion = Task.Run(async () =>
                {
                    if (input != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(input);
                        await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    }
                    process.StandardInput.Close();
                    process.WaitForExit();
                    string stdout = await output.ConfigureAwait(false);
                    string stderr = await error.ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("Git " + arguments[0] + " 失败（" + process.ExitCode + "）: " + stderr.Trim());
                    return stdout;
                });
                if (await Task.WhenAny(completion, Task.Delay(TimeoutMilliseconds)).ConfigureAwait(false) != completion)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    await Task.WhenAny(completion, Task.Delay(1000)).ConfigureAwait(false);
                    // 超时后不再阻塞业务启动，观察异步管道关闭可能产生的异常。
                    _ = completion.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("Git " + arguments[0] + " 超过 10 秒未完成。");
                }
                return await completion.ConfigureAwait(false);
            }
        }
    }
}
