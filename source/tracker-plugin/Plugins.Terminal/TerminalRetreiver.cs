using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Finkit.ManicTime.Shared.DocumentTracking;
using Finkit.ManicTime.Shared.Helpers;
using ManicTime.Client.Tracker.EventTracking.Publishers.ApplicationTracking;

namespace Plugins.Terminal
{
    public class TerminalRetreiver : IDocumentRetreiver
    {
        private static readonly string[] TerminalProcesses = new[]
        {
            "windowsterminal", "powershell", "pwsh", "cmd", "conhost", "bash", "wsl", "wslhost", "wt", "mintty", "tsh"
        };

        private static readonly string[] ShellProcesses = new[]
        {
            "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "wsl.exe", "wslhost.exe", "conhost.exe", "openconsole.exe", "init", "sh.exe", "sh", "wsl-keepalive"
        };

        public DocumentInfo GetDocument(ApplicationInfo application)
        {
            if (!IsTerminalProcess(application.ProcessName))
                return null;

            if (!application.ProcessId.HasValue)
                return null;

            // 1. Try to find active command running in terminal
            var activeCommand = FindActiveCommand(application.ProcessId.Value);

            // 2. Try to parse path (CWD) from window title
            var path = ExtractPathFromTitle(application.WindowTitle);

            string docName = "Idle Terminal";
            string groupName = "Terminals";

            if (activeCommand != null)
            {
                docName = activeCommand.CommandLine;
                groupName = activeCommand.Name;
            }
            else if (!string.IsNullOrEmpty(path))
            {
                docName = "Idle Terminal";
                groupName = path;
            }

            // If we have both active command and CWD, merge them beautifully
            if (activeCommand != null && !string.IsNullOrEmpty(path))
            {
                var folderName = System.IO.Path.GetFileName(path);
                if (string.IsNullOrEmpty(folderName))
                    folderName = path;

                // DocumentName represents the detailed command executed
                docName = $"{activeCommand.Name}: {activeCommand.Arguments}";
                
                // DocumentGroupName represents the directory to group this activity
                groupName = path;
            }

            return new DocumentInfo()
            {
                DocumentName = docName,
                DocumentGroupName = groupName,
                DocumentType = DocumentTypes.File
            };
        }

        private bool IsTerminalProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;
            var lower = processName.ToLowerInvariant();
            return TerminalProcesses.Contains(lower);
        }

        private class CommandInfo
        {
            public string Name { get; set; }
            public string CommandLine { get; set; }
            public string Arguments { get; set; }
        }

        private CommandInfo FindActiveCommand(int terminalPid)
        {
            try
            {
                var processes = new List<WmiProcess>();
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        var ppid = Convert.ToInt32(obj["ParentProcessId"]);
                        var name = obj["Name"]?.ToString() ?? "";
                        var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                        processes.Add(new WmiProcess { ProcessId = pid, ParentProcessId = ppid, Name = name, CommandLine = cmdLine });
                    }
                }

                var descendants = new List<WmiProcess>();
                GetDescendants(terminalPid, processes, descendants);

                // Leaf nodes (no children among running processes)
                var leafNodes = descendants.Where(d => !processes.Any(p => p.ParentProcessId == d.ProcessId)).ToList();

                // Filter out standard shell/utility wrappers
                var activeCommands = leafNodes
                    .Where(l => !ShellProcesses.Contains(l.Name.ToLowerInvariant()))
                    .ToList();

