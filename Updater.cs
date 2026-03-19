using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aimmy.Mac
{
    public static class Updater
    {
        private const string RepoOwner = "fbedev";
        private const string RepoName = "aimmy";
        private const string Branch = "main";

        public enum UpdateState { Idle, Checking, UpdateAvailable, NoUpdate, Updating, Success, Failed }

        public static UpdateState State { get; private set; } = UpdateState.Idle;
        public static string StatusMessage { get; private set; } = "";
        public static string LatestCommitMessage { get; private set; } = "";
        public static string LocalCommit { get; private set; } = "";
        public static string RemoteCommit { get; private set; } = "";

        private static readonly HttpClient _http = new HttpClient();

        static Updater()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Aimmy-Mac/1.0");
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public static void CheckForUpdates()
        {
            if (State == UpdateState.Checking || State == UpdateState.Updating) return;

            State = UpdateState.Checking;
            StatusMessage = "Checking for updates...";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Get local commit hash
                    LocalCommit = RunGit("rev-parse HEAD").Trim();
                    if (string.IsNullOrEmpty(LocalCommit))
                    {
                        State = UpdateState.Failed;
                        StatusMessage = "Not a git repository";
                        return;
                    }

                    // Fetch remote
                    RunGit("fetch origin " + Branch);

                    // Get remote commit hash
                    RemoteCommit = RunGit($"rev-parse origin/{Branch}").Trim();

                    if (string.IsNullOrEmpty(RemoteCommit))
                    {
                        State = UpdateState.Failed;
                        StatusMessage = "Could not reach remote";
                        return;
                    }

                    if (LocalCommit == RemoteCommit)
                    {
                        State = UpdateState.NoUpdate;
                        StatusMessage = "Up to date";
                    }
                    else
                    {
                        // Get commit count behind
                        string countStr = RunGit($"rev-list HEAD..origin/{Branch} --count").Trim();
                        int behind = int.TryParse(countStr, out int c) ? c : 0;

                        // Get latest commit message
                        LatestCommitMessage = RunGit($"log origin/{Branch} -1 --pretty=%s").Trim();

                        State = UpdateState.UpdateAvailable;
                        StatusMessage = $"{behind} new commit{(behind != 1 ? "s" : "")} available";
                    }
                }
                catch (Exception ex)
                {
                    State = UpdateState.Failed;
                    StatusMessage = $"Check failed: {ex.Message}";
                }
            });
        }

        public static void ApplyUpdate()
        {
            if (State != UpdateState.UpdateAvailable) return;

            State = UpdateState.Updating;
            StatusMessage = "Updating...";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Stash any local changes
                    string status = RunGit("status --porcelain").Trim();
                    bool hadChanges = !string.IsNullOrEmpty(status);
                    if (hadChanges)
                    {
                        RunGit("stash push -m \"auto-stash before update\"");
                    }

                    // Pull
                    string pullResult = RunGit($"pull origin {Branch}");

                    // Restore stashed changes
                    if (hadChanges)
                    {
                        RunGit("stash pop");
                    }

                    // Restore packages and rebuild
                    StatusMessage = "Rebuilding...";
                    RunShell("dotnet restore");
                    RunShell("dotnet build -c Release --no-restore");

                    State = UpdateState.Success;
                    StatusMessage = "Updated! Restart to apply.";
                }
                catch (Exception ex)
                {
                    State = UpdateState.Failed;
                    StatusMessage = $"Update failed: {ex.Message}";
                }
            });
        }

        private static string RunGit(string args)
        {
            return RunShell($"git {args}");
        }

        private static string RunShell(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            // Walk up to find the git repo root (we might be in bin/Release/net8.0)
            string dir = psi.WorkingDirectory;
            for (int i = 0; i < 5; i++)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    psi.WorkingDirectory = dir;
                    break;
                }
                string? parent = Directory.GetParent(dir)?.FullName;
                if (parent == null) break;
                dir = parent;
            }

            using var p = Process.Start(psi);
            if (p == null) return "";

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            return output;
        }
    }
}
