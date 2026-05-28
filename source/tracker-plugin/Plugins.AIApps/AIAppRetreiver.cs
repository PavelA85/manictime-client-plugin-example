using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Finkit.ManicTime.Shared.DocumentTracking;
using Finkit.ManicTime.Shared.Helpers;
using ManicTime.Client.Tracker.EventTracking.Publishers.ApplicationTracking;

namespace Plugins.AIApps
{
    public class AIAppRetreiver : IDocumentRetreiver
    {
        private static readonly string[] AIProcesses = new[]
        {
            "cursor", "claude", "chatgpt", "codex", "antigravity", "lm studio", "lmstudio", "anythingllm", "jan", "code", "vscodium"
        };

        public DocumentInfo GetDocument(ApplicationInfo application)
        {
            if (string.IsNullOrEmpty(application.ProcessName))
                return null;

            var processName = application.ProcessName.ToLowerInvariant();
            if (!AIProcesses.Contains(processName))
                return null;

            var title = application.WindowTitle ?? "";

            string documentName = null;
            string groupName = null;
            string documentType = DocumentTypes.File;

            // 1. Process specific handlers
            if (processName == "cursor" || processName == "code" || processName == "vscodium")
            {
                // Format: [● ]file.txt - project - Cursor / VS Code
                // or just: project - Cursor / VS Code
                var appSuffix = processName == "cursor" ? "Cursor" : (processName == "code" ? "Visual Studio Code" : "VSCodium");

                // Regex matches: (optional bullet) (filename) - (project) - (appSuffix)
                var cursorRegex = new Regex($@"^(?:●\s*)?([^\-]+)\s*-\s*([^\-]+)\s*-\s*{appSuffix}$", RegexOptions.IgnoreCase);
                var match = cursorRegex.Match(title);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = match.Groups[2].Value.Trim();
                }
                else
                {
                    // Fallback: [project] - Cursor
                    var fallbackRegex = new Regex($@"^([^\-]+)\s*-\s*{appSuffix}$", RegexOptions.IgnoreCase);
                    var fallbackMatch = fallbackRegex.Match(title);
                    if (fallbackMatch.Success)
                    {
                        documentName = "Editor Workspace";
                        groupName = fallbackMatch.Groups[1].Value.Trim();
                    }
                    else
                    {
                        documentName = "Workspace";
                        groupName = FirstCharToUpper(processName);
                    }
                }
            }
            else if (processName == "claude")
            {
                // Format: Chat Title - Claude
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*Claude$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = "Claude AI";
                }
                else
                {
                    // Try UI Automation fallback if title is just "Claude"
                    if (title.EqualsIgnoringCase("Claude") && application.WindowHandle != IntPtr.Zero)
                    {
                        var chatFromUI = TryGetActiveElementText(application.WindowHandle);
                        if (!string.IsNullOrEmpty(chatFromUI))
                        {
                            documentName = chatFromUI;
                            groupName = "Claude AI";
                        }
                    }

                    if (string.IsNullOrEmpty(documentName))
                    {
                        documentName = "Active Chat";
                        groupName = "Claude AI";
                    }
                }
            }
            else if (processName == "chatgpt")
            {
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*ChatGPT$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = "ChatGPT AI";
                }
                else
                {
                    documentName = "Active Chat";
                    groupName = "ChatGPT AI";
                }
            }
            else if (processName == "lmstudio" || processName == "lm studio")
            {
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*LM Studio$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = "LM Studio";
                }
                else
                {
                    documentName = "Local Model Chat";
                    groupName = "LM Studio";
                }
            }
            else if (processName == "anythingllm")
            {
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*AnythingLLM$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = "AnythingLLM";
                }
            }
            else if (processName == "jan")
            {
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*Jan$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = "Jan AI";
                }
            }
            else
            {
                // Codex, Antigravity, or other generic apps
                // E.g. [Title] - [App] or [Title] | [App]
                var match = Regex.Match(title, @"^(.*?)\s*[-|]\s*([^-|]+)$");
                if (match.Success)
                {
                    documentName = match.Groups[1].Value.Trim();
                    groupName = match.Groups[2].Value.Trim();
                }
                else
                {
                    documentName = title;
                    groupName = FirstCharToUpper(processName);
                }
            }

            if (string.IsNullOrEmpty(documentName))
                documentName = title;
            if (string.IsNullOrEmpty(groupName))
                groupName = FirstCharToUpper(processName);

            return new DocumentInfo()
            {
                DocumentName = documentName,
                DocumentGroupName = groupName,
                DocumentType = documentType
            };
        }

        private static string FirstCharToUpper(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            return input.First().ToString().ToUpper() + input.Substring(1);
        }

        private string TryGetActiveElementText(IntPtr hwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                // Fast scan of top-level child elements or focused elements
                // Electron apps expose web content under a Document role
                var docCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
                var doc = root.FindFirst(TreeScope.Children, docCond) ?? root.FindFirst(TreeScope.Descendants, docCond);
                if (doc != null)
                {
                    // Find headings or list items or active text elements in the viewport
                    var headingCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header);
                    var header = doc.FindFirst(TreeScope.Descendants, headingCond);
                    if (header != null && !string.IsNullOrEmpty(header.Current.Name))
                    {
                        return header.Current.Name;
                    }
                }
            }
            catch
            {
                // Fallback silently to avoid any UI block
            }
            return null;
        }
    }
}