                if (activeCommands.Count > 0)
                {
                    var bestCommand = activeCommands[0];
                    var name = System.IO.Path.GetFileNameWithoutExtension(bestCommand.Name);

                    var cmdLine = bestCommand.CommandLine;
                    var args = "";
                    if (!string.IsNullOrEmpty(cmdLine))
                    {
                        args = ExtractArguments(cmdLine, bestCommand.Name);
                    }

                    // Special handling for node/python executions (like claud-code, gemini-cli)
                    if ((name.EqualsIgnoringCase("node") || name.EqualsIgnoringCase("python") || name.EqualsIgnoringCase("py") || name.EqualsIgnoringCase("npx")) && !string.IsNullOrEmpty(args))
                    {
                        // Check if the argument is a script name like claud-code, gemini, agy
                        var firstArg = args.Split(' ').FirstOrDefault()?.Trim('"', '\'');
                        if (!string.IsNullOrEmpty(firstArg))
                        {
                            var argName = System.IO.Path.GetFileNameWithoutExtension(firstArg);
                            if (argName.EqualsIgnoringCase("index") || argName.EqualsIgnoringCase("main"))
                            {
                                // Look up parent folder to find tool name
                                try
                                {
                                    var parentDir = System.IO.Path.GetDirectoryName(firstArg);
                                    if (!string.IsNullOrEmpty(parentDir))
                                    {
                                        var parentDirName = System.IO.Path.GetFileName(parentDir);
                                        if (!string.IsNullOrEmpty(parentDirName))
                                            name = parentDirName;
                                    }
                                }
                                catch {}
                            }
                            else if (argName.Length > 2)
                            {
                                name = argName;
                            }
                        }
                    }

                    return new CommandInfo
                    {
                        Name = name,
                        CommandLine = cmdLine,
                        Arguments = args
                    };
                }
            }
            catch
            {
                // Safety first - do not interrupt tracking
            }
            return null;
        }

        private static string ExtractArguments(string commandLine, string processName)
        {
            if (string.IsNullOrEmpty(commandLine))
                return "";

            var trimmed = commandLine.Trim();
            if (trimmed.StartsWith("\""))
            {
                var nextQuote = trimmed.IndexOf("\"", 1);
                if (nextQuote > 0 && nextQuote < trimmed.Length - 1)
                {
                    return trimmed.Substring(nextQuote + 1).Trim();
                }
            }
            else
            {
                var space = trimmed.IndexOf(" ");
                if (space > 0)
                {
                    return trimmed.Substring(space + 1).Trim();
                }
            }
            return trimmed;
        }

        private static void GetDescendants(int pid, List<WmiProcess> allProcesses, List<WmiProcess> results)
        {
            var children = allProcesses.Where(p => p.ParentProcessId == pid).ToList();
            foreach (var child in children)
            {
                results.Add(child);
                GetDescendants(child.ProcessId, allProcesses, results);
            }
        }

        private class WmiProcess
        {
            public int ProcessId { get; set; }
            public int ParentProcessId { get; set; }
            public string Name { get; set; }
            public string CommandLine { get; set; }
        }

        private static readonly System.Text.RegularExpressions.Regex WindowsPathRegex = new System.Text.RegularExpressions.Regex(
            @"[A-Za-z]:\\[^:?""*<>|]+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex UnixPathRegex = new System.Text.RegularExpressions.Regex(
            @"(/(?:[a-zA-Z0-9_\-\.]+))+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private string ExtractPathFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return null;

            // 1. Check for absolute Windows path
            var winMatch = WindowsPathRegex.Match(title);
            if (winMatch.Success)
            {
                var rawPath = winMatch.Value.Trim();
                
                // Refine path by stripping typical window title suffixes
                var delimiters = new[] { " - ", " : ", " | ", " (", " [", " <" };
                foreach (var delimiter in delimiters)
                {
                    var idx = rawPath.IndexOf(delimiter);
                    if (idx > 0)
                    {
                        rawPath = rawPath.Substring(0, idx).Trim();
                    }
                }

                if (System.IO.Directory.Exists(rawPath) || System.IO.File.Exists(rawPath))
                    return rawPath;

                // Progressively climb parent folders to find first valid directory
                var current = rawPath;
                while (!string.IsNullOrEmpty(current))
                {
                    try
                    {
                        if (System.IO.Directory.Exists(current))
                            return current;
                        var parent = System.IO.Path.GetDirectoryName(current);
                        if (parent == current || string.IsNullOrEmpty(parent))
                            break;
                        current = parent;
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            // 2. Check for Unix style path (e.g. Git Bash '/c/Projects/...')
            var unixMatch = UnixPathRegex.Match(title);
            if (unixMatch.Success)
            {
                var rawPath = unixMatch.Value.Trim();
                if (rawPath.StartsWith("/c/", StringComparison.OrdinalIgnoreCase) && rawPath.Length >= 4)
                {
                    var mappedPath = "C:\\" + rawPath.Substring(3).Replace('/', '\\');
                    if (System.IO.Directory.Exists(mappedPath) || System.IO.File.Exists(mappedPath))
                        return mappedPath;
                }
            }

            return null;
        }
    }
}
