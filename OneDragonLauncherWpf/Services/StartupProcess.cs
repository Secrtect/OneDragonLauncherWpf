using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace OneDragonLauncherWpf.Services
{
    /// <summary>在执行任何后端代码前加入 Job，启动完成后可解除退出清理策略。</summary>
    internal sealed class StartupProcess : IDisposable
    {
        private SafeFileHandle job;
        private Process process;
        private int disposed;
        public int Id { get { return process.Id; } }
        public Task<int> Completion { get; private set; }

        public StartupProcess(ProcessStartInfo info, Action<string> output)
        {
            SafeFileHandle stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
            SafeFileHandle stdinRead = null, stdinWrite = null;
            IntPtr environment = IntPtr.Zero;
            var native = new ProcessInformation();
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job.IsInvalid) throw new Win32Exception();
                SetKillOnClose(true);
                CreateRedirectPipe(out stdoutRead, out stdoutWrite, true);
                CreateRedirectPipe(out stderrRead, out stderrWrite, true);
                CreateRedirectPipe(out stdinRead, out stdinWrite, false);
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 0x100,
                    Input = stdinRead.DangerousGetHandle(), Output = stdoutWrite.DangerousGetHandle(),
                    Error = stderrWrite.DangerousGetHandle()
                };
                var entries = new List<string>();
                foreach (DictionaryEntry item in info.EnvironmentVariables)
                    entries.Add(item.Key + "=" + item.Value);
                entries.Sort(StringComparer.OrdinalIgnoreCase);
                environment = Marshal.StringToHGlobalUni(string.Join("\0", entries) + "\0\0");
                // CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT.
                var command = new StringBuilder(CommandLine.Join(new[] { info.FileName }) + " " + info.Arguments);
                if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x08000404, environment, info.WorkingDirectory, ref startup, out native))
                    throw new Win32Exception();
                if (!AssignProcessToJobObject(job, native.Process)) throw new Win32Exception();
                process = Process.GetProcessById((int)native.ProcessId);
                // Force the managed wait handle to open before a short-lived child can exit.
                var handle = process.Handle;
                if (ResumeThread(native.Thread) == uint.MaxValue) throw new Win32Exception();

                stdoutWrite.Dispose(); stderrWrite.Dispose();
                stdinRead.Dispose(); stdinWrite.Dispose(); // EOF for hidden input() prompts.
                var stdout = new StreamReader(new FileStream(stdoutRead, FileAccess.Read), Encoding.UTF8);
                stdoutRead = null;
                var stderr = new StreamReader(new FileStream(stderrRead, FileAccess.Read), Encoding.UTF8);
                stderrRead = null;
                Task first = ReadOutput(stdout, output), second = ReadOutput(stderr, output);
                Completion = Task.Run(async () =>
                {
                    process.WaitForExit();
                    int code = process.ExitCode;
                    await Task.WhenAll(first, second).ConfigureAwait(false);
                    return code;
                });
            }
            catch
            {
                // Assignment/resume failures must never leave a suspended or unowned child.
                if (native.Process != IntPtr.Zero) TerminateProcess(native.Process, 1);
                job?.Dispose();
                process?.Dispose();
                throw;
            }
            finally
            {
                stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose();
                stdinRead?.Dispose(); stdinWrite?.Dispose();
                if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
                if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
        }

        private static Task ReadOutput(StreamReader reader, Action<string> output)
        {
            return Task.Run(() =>
            {
                using (reader)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) output(line);
                }
            });
        }

        /// <summary>先清除 KILL_ON_JOB_CLOSE，再关闭句柄；失败时保留保护。</summary>
        public void Release()
        {
            if (job.IsClosed) return;
            SetKillOnClose(false);
            job.Dispose();
        }

        private void SetKillOnClose(bool enabled)
        {
            var limits = new ExtendedLimits();
            limits.Basic.Flags = enabled ? 0x2000u : 0u;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                throw new Win32Exception();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            job?.Dispose();
            // Do not dispose Process while the completion worker is waiting on it.
            if (Completion != null) Completion.ContinueWith(task => process.Dispose(), TaskScheduler.Default);
        }

        private static void CreateRedirectPipe(out SafeFileHandle read, out SafeFileHandle write, bool parentReads)
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            if (!CreatePipe(out read, out write, ref security, 0)) throw new Win32Exception();
            if (!SetHandleInformation(parentReads ? read : write, 1, 0)) throw new Win32Exception();
        }

        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            public int Size; public string Reserved, Desktop, Title;
            public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessTime, JobTime; public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
            public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic; public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
