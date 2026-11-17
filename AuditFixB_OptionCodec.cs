using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-024/025: Altes Format nur im verlustfreien Wertebereich schreiben.
    // Das erweiterte Format hat eine Kennung, volle Int32-Werte und eine feste Datensatzanzahl.
    internal static class AuditFixB_OptionCodec {
        private const int Magic = 0x32535480; // UTS2
        internal static bool Extended(byte[] bytes) => bytes != null && bytes.Length >= 4
            && bytes[0] == 0x80 && bytes[1] == 0x54 && bytes[2] == 0x53 && bytes[3] == 0x32;

        internal static int PackedSize(uint value) {
            int size = 1;
            while ((value >>= 7) != 0) size++;
            return size;
        }

        internal static byte[] Encode(IEnumerable<(int Id, int Selection)> values) {
            var rows = values.OrderBy(v => v.Id).ToArray();
            if (rows.Any(v => v.Id <= 0 || v.Selection < 0)
                || rows.Select(v => v.Id).Distinct().Count() != rows.Length)
                throw new InvalidDataException("Ungültige oder doppelte Options-ID");
            bool extended = rows.Any(v => v.Id > ushort.MaxValue || v.Selection >= 128);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            if (extended) { writer.Write(Magic); writer.Write(rows.Length); }
            int lastId = -1;
            foreach (var row in rows) {
                if (extended) { writer.Write(row.Id); writer.Write(row.Selection); }
                else {
                    bool consecutive = row.Id == lastId + 1;
                    writer.Write((byte)(row.Selection + (consecutive ? 128 : 0)));
                    if (!consecutive) writer.Write((ushort)row.Id);
                }
                lastId = row.Id;
            }
            return stream.ToArray();
        }

        internal static List<(int Id, int Selection)> Decode(byte[] bytes) {
            if (bytes == null || bytes.Length > 1024 * 1024)
                throw new InvalidDataException("Ungültige Clipboard-Länge");
            using var reader = new BinaryReader(new MemoryStream(bytes));
            var rows = new List<(int Id, int Selection)>();
            var ids = new HashSet<int>();
            bool extended = Extended(bytes);
            if (extended) {
                reader.ReadInt32();
                int count = reader.ReadInt32();
                if (count < 0 || (long)count * 8 != bytes.Length - 8)
                    throw new InvalidDataException("Unvollständiges Clipboard");
            }
            int lastId = -1;
            while (reader.BaseStream.Position < bytes.Length) {
                int id, selection;
                if (extended) { id = reader.ReadInt32(); selection = reader.ReadInt32(); }
                else {
                    int encoded = reader.ReadByte();
                    selection = encoded & 127;
                    id = (encoded & 128) != 0 ? checked(lastId + 1) : reader.ReadUInt16();
                }
                lastId = id;
                if (id <= 0 || selection < 0 || !ids.Add(id))
                    throw new InvalidDataException("Ungültige oder doppelte Options-ID");
                rows.Add((id, selection));
            }
            return rows;
        }
    }
}
