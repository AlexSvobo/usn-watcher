using System;
using System.IO;
using System.Text.Json;

namespace UsnWatcher.Core
{
    public sealed record CursorRecord(string Volume, ulong JournalId, long NextUsn, DateTime SavedAt);

    public static class CursorStore
    {
        private const string SubFolder = "usn-watcher";
        private const string FileName = "cursor.json";

        private static string GetDir()
        {
            var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(app, SubFolder);
        }

        private static string GetPath()
        {
            return Path.Combine(GetDir(), FileName);
        }

        public static void Save(string volume, ulong journalId, long nextUsn)
        {
            try
            {
                var dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var dto = new
                {
                    volume = volume,
                    journalId = $"0x{journalId:X16}",
                    nextUsn = nextUsn,
                    savedAt = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPath(), json);
            }
            catch
            {
                // Swallow any IO errors; cursor persistence is best-effort
            }
        }

        public static CursorRecord? Load(string volume)
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path)) return null;

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("volume", out var volEl)) return null;
                if (!string.Equals(volEl.GetString(), volume, StringComparison.OrdinalIgnoreCase)) return null;

                if (!root.TryGetProperty("journalId", out var jidEl)) return null;
                var jidStr = jidEl.GetString() ?? string.Empty;
                if (jidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) jidStr = jidStr.Substring(2);
                if (!ulong.TryParse(jidStr, System.Globalization.NumberStyles.HexNumber, null, out var journalId)) return null;

                if (!root.TryGetProperty("nextUsn", out var nsEl)) return null;
                var nextUsn = nsEl.GetInt64();

                DateTime savedAt = DateTime.UtcNow;
                if (root.TryGetProperty("savedAt", out var saEl) && saEl.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(saEl.GetString(), out var parsed)) savedAt = parsed.ToUniversalTime();
                }

                return new CursorRecord(volume, journalId, nextUsn, savedAt);
            }
            catch
            {
                return null; // Corrupt or unreadable — treat as missing
            }
        }
    }
}
