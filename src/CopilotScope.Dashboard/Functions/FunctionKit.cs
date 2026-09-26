using System.IO.Compression;
using System.Text;

namespace CopilotScope.Dashboard.Functions;

/// <summary>
/// A function's files as one zip, for an assistant this dashboard cannot start: VS Code Copilot Chat,
/// or any assistant at all behind a Compose deployment, which has no launcher (ADR-005, decision 8).
/// The same files the native binary writes into a run's directory, under one folder.
/// </summary>
public static class FunctionKit
{
    public static string FolderName(AssistantFunction function, DateTimeOffset at) =>
        $"copilotscope-{function.Id}-{at:yyyyMMdd-HHmm}";

    public static byte[] Zip(string folder, IReadOnlyList<WorkspaceFile> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry($"{folder}/{file.Path}", CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }
}
