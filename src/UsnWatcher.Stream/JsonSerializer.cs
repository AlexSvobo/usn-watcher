using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsnWatcher.Core;

namespace UsnWatcher.Stream
{
    public static class JsonSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static void WriteNdjson(UsnRecord record, TextWriter writer)
        {
            if (record is null) return;

            var obj = new
            {
                usn = record.Usn,
                timestamp = record.Timestamp.ToUniversalTime().ToString("o"),
                fileReferenceNumber = ToHex(record.FileReferenceNumber),
                parentReferenceNumber = ToHex(record.ParentFileReferenceNumber),
                fileName = record.FileName,
                fullPath = record.FullPath,
                    oldPath = record.OldPath,
                    newPath = record.NewPath,
                reason = record.Reasons,
                reasonRaw = record.ReasonRaw,
                isDirectory = record.IsDirectory,
                attributes = GetAttributes(record.FileAttributes)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(obj, Options);
            writer.WriteLine(json);
            try { writer.Flush(); } catch { }
        }

        private static string ToHex(ulong value) => $"0x{value:x16}";

        private static string[] GetAttributes(uint mask)
        {
            var list = new List<string>();
            try
            {
                var fa = (System.IO.FileAttributes)mask;
                foreach (System.IO.FileAttributes flag in Enum.GetValues(typeof(System.IO.FileAttributes)))
                {
                    if (flag == 0) continue;
                    if ((fa & flag) == flag) list.Add(flag.ToString());
                }
            }
            catch
            {
                // ignore and return empty
            }

            return list.ToArray();
        }
    }
}
