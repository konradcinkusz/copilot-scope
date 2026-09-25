using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotScope.Local.Connecting;

internal enum EditResult
{
    /// <summary>The file was written.</summary>
    Written,

    /// <summary>The file already said what it would have been changed to; it was not touched.</summary>
    Unchanged,

    /// <summary>Not strict JSON — comments, trailing commas, or not an object — so left alone.</summary>
    Unparseable,

    /// <summary>There was no file to remove anything from.</summary>
    Missing
}

/// <summary>
/// An assistant's JSON settings file, edited the way a tool should edit someone else's file.
///
///   - Parsed as strict JSON. A file with comments or trailing commas — VS Code accepts both — is
///     left alone and the change printed instead: rewriting it would silently drop them.
///   - Copied to <c>&lt;file&gt;.copilotscope.bak</c> before it is changed.
///   - Written to a temporary file and renamed over the original, so an interrupted write
///     leaves the old file, never half of the new one.
///   - Written through a symbolic link, not over it: dotfile managers link these files into
///     place, and replacing the link with a file would quietly unmanage it.
///   - Line endings and permissions kept; text outside ASCII left readable, not escaped.
///   - Not written at all when nothing would change.
///
/// The same rules as the python merge in <c>scripts/copilotscope</c>, which this replaces for
/// the native binary, plus the last four.
/// </summary>
internal static class SettingsFile
{
    public const string BackupSuffix = ".copilotscope.bak";

    /// <summary>Deep-merges the patch into the file, creating the file if it is absent.</summary>
    public static EditResult Merge(string path, JsonObject patch)
    {
        if (!TryRead(path, out var document, out var newLine)) return EditResult.Unparseable;
        var before = document.ToJsonString();
        MergeInto(document, patch);
        return document.ToJsonString() == before && File.Exists(path)
            ? EditResult.Unchanged
            : Save(path, document, newLine);
    }

    /// <summary>
    /// Removes each key path. A path is a list of segments rather than a dotted string, because
    /// VS Code's keys are flat names that contain dots (<c>github.copilot.chat.otel.enabled</c>).
    /// An <c>env</c> object emptied by the removal goes too: an empty block left behind in
    /// someone's settings is noise.
    /// </summary>
    public static EditResult Remove(string path, IEnumerable<string[]> keyPaths)
    {
        if (!File.Exists(path)) return EditResult.Missing;
        if (!TryRead(path, out var document, out var newLine)) return EditResult.Unparseable;

        var changed = false;
        foreach (var keyPath in keyPaths)
        {
            JsonNode? node = document;
            foreach (var segment in keyPath[..^1]) node = node is JsonObject o ? o[segment] : null;
            if (node is JsonObject parent && parent.Remove(keyPath[^1])) changed = true;
        }
        if (document["env"] is JsonObject { Count: 0 } && changed) document.Remove("env");

        return changed ? Save(path, document, newLine) : EditResult.Unchanged;
    }

    /// <summary>Reads the file as a JSON object: an empty one when the file is missing or blank.</summary>
    public static bool TryRead(string path, out JsonObject document, out string newLine)
    {
        document = [];
        newLine = "\n";
        if (!File.Exists(path)) return true;

        var text = File.ReadAllText(path);
        if (text.Contains("\r\n", StringComparison.Ordinal)) newLine = "\r\n";
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed) return false;
            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject incoming && target[key] is JsonObject existing) MergeInto(existing, incoming);
            else target[key] = value?.DeepClone();
        }
    }

    private static EditResult Save(string path, JsonObject document, string newLine)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = newLine,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        if (File.Exists(path)) File.Copy(path, path + BackupSuffix, overwrite: true);
        TextFiles.WriteAtomically(path, document.ToJsonString(options) + newLine);
        return EditResult.Written;
    }
}

/// <summary>Writing a file someone else owns: atomically, through links, keeping its mode.</summary>
internal static class TextFiles
{
    public static void WriteAtomically(string path, string text)
    {
        var target = ResolveLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

        var temporary = $"{target}.copilotscope.tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(target))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>The file a chain of symbolic links ends at, or the path itself.</summary>
    public static string ResolveLinks(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null) return path;
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }
}
